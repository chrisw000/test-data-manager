using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tdm.Core.Conversion;
using Tdm.Core.Execution;
using Tdm.Core.Generation;
using Tdm.Core.Naming;
using Tdm.Core.Settings;
using Tdm.EfCore.Repositories;

namespace Tdm.EfCore;

/// <summary>
/// Builds a <see cref="DomainRuntime"/> from a set of domain assemblies — whether plugin-loaded
/// (primary mode) or directly referenced (secondary, compile-time mode; handoff §3).
/// Entity discovery is EF-model-first: <c>dbContext.Model.GetEntityTypes()</c> is the
/// authoritative registry (D3), with a convention-based assembly scan as fallback.
/// </summary>
public static class DomainRuntimeBuilder
{
    public static DomainRuntime Build(DomainSettings domain, TdmSettings root,
        IReadOnlyList<Assembly> assemblies, ILogger? logger = null)
    {
        var profile = root.ProfileFor(domain);
        var warnings = new List<string>();
        var policyViolations = new List<string>();
        var bindings = new List<EntityBinding>();

        var contextTypes = assemblies
            .SelectMany(DbContextActivator.SafeGetTypes)
            .Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true } && typeof(DbContext).IsAssignableFrom(t))
            .ToList();
        if (contextTypes.Count == 0)
        {
            throw new InvalidOperationException(
                $"Domain '{domain.Name}': no public DbContext subclass found in assemblies " +
                $"[{string.Join(", ", assemblies.Select(a => a.GetName().Name))}].");
        }

        foreach (var contextType in contextTypes)
        {
            // Model building is offline — no database connection is opened here,
            // which keeps `tdm validate` connection-free.
            using var context = DbContextActivator.Activate(contextType, domain, assemblies);
            foreach (var efType in context.Model.GetEntityTypes())
            {
                if (efType.IsOwned() || efType.HasSharedClrType) continue;
                var clrType = efType.ClrType;
                var logicalName = NameMatcher.StripPattern(clrType.Name, profile.EntityClassPattern);
                var entityConfig = root.EntityFor(logicalName);

                var keyProperties = efType.FindPrimaryKey()?.Properties;
                var keyProperty = keyProperties is [{ PropertyInfo: { } pi }] ? pi : null;
                var keyEfProperty = keyProperties is [{ } single] ? single : null;
                var keyIsDbGenerated = keyEfProperty?.ValueGenerated == Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAdd
                                       && keyProperty is not null
                                       && IsIntegral(keyProperty.PropertyType);

                var naturalKeyName = entityConfig.NaturalKey ?? profile.NaturalKeyDefault;
                var naturalKeyProperty = PropertyMatcher.Find(clrType, naturalKeyName);

                var effectiveStrategy = entityConfig.IdStrategy switch
                {
                    IdStrategy.Auto => keyProperty is not null && !keyIsDbGenerated &&
                                       (keyProperty.PropertyType == typeof(Guid) || keyProperty.PropertyType == typeof(Guid?))
                        ? IdStrategy.Deterministic
                        : IdStrategy.DbGenerated,
                    var explicitStrategy => explicitStrategy,
                };

                var navigationNames = efType.GetNavigations().Select(n => n.Name)
                    .Concat(efType.GetSkipNavigations().Select(n => n.Name))
                    .ToHashSet(StringComparer.Ordinal);

                var autoFakerSkip = new HashSet<string>(navigationNames, StringComparer.Ordinal);
                foreach (var fk in efType.GetForeignKeys())
                foreach (var fkProp in fk.Properties)
                    autoFakerSkip.Add(fkProp.Name);
                if (keyProperty is not null) autoFakerSkip.Add(keyProperty.Name);

                var descriptor = new EntityDescriptor
                {
                    LogicalName = logicalName,
                    DomainName = domain.Name,
                    ClrType = clrType,
                    KeyProperty = keyProperty,
                    KeyIsDbGenerated = keyIsDbGenerated,
                    NaturalKeyProperty = naturalKeyProperty,
                    IdStrategy = effectiveStrategy,
                    NavigationNames = navigationNames,
                };

                var repository = RepositoryBinding.Find(logicalName, clrType, profile, entityConfig, assemblies);
                var readRepository = RepositoryBinding.FindRead(logicalName, profile, assemblies);
                CheckRepositoryPolicy(domain, profile, entityConfig, logicalName, repository, warnings, policyViolations);

                var faker = FakerBinding.Find(logicalName, clrType, profile, assemblies);
                if (faker is null)
                    warnings.Add($"{domain.Name}.{logicalName}: no {NameMatcher.Expand(profile.FakerPattern, logicalName)} found — heuristic auto-faker will be used.");

                bindings.Add(new EntityBinding
                {
                    Descriptor = descriptor,
                    ContextType = contextType,
                    EfType = efType,
                    Repository = repository,
                    ReadRepository = readRepository,
                    Faker = faker,
                    AutoFakerSkip = autoFakerSkip,
                });
            }
        }

        AddEntityTypeConfigurationFallback(domain, profile, root, assemblies, bindings, warnings);
        AddAssemblyScanFallback(domain, profile, root, assemblies, bindings, warnings);

        foreach (var warning in warnings)
            logger?.LogWarning("{Warning}", warning);

        return new DomainRuntime(domain, root, assemblies, contextTypes, bindings, warnings, policyViolations, logger);
    }

    /// <summary>
    /// Write-repository policy (ADR-0001): every context-mapped entity in a domain whose profile
    /// sets <c>requireWriteRepository</c> must have a write repository with a persist method —
    /// unless the domain routes DbContextOnly by declared choice, or the entity is explicitly
    /// exempted via <c>entities.{Name}.requireRepository: false</c>.
    /// </summary>
    private static void CheckRepositoryPolicy(DomainSettings domain, ConventionProfile profile,
        EntitySettings entityConfig, string logicalName, RepositoryBinding? repository,
        List<string> warnings, List<string> policyViolations)
    {
        if (domain.Persistence == PersistenceMode.DbContextOnly) return;

        var probed = entityConfig.WriteRepository is { Length: > 0 } explicitName
            ? explicitName
            : string.Join(", ", profile.WriteRepositoryPatterns.Select(p => NameMatcher.Expand(p, logicalName)));

        if (repository is null || repository.AddMethod is null)
        {
            var reason = repository is null
                ? $"no write repository found (probed: {probed})"
                : $"{repository.InterfaceType.Name} exposes no recognised persist method";

            var required = entityConfig.RequireRepository ?? profile.RequireWriteRepository;
            if (required)
            {
                policyViolations.Add(
                    $"{domain.Name}.{logicalName}: {reason} — the write-repository policy requires one. " +
                    $"Add the repository, or exempt this entity via entities.{logicalName}.requireRepository: false.");
            }
            else if (entityConfig.RequireRepository == false)
            {
                warnings.Add($"{domain.Name}.{logicalName}: {reason} — exempted from the write-repository policy; DbContext persistence will be used.");
            }
            else
            {
                warnings.Add($"{domain.Name}.{logicalName}: {reason} — DbContext persistence will be used.");
            }
        }
    }

    /// <summary>
    /// Discovery fallback keyed on the developers' own convention: every table has an
    /// <c>IEntityTypeConfiguration&lt;T&gt;</c>. A T that carries a configuration but is not in
    /// any context model usually means a missed <c>ApplyConfigurationsFromAssembly</c> — surfaced
    /// as a warning; the type stays resolvable for generation only.
    /// </summary>
    private static void AddEntityTypeConfigurationFallback(DomainSettings domain, ConventionProfile profile,
        TdmSettings root, IReadOnlyList<Assembly> assemblies, List<EntityBinding> bindings, List<string> warnings)
    {
        var known = bindings.Select(b => b.Descriptor.ClrType).ToHashSet();

        foreach (var type in assemblies.SelectMany(DbContextActivator.SafeGetTypes))
        {
            if (type is not { IsClass: true, IsAbstract: false }) continue;
            foreach (var configured in type.GetInterfaces())
            {
                if (!configured.IsGenericType ||
                    configured.GetGenericTypeDefinition() != typeof(Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<>))
                    continue;

                var entityType = configured.GetGenericArguments()[0];
                if (known.Contains(entityType) || entityType.GetConstructor(Type.EmptyTypes) is null) continue;
                known.Add(entityType);

                var logicalName = NameMatcher.StripPattern(entityType.Name, profile.EntityClassPattern);
                bindings.Add(BuildUnmappedBinding(domain, profile, root, assemblies, entityType, logicalName));
                warnings.Add(
                    $"{domain.Name}.{logicalName}: {type.Name} configures {entityType.Name} but it is not mapped in any " +
                    $"DbContext model — check ApplyConfigurationsFromAssembly; usable for generation only.");
            }
        }
    }

    /// <summary>
    /// Convention-based scan for types deliberately not mapped in any context (handoff §5.1
    /// step 3). Resolvable by name but not persistable — the runtime reports a clear error
    /// if a step tries to persist one.
    /// </summary>
    private static void AddAssemblyScanFallback(DomainSettings domain, ConventionProfile profile,
        TdmSettings root, IReadOnlyList<Assembly> assemblies, List<EntityBinding> bindings, List<string> warnings)
    {
        if (string.IsNullOrEmpty(profile.EntityNamespaceSuffix)) return;
        var known = bindings.Select(b => b.Descriptor.ClrType).ToHashSet();

        foreach (var type in assemblies.SelectMany(DbContextActivator.SafeGetTypes))
        {
            if (!type.IsClass || type.IsAbstract || !type.IsPublic || known.Contains(type)) continue;
            if (type.Namespace is null ||
                !type.Namespace.EndsWith(profile.EntityNamespaceSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            var logicalName = NameMatcher.StripPattern(type.Name, profile.EntityClassPattern);
            if (logicalName == type.Name && profile.EntityClassPattern != "{Name}") continue; // pattern didn't apply
            if (type.GetConstructor(Type.EmptyTypes) is null) continue;
            if (typeof(DbContext).IsAssignableFrom(type)) continue;

            bindings.Add(BuildUnmappedBinding(domain, profile, root, assemblies, type, logicalName));
            warnings.Add($"{domain.Name}.{logicalName}: resolved by assembly scan ({type.FullName}) but not mapped in any DbContext — usable for generation only.");
        }
    }

    /// <summary>Binding for a fallback-discovered type not mapped in any context model —
    /// resolvable by name, generation only; persisting reports a clear error.</summary>
    private static EntityBinding BuildUnmappedBinding(DomainSettings domain, ConventionProfile profile,
        TdmSettings root, IReadOnlyList<Assembly> assemblies, Type type, string logicalName)
    {
        var entityConfig = root.EntityFor(logicalName);
        var naturalKeyProperty = PropertyMatcher.Find(type, entityConfig.NaturalKey ?? profile.NaturalKeyDefault);
        var keyProperty = PropertyMatcher.Find(type, "Id");

        return new EntityBinding
        {
            Descriptor = new EntityDescriptor
            {
                LogicalName = logicalName,
                DomainName = domain.Name,
                ClrType = type,
                KeyProperty = keyProperty,
                NaturalKeyProperty = naturalKeyProperty,
                IdStrategy = IdStrategy.Deterministic,
                NavigationNames = new HashSet<string>(),
            },
            ContextType = null,
            EfType = null,
            Repository = RepositoryBinding.Find(logicalName, type, profile, entityConfig, assemblies),
            ReadRepository = RepositoryBinding.FindRead(logicalName, profile, assemblies),
            Faker = FakerBinding.Find(logicalName, type, profile, assemblies),
            AutoFakerSkip = keyProperty is null
                ? new HashSet<string>()
                : new HashSet<string> { keyProperty.Name },
        };
    }

    private static bool IsIntegral(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(short);
    }
}
