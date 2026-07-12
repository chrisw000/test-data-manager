using Tdm.Core.Settings;

namespace Tdm.Core.Execution;

public sealed class PersistOutcome
{
    public bool Success { get; init; }
    /// <summary>Route taken, e.g. "ICustomerRepository.AddAsync", "DbContext", "DbContext(bulk)".</summary>
    public string? Route { get; init; }
    public string? Error { get; init; }

    public static PersistOutcome Ok(string route) => new() { Success = true, Route = route };
    public static PersistOutcome Fail(string error, string? route = null) => new() { Success = false, Error = error, Route = route };
}

public sealed class ScenarioCloseOutcome
{
    public int Deleted { get; set; }
    public List<string> Orphaned { get; set; } = [];
    public string? Error { get; set; }
}

/// <summary>Per-entity resolution map entry, for `tdm list-entities` and `validate`.</summary>
public sealed record EntityResolutionInfo(
    string LogicalName, string ClrType, string? NaturalKey, string KeyInfo,
    string? Repository, string FakerSource, string PersistRoute);

/// <summary>
/// Everything the engine needs from a loaded domain: resolution, generation, persistence,
/// lifecycle. Implemented by Tdm.EfCore's DomainRuntime; Tdm.Core stays EF-free.
/// </summary>
public interface IDomainRuntime : IAsyncDisposable
{
    string Name { get; }
    DomainSettings Settings { get; }
    IReadOnlyList<EntityDescriptor> Entities { get; }

    /// <summary>Warnings raised while building the runtime (unresolvable repo dependencies etc.).</summary>
    IReadOnlyList<string> Warnings { get; }

    bool TryResolveEntity(string gherkinName, out EntityDescriptor? entity, out string? error);

    IReadOnlyList<EntityResolutionInfo> DescribeEntities();

    Task BeginScenarioAsync(LifecycleMode lifecycle, int seed, CancellationToken ct = default);

    /// <summary>Commits/rolls back/tears down per the scenario's lifecycle mode.</summary>
    Task<ScenarioCloseOutcome> EndScenarioAsync(CancellationToken ct = default);

    /// <summary>
    /// Generates an instance via the convention faker or the heuristic auto-faker.
    /// Deterministic per scenario seed; entities consume the faker sequence in step order.
    /// </summary>
    object Generate(EntityDescriptor entity, out string fakerSource, List<string> warnings);

    Task<PersistOutcome> CreateAsync(EntityDescriptor entity, object instance, bool forceDbContext = false, CancellationToken ct = default);
    Task<PersistOutcome> CreateBulkAsync(EntityDescriptor entity, IReadOnlyList<object> instances, int chunkSize, CancellationToken ct = default);
    Task<PersistOutcome> UpdateAsync(EntityDescriptor entity, object instance, CancellationToken ct = default);
    Task<PersistOutcome> DeleteAsync(EntityDescriptor entity, object instance, CancellationToken ct = default);
    Task<int> DeleteWhereAsync(EntityDescriptor entity, IReadOnlyList<PropertyFilter> filters, CancellationToken ct = default);

    Task<object?> FindByNaturalKeyAsync(EntityDescriptor entity, string naturalKey, CancellationToken ct = default);
    Task<int> CountAsync(EntityDescriptor entity, IReadOnlyList<PropertyFilter> filters, CancellationToken ct = default);

    /// <summary>
    /// Wires a reference onto <paramref name="instance"/>: sets the FK property when EF
    /// metadata exposes one, else the navigation, else a {Entity}Id name-convention property.
    /// </summary>
    bool TrySetReference(object instance, EntityDescriptor entity, string referencedEntityName,
        EntityDescriptor? referencedDescriptor, object? referencedInstance, object? referencedId, out string? error);

    /// <summary>Deletes a row recorded in a manifest (playback teardown). Returns false when the row is gone.</summary>
    Task<bool> DeleteByIdAsync(string logicalEntityName, string id, CancellationToken ct = default);
}
