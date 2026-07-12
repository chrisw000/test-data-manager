using System.Globalization;
using System.Reflection;
using Tdm.Core.Settings;

namespace Tdm.Core.Execution;

/// <summary>
/// A resolved entity: the bridge between a Gherkin logical name ("Customer") and the CLR
/// type mapped in a domain's model ("CustomerEntity"). Built by the EF integration layer.
/// </summary>
public sealed class EntityDescriptor
{
    /// <summary>Convention-stripped logical name, e.g. "Customer".</summary>
    public required string LogicalName { get; init; }
    public required string DomainName { get; init; }
    public required Type ClrType { get; init; }
    public PropertyInfo? KeyProperty { get; init; }
    public bool KeyIsDbGenerated { get; init; }
    public PropertyInfo? NaturalKeyProperty { get; init; }
    /// <summary>Effective strategy after config + EF metadata detection (handoff §7).</summary>
    public IdStrategy IdStrategy { get; init; } = IdStrategy.Auto;
    /// <summary>Navigation/collection property names — skipped by auto-faker and value snapshots.</summary>
    public IReadOnlyCollection<string> NavigationNames { get; init; } = [];

    public bool HasClientSettableGuidKey =>
        KeyProperty is not null && !KeyIsDbGenerated &&
        (KeyProperty.PropertyType == typeof(Guid) || KeyProperty.PropertyType == typeof(Guid?));

    public object? GetKey(object instance) => KeyProperty?.GetValue(instance);

    public void SetKey(object instance, object value) => KeyProperty?.SetValue(instance, value);

    public string? GetNaturalKey(object instance) =>
        NaturalKeyProperty?.GetValue(instance) is { } v
            ? Convert.ToString(v, CultureInfo.InvariantCulture)
            : null;

    /// <summary>Scalar (non-navigation) readable properties, for manifest value snapshots.</summary>
    public IEnumerable<PropertyInfo> ScalarProperties()
    {
        foreach (var prop in ClrType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || NavigationNames.Contains(prop.Name)) continue;
            yield return prop;
        }
    }

    public Dictionary<string, string?> SnapshotValues(object instance)
    {
        var values = new Dictionary<string, string?>();
        foreach (var prop in ScalarProperties())
        {
            object? v;
            try { v = prop.GetValue(instance); }
            catch { continue; }
            values[prop.Name] = v switch
            {
                null => null,
                DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
                DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => v.ToString(),
            };
        }
        return values;
    }
}

/// <summary>Equality filter used for delete-all / load-count queries.</summary>
public sealed record PropertyFilter(PropertyInfo Property, object? Value);
