using System.Reflection;
using Bogus;
using Tdm.Core.Conversion;
using Tdm.Core.Execution;
using Tdm.Core.Naming;
using Tdm.Core.Settings;

namespace Tdm.Core.Generation;

/// <summary>What the statistical layer changed — drives the manifest's fakerSource markers.</summary>
public readonly record struct StatApplication(bool Distributions, bool Datasets)
{
    public bool Any => Distributions || Datasets;
}

/// <summary>
/// Applies config-declared statistical generation (W4-D4/W4-D5) over a freshly generated
/// instance: weighted categoricals, numeric distributions, and correlated dataset columns
/// (one sampled row per dataset per entity, so tuples stay consistent). Runs after the
/// faker and before overrides — declarative intent holds regardless of faker, overrides
/// still win. Draw order is fixed (datasets ordinal by name, then properties ordinal by
/// name) so the per-scenario Randomizer sequence is reproducible.
/// </summary>
public sealed class StatisticalGenerator(TdmSettings settings)
{
    private readonly DatasetStore _datasets = new(settings.Datasets, settings.BaseDirectory);

    public StatApplication Apply(EntityDescriptor descriptor, object instance, Randomizer random)
    {
        var config = settings.EntityFor(descriptor.LogicalName).Properties;
        if (config.Count == 0) return default;

        var resolved = config
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => (Config: p.Value, Property: ResolveProperty(descriptor, p.Key)))
            .ToList();

        var applied = new StatApplication();

        // Correlated datasets first: one row draw per dataset, all its columns from that row.
        foreach (var group in resolved.Where(p => p.Config.Dataset is not null)
                     .GroupBy(p => p.Config.Dataset!, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var table = _datasets.Get(group.Key);
            var row = table.Rows[random.Int(0, table.Rows.Count - 1)];
            foreach (var (propConfig, property) in group)
            {
                var column = propConfig.Column ?? property.Name;
                Assign(descriptor, property, row[table.ColumnIndex(column)]);
            }
            applied = applied with { Datasets = true };
        }

        foreach (var (propConfig, property) in resolved)
        {
            if (propConfig.Weights is { } weights)
            {
                Assign(descriptor, property, Distributions.SampleWeighted(weights, random));
                applied = applied with { Distributions = true };
            }
            else if (propConfig.Distribution is not null)
            {
                property.SetValue(instance, ConvertNumeric(descriptor, property,
                    Distributions.Sample(propConfig, random), propConfig.Decimals));
                applied = applied with { Distributions = true };
            }
        }
        return applied;

        void Assign(EntityDescriptor entity, PropertyInfo property, string raw)
        {
            if (!ValueConverter.TryConvert(raw, property.PropertyType, out var value, out var error))
            {
                throw new InvalidOperationException(
                    $"{entity.LogicalName}.{property.Name}: cannot convert '{raw}' to {property.PropertyType.Name}: {error}");
            }
            property.SetValue(instance, value);
        }
    }

    private static PropertyInfo ResolveProperty(EntityDescriptor descriptor, string configuredName)
    {
        var property = descriptor.ScalarProperties()
            .FirstOrDefault(p => NameMatcher.Matches(configuredName, p.Name));
        if (property is null or { CanWrite: false })
        {
            throw new InvalidOperationException(
                $"entities.{descriptor.LogicalName}.properties: '{configuredName}' matches no writable property on " +
                $"{descriptor.ClrType.Name}. Properties: {string.Join(", ", descriptor.ScalarProperties().Select(p => p.Name))}.");
        }
        return property;
    }

    private static object ConvertNumeric(EntityDescriptor descriptor, PropertyInfo property,
        double value, int? decimals)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        var rounded = Math.Round(value, decimals ?? 2);
        if (type == typeof(decimal)) return (decimal)rounded;
        if (type == typeof(double)) return rounded;
        if (type == typeof(float)) return (float)rounded;
        if (type == typeof(int)) return (int)Math.Round(value);
        if (type == typeof(long)) return (long)Math.Round(value);
        if (type == typeof(short)) return (short)Math.Round(value);
        throw new InvalidOperationException(
            $"{descriptor.LogicalName}.{property.Name}: a distribution needs a numeric property " +
            $"({property.PropertyType.Name} is not) — use \"weights\" for categorical values.");
    }
}
