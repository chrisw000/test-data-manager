using System.Globalization;
using Bogus;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using Tdm.Core.Conversion;
using Tdm.Core.Execution;
using Tdm.Core.Naming;
using Tdm.Core.Settings;
using Tdm.EfCore.Bulk;
using Tdm.EfCore.Fakers;
using Tdm.EfCore.Querying;
using Tdm.EfCore.Repositories;

namespace Tdm.EfCore;

/// <summary>Build-time binding of one entity to its context, EF metadata, repository and faker.</summary>
internal sealed class EntityBinding
{
    public required EntityDescriptor Descriptor { get; init; }
    /// <summary>Null for assembly-scan-fallback entities that are not mapped in any context model.</summary>
    public Type? ContextType { get; init; }
    public Microsoft.EntityFrameworkCore.Metadata.IEntityType? EfType { get; init; }
    public RepositoryBinding? Repository { get; init; }
    /// <summary>Reporting only — verification reads go through the DbContext (ADR-0001).</summary>
    public ReadRepositoryInfo? ReadRepository { get; init; }
    public FakerBinding? Faker { get; init; }
    /// <summary>Key + FK columns + navigations — never auto-faked.</summary>
    public required IReadOnlySet<string> AutoFakerSkip { get; init; }
}

/// <summary>
/// The build-once, immutable half of a domain (W3-D2): EF model output, entity/repository/
/// faker bindings, warnings and policy findings — everything expensive, shared by all
/// sessions. Schema creation happens exactly once across sessions.
/// </summary>
internal sealed class DomainRuntimeCore(DomainSettings settings, IReadOnlyList<Assembly> assemblies,
    IReadOnlyList<Type> contextTypes, List<EntityBinding> bindings,
    List<string> warnings, List<string> policyViolations, ILogger log)
{
    public DomainSettings Settings { get; } = settings;
    public IReadOnlyList<Assembly> Assemblies { get; } = assemblies;
    public IReadOnlyList<Type> ContextTypes { get; } = contextTypes;
    public List<EntityBinding> Bindings { get; } = bindings;
    public List<string> Warnings { get; } = warnings;
    public List<string> PolicyViolations { get; } = policyViolations;
    public ILogger Log { get; } = log;
    public IReadOnlyList<EntityDescriptor> Entities { get; } = bindings.Select(b => b.Descriptor).ToList();

    private readonly Lock _schemaLock = new();
    private bool _schemaEnsured;

    /// <summary>Parallel first sessions race to create the schema — only one may run CreateTables.</summary>
    public void EnsureSchemaOnce(IEnumerable<DbContext> contexts)
    {
        if (_schemaEnsured) return;
        lock (_schemaLock)
        {
            if (_schemaEnsured) return;
            foreach (var ctx in contexts) EnsureSchema(ctx);
            _schemaEnsured = true;
        }
    }

    // EnsureCreated alone is not enough when multiple contexts share one database:
    // it no-ops as soon as any tables exist, leaving later contexts' tables missing.
    private static void EnsureSchema(DbContext ctx)
    {
        var creator = ctx.Database.GetService<IRelationalDatabaseCreator>();
        if (!creator.Exists()) creator.Create();
        try { creator.CreateTables(); }
        catch (Exception)
        {
            // This context's tables (or some of them) already exist — expected on reuse.
        }
    }
}

/// <summary>
/// The EF-backed implementation of <see cref="IDomainRuntime"/>: owns per-scenario DbContexts,
/// transactions, tracked-teardown bookkeeping, repository service provider and faker instances.
/// Contexts are created fresh per scenario and disposed at scenario end. One instance is one
/// execution session over a shared immutable <see cref="DomainRuntimeCore"/> (W3-D2); parallel
/// scenarios each run on their own <see cref="CreateSession"/> instance.
/// </summary>
public sealed class DomainRuntime : IDomainRuntime
{
    private readonly DomainRuntimeCore _core;

    // Per-scenario state.
    private readonly Dictionary<Type, DbContext> _contexts = [];
    private readonly List<IDbContextTransaction> _transactions = [];
    private readonly Dictionary<Type, object> _fakerInstances = [];
    private readonly List<(EntityBinding Binding, object Instance)> _tracked = [];
    /// <summary>Bulk rows under TrackedTeardown are tracked by primary key, not instance
    /// (W3-D4) — 1M tracked instances would defeat the O(chunk) streaming pipeline. Torn
    /// down set-based in <see cref="DeleteByKeysAsync"/>.</summary>
    private readonly List<(EntityBinding Binding, List<object> Keys)> _trackedBulk = [];
    private readonly HashSet<Type> _repoResolutionFailures = [];
    private ServiceProvider? _repositories;
    private Faker _autoFaker = new();
    private LifecycleMode _lifecycle = LifecycleMode.Persistent;

    internal DomainRuntime(DomainSettings settings, IReadOnlyList<Assembly> assemblies,
        IReadOnlyList<Type> contextTypes, List<EntityBinding> bindings, List<string> warnings,
        List<string> policyViolations, ILogger? logger)
        : this(new DomainRuntimeCore(settings, assemblies, contextTypes, bindings, warnings,
            policyViolations, logger ?? NullLogger.Instance))
    {
    }

    private DomainRuntime(DomainRuntimeCore core) => _core = core;

    public IDomainRuntime CreateSession() => new DomainRuntime(_core);

    public string Name => _core.Settings.Name;
    public DomainSettings Settings => _core.Settings;
    public IReadOnlyList<EntityDescriptor> Entities => _core.Entities;
    public IReadOnlyList<string> Warnings => _core.Warnings;
    public IReadOnlyList<string> PolicyViolations => _core.PolicyViolations;

    private List<EntityBinding> Bindings => _core.Bindings;
    private ILogger Log => _core.Log;

    // ---------------------------------------------------------------- Resolution

    public bool TryResolveEntity(string gherkinName, out EntityDescriptor? entity, out string? error)
    {
        entity = null;
        error = null;
        var matches = Bindings.Where(b => NameMatcher.Matches(gherkinName, b.Descriptor.LogicalName)).ToList();
        switch (matches.Count)
        {
            case 1:
                entity = matches[0].Descriptor;
                return true;
            case 0:
                return false;
            default:
                error = $"Entity '{gherkinName}' is ambiguous within domain '{Name}': " +
                        string.Join(", ", matches.Select(m => m.Descriptor.ClrType.Name)) + ".";
                return false;
        }
    }

    public IReadOnlyList<EntityResolutionInfo> DescribeEntities() =>
        Bindings.Select(b => new EntityResolutionInfo(
            b.Descriptor.LogicalName,
            b.Descriptor.ClrType.FullName ?? b.Descriptor.ClrType.Name,
            b.Descriptor.NaturalKeyProperty?.Name,
            b.Descriptor.KeyProperty is null
                ? "(no single-column key)"
                : $"{b.Descriptor.KeyProperty.Name}:{b.Descriptor.KeyProperty.PropertyType.Name}" +
                  (b.Descriptor.KeyIsDbGenerated ? " (db-generated)" : $" ({b.Descriptor.IdStrategy})"),
            b.Repository is null ? null : $"{b.Repository.InterfaceType.Name} → {b.Repository.ImplementationType.Name}",
            b.ReadRepository is null ? null : $"{b.ReadRepository.InterfaceType.Name} → {b.ReadRepository.ImplementationType.Name}",
            b.Faker?.FakerType.Name ?? "auto",
            PredictRoute(b))).ToList();

    private string PredictRoute(EntityBinding binding) => Settings.Persistence switch
    {
        PersistenceMode.DbContextOnly => "DbContext",
        _ when binding.Repository?.AddMethod is { } m => $"{binding.Repository.InterfaceType.Name}.{m.Name}",
        PersistenceMode.RepositoryOnly => "(unavailable: no repository persist method)",
        _ => "DbContext (no repository persist method)",
    };

    private EntityBinding BindingFor(EntityDescriptor descriptor) =>
        Bindings.First(b => ReferenceEquals(b.Descriptor, descriptor));

    // ---------------------------------------------------------------- Lifecycle

    public async Task BeginScenarioAsync(LifecycleMode lifecycle, int seed, CancellationToken ct = default)
    {
        await ResetScenarioStateAsync().ConfigureAwait(false);
        _lifecycle = lifecycle;
        _seedValue = seed;
        EnsureContexts();

        if (lifecycle == LifecycleMode.Transactional)
        {
            foreach (var ctx in _contexts.Values)
                _transactions.Add(await ctx.Database.BeginTransactionAsync(ct).ConfigureAwait(false));
        }

        _autoFaker = new Faker { Random = new Randomizer(seed) };
        BuildRepositoryProvider();
        void BuildRepositoryProvider()
        {
            var services = new ServiceCollection();
            foreach (var (type, ctx) in _contexts)
                services.AddSingleton(type, ctx);
            foreach (var binding in Bindings.Where(b => b.Repository is not null))
                services.TryAddRepository(binding.Repository!);
            _repositories = services.BuildServiceProvider();
        }

        _fakerInstances.Clear();
        _tracked.Clear();
        _repoResolutionFailures.Clear();
    }

    public async Task<ScenarioCloseOutcome> EndScenarioAsync(CancellationToken ct = default)
    {
        var outcome = new ScenarioCloseOutcome();
        try
        {
            switch (_lifecycle)
            {
                case LifecycleMode.Transactional:
                    foreach (var tx in _transactions)
                    {
                        try { await tx.RollbackAsync(ct).ConfigureAwait(false); }
                        catch (Exception ex) { outcome.Error = $"rollback failed: {ex.Message}"; }
                    }
                    break;

                case LifecycleMode.TrackedTeardown:
                    // Bulk-created row sets first (they are leaves — bulk rows are never
                    // principals of individually-created children in the same scenario order),
                    // newest set first, then individually tracked instances in reverse
                    // dependency order: children were created after their principals.
                    for (var i = _trackedBulk.Count - 1; i >= 0; i--)
                    {
                        var (bulkBinding, keys) = _trackedBulk[i];
                        try
                        {
                            outcome.Deleted += await DeleteByKeysAsync(bulkBinding, keys, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            outcome.Orphaned.Add(
                                $"{Name}.{bulkBinding.Descriptor.LogicalName}: set-based teardown of {keys.Count} bulk row(s) failed — {ex.Message}");
                            Log.LogWarning(ex, "Set-based teardown failed for {Count} {Entity} bulk row(s); rows left orphaned",
                                keys.Count, bulkBinding.Descriptor.LogicalName);
                        }
                    }
                    for (var i = _tracked.Count - 1; i >= 0; i--)
                    {
                        var (binding, instance) = _tracked[i];
                        var ctx = ContextFor(binding);
                        try
                        {
                            ctx.Remove(instance);
                            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                            outcome.Deleted++;
                        }
                        catch (Exception ex)
                        {
                            try { ctx.Entry(instance).State = EntityState.Detached; } catch { /* best effort */ }
                            var key = binding.Descriptor.GetNaturalKey(instance)
                                      ?? Convert.ToString(binding.Descriptor.GetKey(instance), CultureInfo.InvariantCulture);
                            outcome.Orphaned.Add($"{Name}.{binding.Descriptor.LogicalName}:{key} — {ex.Message}");
                            Log.LogWarning(ex, "Teardown failed for {Entity} {Key}; row left orphaned",
                                binding.Descriptor.LogicalName, key);
                        }
                    }
                    break;
            }
        }
        finally
        {
            await ResetScenarioStateAsync().ConfigureAwait(false);
        }
        return outcome;
    }

    private async Task ResetScenarioStateAsync()
    {
        foreach (var tx in _transactions) await tx.DisposeAsync().ConfigureAwait(false);
        _transactions.Clear();
        foreach (var ctx in _contexts.Values) await ctx.DisposeAsync().ConfigureAwait(false);
        _contexts.Clear();
        if (_repositories is not null) await _repositories.DisposeAsync().ConfigureAwait(false);
        _repositories = null;
        _tracked.Clear();
        _trackedBulk.Clear();
        _fakerInstances.Clear();
    }

    private void EnsureContexts()
    {
        if (_contexts.Count > 0) return;
        foreach (var contextType in _core.ContextTypes)
            _contexts[contextType] = DbContextActivator.Activate(contextType, Settings, _core.Assemblies);
        if (Settings.EnsureCreated)
            _core.EnsureSchemaOnce(_contexts.Values);
    }

    private DbContext ContextFor(EntityBinding binding)
    {
        EnsureContexts();
        if (binding.ContextType is null)
        {
            throw new InvalidOperationException(
                $"Entity '{binding.Descriptor.LogicalName}' was resolved by fallback discovery but is not mapped " +
                $"in any DbContext model of domain '{Name}' — it cannot be persisted or queried.");
        }
        return _contexts[binding.ContextType];
    }

    // ---------------------------------------------------------------- Generation

    public object Generate(EntityDescriptor entity, out string fakerSource, List<string> warnings)
    {
        var binding = BindingFor(entity);
        if (binding.Faker is { } faker)
        {
            // One instance per scenario: subsequent Generate calls consume the seeded
            // sequence in step order (handoff §7).
            if (!_fakerInstances.TryGetValue(faker.FakerType, out var instance))
            {
                instance = faker.CreateSeeded(_seedValue);
                _fakerInstances[faker.FakerType] = instance;
            }
            fakerSource = faker.FakerType.Name;
            return faker.Generate(instance);
        }

        fakerSource = "auto";
        return AutoFaker.Generate(entity, binding.AutoFakerSkip, _autoFaker);
    }

    private int _seedValue;

    // ---------------------------------------------------------------- Persistence

    public async Task<PersistOutcome> CreateAsync(EntityDescriptor entity, object instance,
        bool forceDbContext = false, CancellationToken ct = default)
    {
        var binding = BindingFor(entity);

        if (!forceDbContext && Settings.Persistence != PersistenceMode.DbContextOnly)
        {
            var (repo, method) = ResolveRepository(binding, b => b.Repository?.AddMethod);
            if (repo is not null && method is not null)
            {
                try
                {
                    await RepositoryBinding.InvokeAsync(method, repo, instance).ConfigureAwait(false);
                    Track(binding, instance);
                    return PersistOutcome.Ok($"{binding.Repository!.InterfaceType.Name}.{method.Name}");
                }
                catch (Exception ex)
                {
                    TryDetach(binding, instance);
                    return PersistOutcome.Fail(Unwrap(ex), $"{binding.Repository!.InterfaceType.Name}.{method.Name}");
                }
            }
            if (Settings.Persistence == PersistenceMode.RepositoryOnly)
                return PersistOutcome.Fail($"Persistence is RepositoryOnly but no repository persist method resolved for '{entity.LogicalName}'.");
        }

        return await DbContextPersistAsync(binding, instance, ctx => ctx.Add(instance), "DbContext", track: true, ct).ConfigureAwait(false);
    }

    public async Task<PersistOutcome> CreateBulkAsync(EntityDescriptor entity, IReadOnlyList<object> instances,
        BulkPersistOptions options, CancellationToken ct = default)
    {
        var binding = BindingFor(entity);
        DbContext ctx;
        try { ctx = ContextFor(binding); }
        catch (InvalidOperationException ex) { return PersistOutcome.Fail(ex.Message); }

        var chunkSize = Math.Max(1, options.ChunkSize);

        // Provider-native path (W3-D3): only for client-set keys — bulk APIs don't propagate
        // store-generated keys back into the instances (teardown and references need them).
        if (options.Strategy == BulkStrategy.Provider &&
            binding.EfType is not null &&
            !entity.KeyIsDbGenerated && entity.IdStrategy != IdStrategy.DbGenerated &&
            Providers.ProviderRegistry.InserterFor(ctx) is { } inserter)
        {
            if (BulkColumns.TryMap(binding.EfType, out var map, out var reason))
            {
                var persisted = 0;
                try
                {
                    foreach (var chunk in instances.Chunk(chunkSize))
                    {
                        ct.ThrowIfCancellationRequested();
                        await inserter.InsertAsync(ctx, binding.EfType, map!, chunk, ct).ConfigureAwait(false);
                        persisted += chunk.Length;
                        TrackBulk(binding, chunk);
                    }
                    return PersistOutcome.Ok(inserter.Route);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return PersistOutcome.Fail(
                        $"{Unwrap(ex)} ({persisted}/{instances.Count} rows persisted before failure)", inserter.Route);
                }
            }
            Log.LogDebug("Bulk create of {Entity}: provider inserter unavailable ({Reason}); using the EF path.",
                entity.LogicalName, reason);
        }

        return await CreateBulkEfCoreAsync(binding, ctx, instances, chunkSize, ct).ConfigureAwait(false);
    }

    /// <summary>The portable chunked AddRange+SaveChanges path (v1). The change tracker is
    /// cleared between chunks so memory stays O(chunk) at any count.</summary>
    private async Task<PersistOutcome> CreateBulkEfCoreAsync(EntityBinding binding, DbContext ctx,
        IReadOnlyList<object> instances, int chunkSize, CancellationToken ct)
    {
        var persisted = 0;
        try
        {
            foreach (var chunk in instances.Chunk(chunkSize))
            {
                ct.ThrowIfCancellationRequested();
                ctx.AddRange(chunk);
                await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
                persisted += chunk.Length;
                TrackBulk(binding, chunk);
                ctx.ChangeTracker.Clear();
            }
            return PersistOutcome.Ok("DbContext(bulk)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ctx.ChangeTracker.Clear();
            return PersistOutcome.Fail($"{Unwrap(ex)} ({persisted}/{instances.Count} rows persisted before failure)", "DbContext(bulk)");
        }
    }

    public async Task<PersistOutcome> UpdateAsync(EntityDescriptor entity, object instance, CancellationToken ct = default)
    {
        var binding = BindingFor(entity);

        if (Settings.Persistence != PersistenceMode.DbContextOnly)
        {
            var (repo, method) = ResolveRepository(binding, b => b.Repository?.UpdateMethod);
            if (repo is not null && method is not null)
            {
                try
                {
                    await RepositoryBinding.InvokeAsync(method, repo, instance).ConfigureAwait(false);
                    return PersistOutcome.Ok($"{binding.Repository!.InterfaceType.Name}.{method.Name}");
                }
                catch (Exception ex)
                {
                    TryDetach(binding, instance);
                    return PersistOutcome.Fail(Unwrap(ex), $"{binding.Repository!.InterfaceType.Name}.{method.Name}");
                }
            }
            if (Settings.Persistence == PersistenceMode.RepositoryOnly)
                return PersistOutcome.Fail($"Persistence is RepositoryOnly but no repository update method resolved for '{entity.LogicalName}'.");
        }

        return await DbContextPersistAsync(binding, instance, ctx =>
        {
            if (ctx.Entry(instance).State == EntityState.Detached) ctx.Update(instance);
        }, "DbContext", track: false, ct).ConfigureAwait(false);
    }

    public async Task<PersistOutcome> DeleteAsync(EntityDescriptor entity, object instance, CancellationToken ct = default)
    {
        var binding = BindingFor(entity);
        _tracked.RemoveAll(t => ReferenceEquals(t.Instance, instance));

        if (Settings.Persistence != PersistenceMode.DbContextOnly)
        {
            var (repo, method) = ResolveRepository(binding, b => b.Repository?.DeleteMethod);
            if (repo is not null && method is not null)
            {
                try
                {
                    await RepositoryBinding.InvokeAsync(method, repo, instance).ConfigureAwait(false);
                    return PersistOutcome.Ok($"{binding.Repository!.InterfaceType.Name}.{method.Name}");
                }
                catch (Exception ex)
                {
                    TryDetach(binding, instance);
                    return PersistOutcome.Fail(Unwrap(ex), $"{binding.Repository!.InterfaceType.Name}.{method.Name}");
                }
            }
            if (Settings.Persistence == PersistenceMode.RepositoryOnly)
                return PersistOutcome.Fail($"Persistence is RepositoryOnly but no repository delete method resolved for '{entity.LogicalName}'.");
        }

        return await DbContextPersistAsync(binding, instance, ctx => ctx.Remove(instance), "DbContext", track: false, ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteWhereAsync(EntityDescriptor entity, IReadOnlyList<PropertyFilter> filters, CancellationToken ct = default)
    {
        var binding = BindingFor(entity);
        var ctx = ContextFor(binding);
        var rows = await EntityQuery.WhereAsync(ctx, entity.ClrType, filters, take: null, ct).ConfigureAwait(false);
        if (rows.Count == 0) return 0;
        ctx.RemoveRange(rows);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        var deletedSet = rows.ToHashSet(ReferenceEqualityComparer.Instance);
        _tracked.RemoveAll(t => deletedSet.Contains(t.Instance));
        return rows.Count;
    }

    private async Task<PersistOutcome> DbContextPersistAsync(EntityBinding binding, object instance,
        Action<DbContext> mutate, string route, bool track, CancellationToken ct)
    {
        DbContext ctx;
        try { ctx = ContextFor(binding); }
        catch (InvalidOperationException ex) { return PersistOutcome.Fail(ex.Message); }

        try
        {
            mutate(ctx);
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            if (track) Track(binding, instance);
            return PersistOutcome.Ok(route);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try { ctx.Entry(instance).State = EntityState.Detached; } catch { /* best effort */ }
            return PersistOutcome.Fail(Unwrap(ex), route);
        }
    }

    private void Track(EntityBinding binding, object instance)
    {
        if (_lifecycle == LifecycleMode.TrackedTeardown)
            _tracked.Add((binding, instance));
    }

    private void TrackBulk(EntityBinding binding, IReadOnlyList<object> chunk)
    {
        if (_lifecycle != LifecycleMode.TrackedTeardown) return;
        if (binding.Descriptor.KeyProperty is null || binding.EfType is null)
        {
            // No single-column key to delete by — fall back to instance tracking.
            foreach (var instance in chunk) _tracked.Add((binding, instance));
            return;
        }

        if (_trackedBulk.Count == 0 || !ReferenceEquals(_trackedBulk[^1].Binding, binding))
            _trackedBulk.Add((binding, []));
        var keys = _trackedBulk[^1].Keys;
        foreach (var instance in chunk)
            keys.Add(binding.Descriptor.GetKey(instance)!);
    }

    /// <summary>Set-based teardown of bulk-created rows: DELETE … WHERE key IN (…), batched.</summary>
    private async Task<int> DeleteByKeysAsync(EntityBinding binding, List<object> keys, CancellationToken ct)
    {
        var ctx = ContextFor(binding);
        var efType = binding.EfType!;
        var tableName = efType.GetTableName()
                        ?? throw new InvalidOperationException($"{binding.Descriptor.LogicalName} is not mapped to a table.");
        var keyProperty = efType.FindPrimaryKey()!.Properties[0];
        var helper = ctx.GetService<Microsoft.EntityFrameworkCore.Storage.ISqlGenerationHelper>();
        var table = helper.DelimitIdentifier(tableName, efType.GetSchema());
        var column = helper.DelimitIdentifier(
            keyProperty.GetColumnName(Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(tableName, efType.GetSchema()))
            ?? keyProperty.GetColumnName());
        var converter = keyProperty.GetTypeMapping().Converter;

        var deleted = 0;
        foreach (var batch in keys.Chunk(500))
        {
            ct.ThrowIfCancellationRequested();
            var placeholders = string.Join(", ", Enumerable.Range(0, batch.Length).Select(i => $"{{{i}}}"));
            var values = batch.Select(k => converter is null ? k : converter.ConvertToProvider(k)!).ToArray();
            // Identifiers come from ISqlGenerationHelper over EF metadata and the {n}
            // placeholders become provider parameters — nothing user-controlled is inlined.
#pragma warning disable EF1002
            deleted += await ctx.Database
                .ExecuteSqlRawAsync($"DELETE FROM {table} WHERE {column} IN ({placeholders})", values, ct)
                .ConfigureAwait(false);
#pragma warning restore EF1002
        }
        return deleted;
    }

    /// <summary>A failed entity must not stay tracked — it would poison every later SaveChanges.</summary>
    private void TryDetach(EntityBinding binding, object instance)
    {
        try
        {
            if (binding.ContextType is not null && _contexts.TryGetValue(binding.ContextType, out var ctx))
                ctx.Entry(instance).State = EntityState.Detached;
        }
        catch { /* best effort */ }
    }

    private (object? Repo, MethodInfo? Method) ResolveRepository(EntityBinding binding,
        Func<EntityBinding, MethodInfo?> methodSelector)
    {
        var method = methodSelector(binding);
        if (binding.Repository is null || method is null || _repositories is null) return (null, null);
        if (_repoResolutionFailures.Contains(binding.Repository.InterfaceType)) return (null, null);
        try
        {
            var repo = _repositories.GetService(binding.Repository.InterfaceType);
            if (repo is null) _repoResolutionFailures.Add(binding.Repository.InterfaceType);
            return (repo, method);
        }
        catch (InvalidOperationException ex)
        {
            // Unresolvable constructor dependencies → warn once, fall back to DbContext (handoff §5.2).
            _repoResolutionFailures.Add(binding.Repository.InterfaceType);
            Log.LogWarning("Repository {Repository} could not be constructed ({Message}); falling back to DbContext persistence.",
                binding.Repository.ImplementationType.Name, ex.Message);
            return (null, null);
        }
    }

    private static string Unwrap(Exception ex)
    {
        var inner = ex;
        while (inner is TargetInvocationException { InnerException: { } deeper }) inner = deeper;
        if (inner.InnerException is { } cause && inner is DbUpdateException)
            return $"{inner.GetType().Name}: {cause.Message}";
        return $"{inner.GetType().Name}: {inner.Message}";
    }

    // ---------------------------------------------------------------- Queries

    public async Task<object?> FindByNaturalKeyAsync(EntityDescriptor entity, string naturalKey, CancellationToken ct = default)
    {
        var binding = BindingFor(entity);
        if (entity.NaturalKeyProperty is null)
        {
            throw new InvalidOperationException(
                $"Entity '{entity.LogicalName}' has no natural key property configured " +
                $"(profile default or entities.{entity.LogicalName}.naturalKey).");
        }
        if (!ValueConverter.TryConvert(naturalKey, entity.NaturalKeyProperty.PropertyType, out var keyValue, out var error))
            throw new InvalidOperationException(error);

        var ctx = ContextFor(binding);
        var matches = await EntityQuery.WhereAsync(ctx, entity.ClrType,
            [new PropertyFilter(entity.NaturalKeyProperty, keyValue)], take: 2, ct).ConfigureAwait(false);

        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"Natural key '{naturalKey}' matches multiple {entity.LogicalName} rows " +
                $"(ids: {string.Join(", ", matches.Select(m => entity.GetKey(m)))}) — natural keys must be unique."),
        };
    }

    public async Task<object?> FindByIdAsync(EntityDescriptor entity, string id, CancellationToken ct = default)
    {
        var binding = BindingFor(entity);
        if (entity.KeyProperty is null)
            throw new InvalidOperationException($"Entity '{entity.LogicalName}' has no single-column key.");
        if (!ValueConverter.TryConvert(id, entity.KeyProperty.PropertyType, out var keyValue, out var error))
            throw new InvalidOperationException(error);
        var ctx = ContextFor(binding);
        return await ctx.FindAsync(entity.ClrType, [keyValue], ct).ConfigureAwait(false);
    }

    public async Task<int> CountAsync(EntityDescriptor entity, IReadOnlyList<PropertyFilter> filters, CancellationToken ct = default)
    {
        var ctx = ContextFor(BindingFor(entity));
        return await EntityQuery.CountAsync(ctx, entity.ClrType, filters, ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteByIdAsync(string logicalEntityName, string id, CancellationToken ct = default)
    {
        if (!TryResolveEntity(logicalEntityName, out var entity, out var error))
            throw new InvalidOperationException(error ?? $"Entity '{logicalEntityName}' not found in domain '{Name}'.");
        var binding = BindingFor(entity!);
        if (entity!.KeyProperty is null)
            throw new InvalidOperationException($"Entity '{logicalEntityName}' has no single-column key.");
        if (!ValueConverter.TryConvert(id, entity.KeyProperty.PropertyType, out var keyValue, out var convertError))
            throw new InvalidOperationException(convertError);

        var ctx = ContextFor(binding);
        var row = await ctx.FindAsync(entity.ClrType, [keyValue], ct).ConfigureAwait(false);
        if (row is null) return false;
        ctx.Remove(row);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    // ---------------------------------------------------------------- References

    public bool TrySetReference(object instance, EntityDescriptor entity, string referencedEntityName,
        EntityDescriptor? referencedDescriptor, object? referencedInstance, object? referencedId, out string? error)
    {
        error = null;
        var binding = BindingFor(entity);

        if (binding.EfType is not null && referencedDescriptor is not null)
        {
            var foreignKey = binding.EfType.GetForeignKeys()
                .FirstOrDefault(fk => fk.PrincipalEntityType.ClrType == referencedDescriptor.ClrType);
            if (foreignKey is not null)
            {
                // Prefer the FK column when it is a real CLR property (handoff §8).
                if (foreignKey.Properties is [{ PropertyInfo: { } fkProp }] && referencedId is not null)
                {
                    fkProp.SetValue(instance, CoerceKey(referencedId, fkProp.PropertyType));
                    return true;
                }
                if (foreignKey.DependentToPrincipal?.PropertyInfo is { } navProp && referencedInstance is not null)
                {
                    navProp.SetValue(instance, referencedInstance);
                    return true;
                }
            }
        }

        // Name-convention fallback — covers external references whose principal type
        // is not part of this domain's model (FK columns agreeing via the identity contract).
        var conventionProp = PropertyMatcher.Find(entity.ClrType, referencedEntityName + "Id");
        if (conventionProp is not null && referencedId is not null)
        {
            conventionProp.SetValue(instance, CoerceKey(referencedId, conventionProp.PropertyType));
            return true;
        }

        error = $"no foreign key or navigation to '{referencedEntityName}' found on {entity.ClrType.Name}, " +
                $"and no '{referencedEntityName}Id' property exists" +
                (referencedId is null && referencedInstance is null ? " (reference resolved to nothing)" : "");
        return false;
    }

    private static object? CoerceKey(object id, Type targetType)
    {
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (underlying.IsInstanceOfType(id)) return id;
        if (underlying == typeof(Guid)) return id is string s ? Guid.Parse(s) : id;
        return Convert.ChangeType(id, underlying, CultureInfo.InvariantCulture);
    }

    public async ValueTask DisposeAsync() => await ResetScenarioStateAsync().ConfigureAwait(false);
}

file static class ServiceCollectionExtensions
{
    public static void TryAddRepository(this IServiceCollection services, RepositoryBinding binding)
    {
        if (services.All(d => d.ServiceType != binding.InterfaceType))
            services.AddTransient(binding.InterfaceType, binding.ImplementationType);
    }
}
