using System.Text.Json;
using Tdm.Core.SeedPacks;
using Tdm.Core.Settings;

namespace Tdm.Core.Profiling;

/// <summary>
/// The statistics pack (W4-D8 spike): per-column distributions, cardinalities and
/// correlation hints from a production-like source — <b>never row values</b>. The only
/// captured literals are category labels of explicitly low-cardinality columns (≤ the
/// categorical threshold), and even those can be suppressed (`--no-values`). §2.3's
/// distribution config consumes the pack via <see cref="ToFragment"/>: synthetic-but-
/// realistic without copying data, and the W2 attestation stays truthful because consuming
/// runs declare the pack in their attribution.
/// </summary>
public sealed class StatsPack
{
    public int StatsVersion { get; set; } = 1;
    public DateTime GeneratedUtc { get; set; }
    /// <summary>Rows sampled per entity (upper bound; small tables profile fully).</summary>
    public int SampleRows { get; set; }
    /// <summary>True when category labels were suppressed (`--no-values`): cardinalities and
    /// numeric shapes only.</summary>
    public bool ValuesSuppressed { get; set; }
    public Dictionary<string, EntityStats> Entities { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string Serialize() => JsonSerializer.Serialize(this, TdmSettings.JsonOptions);

    public static StatsPack Load(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<StatsPack>(stream, TdmSettings.JsonOptions)
               ?? throw new InvalidOperationException($"Stats pack '{path}' deserialised to null.");
    }

    /// <summary>The §2.3 hand-off: an entities-config fragment (the seed-pack fragment shape)
    /// carrying each profiled property's suggested weights/distribution — paste into
    /// tdm.settings.json or ship inside a seed pack (W4-D7).</summary>
    public SeedPackFragment ToFragment()
    {
        var fragment = new SeedPackFragment();
        foreach (var (entityName, entity) in Entities.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            var properties = new Dictionary<string, PropertyGenerationSettings>(StringComparer.OrdinalIgnoreCase);
            foreach (var (propertyName, property) in entity.Properties.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (property.Suggested is { } suggested)
                    properties[propertyName] = suggested;
            }
            if (properties.Count > 0)
                fragment.Entities[entityName] = new EntitySettings { Properties = properties };
        }
        return fragment;
    }
}

public sealed class EntityStats
{
    public string Domain { get; set; } = "";
    /// <summary>Rows actually sampled for this entity.</summary>
    public int Rows { get; set; }
    public Dictionary<string, PropertyStats> Properties { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Near-functional-dependency column pairs (city↔postcode style) — candidates
    /// for a W4-D5 correlated dataset.</summary>
    public List<string[]> CorrelationHints { get; set; } = [];
}

public sealed class PropertyStats
{
    public string Type { get; set; } = "";
    public int Count { get; set; }
    public int NullCount { get; set; }
    public int Distinct { get; set; }
    // Numeric moments (absent for non-numeric columns).
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Mean { get; set; }
    public double? Median { get; set; }
    public double? StdDev { get; set; }
    /// <summary>Relative frequencies for low-cardinality categorical columns — the only
    /// place source literals appear, and only under the categorical threshold.</summary>
    public Dictionary<string, double>? Weights { get; set; }
    /// <summary>Ready-to-use §2.3 config: the fitted distribution or the observed weights.</summary>
    public PropertyGenerationSettings? Suggested { get; set; }
}
