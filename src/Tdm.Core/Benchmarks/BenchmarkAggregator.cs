using Tdm.Core.Manifest;

namespace Tdm.Core.Benchmarks;

/// <summary>
/// Stopwatch-based aggregation (handoff §12) — deliberately not BenchmarkDotNet, this is
/// IO-bound integration work. Percentiles use the nearest-rank method.
/// </summary>
public sealed class BenchmarkAggregator
{
    public readonly record struct Sample(string Operation, string Entity, double Ms);

    private readonly List<Sample> _samples = [];
    private readonly Lock _lock = new();

    public void Record(string operation, string entity, double ms)
    {
        lock (_lock) _samples.Add(new Sample(operation, entity, ms));
    }

    public IReadOnlyList<Sample> Samples { get { lock (_lock) return [.. _samples]; } }

    /// <summary>Stats keyed by operation (create/update/delete/load/resolve/generate/override/persist).</summary>
    public Dictionary<string, BenchmarkStats> ByOperation() => Summarize(s => s.Operation);

    /// <summary>Stats keyed by "operation:Entity".</summary>
    public Dictionary<string, BenchmarkStats> ByOperationAndEntity() => Summarize(s => $"{s.Operation}:{s.Entity}");

    public Dictionary<string, BenchmarkStats> Summarize(Func<Sample, string> keySelector)
    {
        List<Sample> snapshot;
        lock (_lock) snapshot = [.. _samples];

        return snapshot
            .GroupBy(keySelector)
            .ToDictionary(g => g.Key, g => Compute(g.Select(s => s.Ms).ToList()));
    }

    public void MergeInto(BenchmarkAggregator target)
    {
        List<Sample> snapshot;
        lock (_lock) snapshot = [.. _samples];
        foreach (var s in snapshot) target.Record(s.Operation, s.Entity, s.Ms);
    }

    public static BenchmarkStats Compute(List<double> values)
    {
        if (values.Count == 0) return new BenchmarkStats();
        values.Sort();
        return new BenchmarkStats
        {
            Count = values.Count,
            TotalMs = Math.Round(values.Sum(), 3),
            MeanMs = Math.Round(values.Average(), 3),
            P50Ms = Math.Round(Percentile(values, 0.50), 3),
            P95Ms = Math.Round(Percentile(values, 0.95), 3),
            MaxMs = Math.Round(values[^1], 3),
        };
    }

    private static double Percentile(List<double> sorted, double p)
    {
        var rank = (int)Math.Ceiling(p * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }
}
