using System.Globalization;
using Tdm.Core.Conversion;
using Tdm.Core.Execution;
using Tdm.Core.Naming;
using Tdm.Core.Settings;

namespace Tdm.Core.Tests.Execution;

public sealed class Widget
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Colour { get; set; } = "";
    public int Size { get; set; }
}

public sealed class Gadget
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public Guid WidgetId { get; set; }
}

/// <summary>In-memory IDomainRuntime: lets engine tests exercise policies, references,
/// identity and lifecycle orchestration without EF or a database.</summary>
public sealed class FakeDomainRuntime(string name, params EntityDescriptor[] descriptors) : IDomainRuntime
{
    public string Name { get; } = name;
    public DomainSettings Settings { get; } = new() { Name = name };
    public IReadOnlyList<EntityDescriptor> Entities { get; } = descriptors;
    public IReadOnlyList<string> Warnings => [];
    public IReadOnlyList<string> PolicyViolations => [];

    public Dictionary<string, List<object>> Store { get; } =
        descriptors.ToDictionary(d => d.LogicalName, _ => new List<object>());
    public List<string> Calls { get; } = [];
    public bool FailCreates { get; set; }

    public static EntityDescriptor Describe<T>(string logicalName, string domain, string naturalKey = "Name") => new()
    {
        LogicalName = logicalName,
        DomainName = domain,
        ClrType = typeof(T),
        KeyProperty = typeof(T).GetProperty("Id"),
        NaturalKeyProperty = typeof(T).GetProperty(naturalKey),
        IdStrategy = IdStrategy.Deterministic,
        NavigationNames = [],
    };

    private List<object> Rows(EntityDescriptor d) => Store[d.LogicalName];

    public bool TryResolveEntity(string gherkinName, out EntityDescriptor? entity, out string? error)
    {
        error = null;
        entity = Entities.FirstOrDefault(d => NameMatcher.Matches(gherkinName, d.LogicalName));
        return entity is not null;
    }

    public IReadOnlyList<EntityResolutionInfo> DescribeEntities() => [];

    public Task BeginScenarioAsync(LifecycleMode lifecycle, int seed, CancellationToken ct = default)
    {
        Calls.Add($"begin:{lifecycle}:{seed}");
        return Task.CompletedTask;
    }

    public Task<ScenarioCloseOutcome> EndScenarioAsync(CancellationToken ct = default)
    {
        Calls.Add("end");
        return Task.FromResult(new ScenarioCloseOutcome());
    }

    public object Generate(EntityDescriptor entity, out string fakerSource, List<string> warnings)
    {
        fakerSource = "fake";
        return Activator.CreateInstance(entity.ClrType)!;
    }

    public Task<PersistOutcome> CreateAsync(EntityDescriptor entity, object instance, bool forceDbContext = false, CancellationToken ct = default)
    {
        Calls.Add($"create:{entity.LogicalName}");
        if (FailCreates) return Task.FromResult(PersistOutcome.Fail("boom"));
        Rows(entity).Add(instance);
        return Task.FromResult(PersistOutcome.Ok("FakeStore"));
    }

    public Task<PersistOutcome> CreateBulkAsync(EntityDescriptor entity, IReadOnlyList<object> instances, int chunkSize, CancellationToken ct = default)
    {
        Calls.Add($"createBulk:{entity.LogicalName}:{instances.Count}");
        Rows(entity).AddRange(instances);
        return Task.FromResult(PersistOutcome.Ok("FakeStore(bulk)"));
    }

    public Task<PersistOutcome> UpdateAsync(EntityDescriptor entity, object instance, CancellationToken ct = default)
    {
        Calls.Add($"update:{entity.LogicalName}");
        return Task.FromResult(PersistOutcome.Ok("FakeStore"));
    }

    public Task<PersistOutcome> DeleteAsync(EntityDescriptor entity, object instance, CancellationToken ct = default)
    {
        Calls.Add($"delete:{entity.LogicalName}");
        Rows(entity).Remove(instance);
        return Task.FromResult(PersistOutcome.Ok("FakeStore"));
    }

    public Task<int> DeleteWhereAsync(EntityDescriptor entity, IReadOnlyList<PropertyFilter> filters, CancellationToken ct = default)
    {
        var matches = Rows(entity).Where(row => Matches(row, filters)).ToList();
        foreach (var row in matches) Rows(entity).Remove(row);
        return Task.FromResult(matches.Count);
    }

    public Task<object?> FindByNaturalKeyAsync(EntityDescriptor entity, string naturalKey, CancellationToken ct = default) =>
        Task.FromResult(Rows(entity).FirstOrDefault(row => entity.GetNaturalKey(row) == naturalKey));

    public Task<object?> FindByIdAsync(EntityDescriptor entity, string id, CancellationToken ct = default) =>
        Task.FromResult(Rows(entity).FirstOrDefault(row =>
            string.Equals(Convert.ToString(entity.GetKey(row), CultureInfo.InvariantCulture), id, StringComparison.OrdinalIgnoreCase)));

    public Task<int> CountAsync(EntityDescriptor entity, IReadOnlyList<PropertyFilter> filters, CancellationToken ct = default) =>
        Task.FromResult(Rows(entity).Count(row => Matches(row, filters)));

    public bool TrySetReference(object instance, EntityDescriptor entity, string referencedEntityName,
        EntityDescriptor? referencedDescriptor, object? referencedInstance, object? referencedId, out string? error)
    {
        error = null;
        var property = PropertyMatcher.Find(entity.ClrType, referencedEntityName + "Id");
        if (property is not null && referencedId is not null)
        {
            property.SetValue(instance, referencedId);
            return true;
        }
        error = $"no {referencedEntityName}Id property";
        return false;
    }

    public Task<bool> DeleteByIdAsync(string logicalEntityName, string id, CancellationToken ct = default)
    {
        TryResolveEntity(logicalEntityName, out var entity, out _);
        var row = Rows(entity!).FirstOrDefault(r =>
            string.Equals(Convert.ToString(entity!.GetKey(r), CultureInfo.InvariantCulture), id, StringComparison.OrdinalIgnoreCase));
        if (row is null) return Task.FromResult(false);
        Rows(entity!).Remove(row);
        return Task.FromResult(true);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static bool Matches(object row, IReadOnlyList<PropertyFilter> filters) =>
        filters.All(f => Equals(f.Property.GetValue(row), f.Value));
}
