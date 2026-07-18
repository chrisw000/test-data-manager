using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tdm.Core.Naming;
using Tdm.Core.Profiling;
using Tdm.Core.Settings;

namespace Tdm.EfCore.Profiling;

public sealed record ProfileOptions(int SampleRows = 10_000, int CategoricalMax = 10, bool IncludeValues = true);

/// <summary>
/// The `tdm profile` prototype (W4-D8 spike): connects <b>read-only</b> to a
/// production-like source and computes per-column statistics over a bounded sample —
/// numeric moments with a fitted distribution suggestion, low-cardinality categorical
/// weights, and correlation hints. Never emits row values: Guid/high-cardinality/free-text
/// columns contribute cardinalities only, and `--no-values` suppresses category labels too.
/// Point it at a replica; the only SQL issued is per-table `SELECT TOP(n)`-style sampling.
/// </summary>
public static class StatsProfiler
{
    private static readonly MethodInfo SetMethod =
        typeof(DbContext).GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!;

    public static StatsPack Profile(DomainSettings domain, TdmSettings root,
        IReadOnlyList<Assembly> assemblies, ProfileOptions options, ILogger? logger = null)
    {
        var profile = root.ProfileFor(domain);
        var pack = new StatsPack
        {
            GeneratedUtc = DateTime.UtcNow,
            SampleRows = options.SampleRows,
            ValuesSuppressed = !options.IncludeValues,
        };

        var contextTypes = assemblies
            .SelectMany(DbContextActivator.SafeGetTypes)
            .Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true } && typeof(DbContext).IsAssignableFrom(t))
            .ToList();
        if (contextTypes.Count == 0)
            throw new InvalidOperationException($"Domain '{domain.Name}': no public DbContext subclass in the plugin assemblies.");

        foreach (var contextType in contextTypes)
        {
            using var context = DbContextActivator.Activate(contextType, domain, assemblies);
            context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
            context.ChangeTracker.AutoDetectChangesEnabled = false;

            foreach (var entityType in context.Model.GetEntityTypes())
            {
                if (entityType.IsOwned()) continue;
                var logicalName = NameMatcher.StripPattern(entityType.ClrType.Name, profile.EntityClassPattern);
                var rows = SampleRows(context, entityType.ClrType, options.SampleRows);
                pack.Entities[logicalName] = ProfileEntity(domain.Name, entityType, rows, options);
                logger?.LogInformation("Profiled {Domain}.{Entity}: {Rows} row(s) sampled",
                    domain.Name, logicalName, pack.Entities[logicalName].Rows);
            }
        }
        return pack;
    }

    private static List<object> SampleRows(DbContext context, Type clrType, int take)
    {
        var set = (IQueryable)SetMethod.MakeGenericMethod(clrType).Invoke(context, null)!;
        var bounded = set.Provider.CreateQuery(
            Expression.Call(typeof(Queryable), nameof(Queryable.Take), [clrType],
                set.Expression, Expression.Constant(take)));
        return [.. bounded.Cast<object>()];
    }

    private static EntityStats ProfileEntity(string domainName,
        Microsoft.EntityFrameworkCore.Metadata.IEntityType entityType, List<object> rows, ProfileOptions options)
    {
        var stats = new EntityStats { Domain = domainName, Rows = rows.Count };
        var categoricalColumns = new List<(string Name, List<string?> Values)>();

        foreach (var property in entityType.GetProperties())
        {
            // Shadow properties have no CLR member to read; keys/FKs are identity, not shape.
            if (property.PropertyInfo is not { } clrProperty) continue;
            if (property.IsKey() || property.IsForeignKey()) continue;

            var values = rows.Select(clrProperty.GetValue).ToList();
            var propertyStats = ProfileProperty(clrProperty, values, options, out var categoricalValues);
            stats.Properties[clrProperty.Name] = propertyStats;
            if (categoricalValues is not null)
                categoricalColumns.Add((clrProperty.Name, categoricalValues));
        }

        // Correlation hints (W4-D5 candidates): a near-functional dependency between two
        // low-cardinality columns — |distinct pairs| barely above the larger side.
        for (var i = 0; i < categoricalColumns.Count; i++)
        for (var j = i + 1; j < categoricalColumns.Count; j++)
        {
            var (nameA, a) = categoricalColumns[i];
            var (nameB, b) = categoricalColumns[j];
            var distinctA = a.Distinct().Count();
            var distinctB = b.Distinct().Count();
            if (distinctA < 2 || distinctB < 2) continue;
            var pairDistinct = a.Zip(b).Distinct().Count();
            if (pairDistinct <= Math.Max(distinctA, distinctB) * 1.2)
                stats.CorrelationHints.Add([nameA, nameB]);
        }
        return stats;
    }

    private static PropertyStats ProfileProperty(PropertyInfo clrProperty, List<object?> values,
        ProfileOptions options, out List<string?>? categoricalValues)
    {
        categoricalValues = null;
        var type = Nullable.GetUnderlyingType(clrProperty.PropertyType) ?? clrProperty.PropertyType;
        var nonNull = values.Where(v => v is not null).ToList();
        var stats = new PropertyStats
        {
            Type = type.Name,
            Count = values.Count,
            NullCount = values.Count - nonNull.Count,
            Distinct = nonNull.Distinct().Count(),
        };

        if (IsNumeric(type))
        {
            var numbers = nonNull.Select(v => Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture))
                .OrderBy(v => v).ToList();
            if (numbers.Count > 0)
            {
                stats.Min = Round(numbers[0]);
                stats.Max = Round(numbers[^1]);
                stats.Mean = Round(numbers.Average());
                stats.Median = Round(numbers[numbers.Count / 2]);
                var mean = numbers.Average();
                var stdDev = Math.Sqrt(numbers.Average(v => Math.Pow(v - mean, 2)));
                stats.StdDev = Round(stdDev);
                stats.Suggested = SuggestDistribution(numbers, mean, stdDev);
            }
            return stats;
        }

        if (type == typeof(string) || type == typeof(bool) || type.IsEnum)
        {
            var labels = values.Select(v => v?.ToString()).ToList();
            if (stats.Distinct <= options.CategoricalMax)
            {
                // The one place source literals can appear — explicitly low-cardinality
                // category labels, and only when values are not suppressed.
                categoricalValues = labels;
                if (options.IncludeValues && nonNull.Count > 0)
                {
                    stats.Weights = labels.Where(l => l is not null)
                        .GroupBy(l => l!, StringComparer.Ordinal)
                        .OrderBy(g => g.Key, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => Round((double)g.Count() / nonNull.Count));
                    stats.Suggested = new PropertyGenerationSettings { Weights = stats.Weights };
                }
            }
            return stats;
        }

        // Guid, DateTime, byte[], … — cardinality only. Ids and timestamps are identity and
        // recency, not shape; capturing them would leak row-level data.
        return stats;
    }

    /// <summary>Heuristic fit: non-negative right-skewed data reads as lognormal (mean = the
    /// sample median — §2.3's scale convention); anything else as normal. Uniform and
    /// exponential remain manual choices — the fragment is a starting point, not an oracle.</summary>
    private static PropertyGenerationSettings? SuggestDistribution(List<double> sorted, double mean, double stdDev)
    {
        if (sorted.Count < 20 || stdDev == 0) return null;
        var skewness = sorted.Average(v => Math.Pow((v - mean) / stdDev, 3));
        if (sorted[0] >= 0 && skewness > 1)
        {
            var positives = sorted.Where(v => v > 0).Select(v => Math.Log(v)).ToList();
            var logMean = positives.Average();
            var logSigma = Math.Sqrt(positives.Average(v => Math.Pow(v - logMean, 2)));
            return new PropertyGenerationSettings
            {
                Distribution = "lognormal",
                Mean = Round(sorted[sorted.Count / 2]),
                Sigma = Round(logSigma),
                Min = 0,
            };
        }
        return new PropertyGenerationSettings
        {
            Distribution = "normal",
            Mean = Round(mean),
            Sigma = Round(stdDev),
        };
    }

    private static bool IsNumeric(Type type) =>
        type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) ||
        type == typeof(double) || type == typeof(float) || type == typeof(decimal);

    private static double Round(double value) => Math.Round(value, 4);
}
