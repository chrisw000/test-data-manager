using Tdm.Core.Manifest;

namespace Tdm.Core.Benchmarks;

/// <summary>One perf gate (W3-D8), declared in the policy file's environment block:
/// <c>"benchmarks": { "gates": [ { "operation": "create:Order", "stat": "p95Ms",
/// "maxRegressionPct": 20 } ] }</c> — evaluated by `tdm bench compare` against a baseline.</summary>
public sealed class BenchmarkGate
{
    /// <summary>Benchmark key: an operation ("create") or operation:Entity ("create:Order").</summary>
    public string Operation { get; set; } = "";
    /// <summary>meanMs | p50Ms | p95Ms | maxMs | totalMs (case-insensitive).</summary>
    public string Stat { get; set; } = "p95Ms";
    /// <summary>Fail when current exceeds baseline by more than this percentage.</summary>
    public double MaxRegressionPct { get; set; } = 20;
}

public sealed record GateResult(BenchmarkGate Gate, double? BaselineMs, double? CurrentMs,
    double? RegressionPct, bool Passed, string Message);

public sealed record ComparisonRow(string Operation, double? BaselineMs, double? CurrentMs, double? RegressionPct);

/// <summary>
/// Pure benchmark comparison (W3-D8): current run stats vs a baseline (a pinned manifest, or
/// the per-stat rolling median of the last N stored runs — medians absorb CI agent noise).
/// </summary>
public static class BenchmarkComparer
{
    public static double? Stat(BenchmarkStats stats, string stat) => stat.ToLowerInvariant() switch
    {
        "meanms" => stats.MeanMs,
        "p50ms" => stats.P50Ms,
        "p95ms" => stats.P95Ms,
        "maxms" => stats.MaxMs,
        "totalms" => stats.TotalMs,
        _ => null,
    };

    /// <summary>Per-key, per-stat median across runs — a key contributes wherever it appears.</summary>
    public static Dictionary<string, BenchmarkStats> MedianBaseline(
        IReadOnlyList<Dictionary<string, BenchmarkStats>> runs)
    {
        var baseline = new Dictionary<string, BenchmarkStats>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in runs.SelectMany(r => r.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var samples = runs.Where(r => r.ContainsKey(key)).Select(r => r[key]).ToList();
            baseline[key] = new BenchmarkStats
            {
                Count = (int)Math.Round(Median(samples.Select(s => (double)s.Count))),
                TotalMs = Median(samples.Select(s => s.TotalMs)),
                MeanMs = Median(samples.Select(s => s.MeanMs)),
                P50Ms = Median(samples.Select(s => s.P50Ms)),
                P95Ms = Median(samples.Select(s => s.P95Ms)),
                MaxMs = Median(samples.Select(s => s.MaxMs)),
            };
        }
        return baseline;
    }

    /// <summary>Full comparison of every key either side knows, for the human-readable table.</summary>
    public static List<ComparisonRow> Compare(
        IReadOnlyDictionary<string, BenchmarkStats> current,
        IReadOnlyDictionary<string, BenchmarkStats> baseline,
        string stat = "p95Ms")
    {
        return current.Keys.Union(baseline.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Select(key =>
            {
                var currentMs = current.TryGetValue(key, out var c) ? Stat(c, stat) : null;
                var baselineMs = baseline.TryGetValue(key, out var b) ? Stat(b, stat) : null;
                return new ComparisonRow(key, baselineMs, currentMs, Regression(baselineMs, currentMs));
            })
            .ToList();
    }

    /// <summary>Evaluates the policy gates. Missing data never fails a gate — a first run has
    /// no baseline, and an operation absent from the current run regressed nothing; both pass
    /// with the reason in the message.</summary>
    public static List<GateResult> EvaluateGates(
        IReadOnlyDictionary<string, BenchmarkStats> current,
        IReadOnlyDictionary<string, BenchmarkStats> baseline,
        IReadOnlyList<BenchmarkGate> gates)
    {
        var results = new List<GateResult>();
        foreach (var gate in gates)
        {
            var currentMs = current.TryGetValue(gate.Operation, out var c) ? Stat(c, gate.Stat) : null;
            var baselineMs = baseline.TryGetValue(gate.Operation, out var b) ? Stat(b, gate.Stat) : null;

            if (currentMs is null || baselineMs is null)
            {
                var reason = Stat(new BenchmarkStats(), gate.Stat) is null
                    ? $"unknown stat '{gate.Stat}' (use meanMs, p50Ms, p95Ms, maxMs or totalMs)"
                    : currentMs is null
                        ? $"'{gate.Operation}' not present in the current run"
                        : $"no baseline data for '{gate.Operation}'";
                results.Add(new GateResult(gate, baselineMs, currentMs, null, Passed: true, $"skipped: {reason}"));
                continue;
            }

            var regression = Regression(baselineMs, currentMs)!.Value;
            var passed = regression <= gate.MaxRegressionPct;
            results.Add(new GateResult(gate, baselineMs, currentMs, regression, passed,
                passed
                    ? $"{gate.Operation} {gate.Stat} {currentMs:0.###} ms vs baseline {baselineMs:0.###} ms ({Signed(regression)}%, max +{gate.MaxRegressionPct}%)"
                    : $"{gate.Operation} {gate.Stat} regressed {Signed(regression)}% ({baselineMs:0.###} ms → {currentMs:0.###} ms), exceeding the +{gate.MaxRegressionPct}% gate"));
        }
        return results;
    }

    private static double? Regression(double? baselineMs, double? currentMs) =>
        baselineMs is > 0 && currentMs is { } current ? Math.Round((current - baselineMs.Value) / baselineMs.Value * 100, 1) : null;

    private static string Signed(double pct) => pct >= 0 ? $"+{pct:0.#}" : $"{pct:0.#}";

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0) return 0;
        var mid = sorted.Count / 2;
        return Math.Round(sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2, 3);
    }
}
