using Tdm.Core.Naming;

namespace Tdm.Core.Execution;

/// <summary>
/// Per-scenario registry of created entities and external references, keyed on
/// (logical entity name, natural key). First stop for reference resolution (handoff §8) —
/// a hit here is fully deterministic.
/// </summary>
public sealed class ScenarioContextBag
{
    public sealed class Entry
    {
        public required string EntityName { get; init; }
        public required string Key { get; init; }
        /// <summary>Local instance; null for external references (only the identity is known).</summary>
        public object? Instance { get; init; }
        public object? Id { get; init; }
        public string? DomainName { get; init; }
        public bool IsExternal { get; init; }
    }

    private readonly Dictionary<(string Entity, string Key), Entry> _entries = [];

    public void AddCreated(EntityDescriptor descriptor, string naturalKey, object instance, object? id) =>
        _entries[(NameMatcher.Normalize(descriptor.LogicalName), naturalKey)] = new Entry
        {
            EntityName = descriptor.LogicalName,
            Key = naturalKey,
            Instance = instance,
            Id = id,
            DomainName = descriptor.DomainName,
        };

    public void AddExternal(string entityName, string naturalKey, Guid id, string sourceDomain) =>
        _entries[(NameMatcher.Normalize(entityName), naturalKey)] = new Entry
        {
            EntityName = entityName,
            Key = naturalKey,
            Id = id,
            DomainName = sourceDomain,
            IsExternal = true,
        };

    public bool TryGet(string entityName, string naturalKey, out Entry entry)
    {
        if (_entries.TryGetValue((NameMatcher.Normalize(entityName), naturalKey), out entry!))
            return true;
        // Tolerate singular/plural mismatch between the create step and the reference clause.
        return _entries.TryGetValue((NameMatcher.Normalize(NameMatcher.Singularize(entityName)), naturalKey), out entry!);
    }
}
