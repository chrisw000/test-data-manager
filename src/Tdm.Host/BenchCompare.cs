using Tdm.Core.Benchmarks;
using Tdm.Core.Manifest;
using Tdm.Observability;
using Tdm.Observability.Reports;
using Tdm.Observability.Trends;

namespace Tdm.Host;

/// <summary>
/// `tdm bench compare` (W3-D8): current manifest vs a baseline — a pinned manifest, or the
/// rolling median of the last N runs in the trend store (W3-D7) — evaluated against the
/// policy file's perf gates. One enforcement pipeline: the gates live in tdm.policy.json
/// next to the volume caps. Standalone file work; no plugins or database are touched.
/// </summary>
internal static class BenchCompare
{
    public static async Task<int> ExecuteAsync(string manifestPath, string? baselinePath, string? storePath,
        int baselineRuns, string? environmentName, string policyFilePath, string stat, bool quarantine,
        IReadOnlyList<(string Format, string Path)> reports, CancellationToken ct)
    {
        var current = ManifestWriter.Read(Path.GetFullPath(manifestPath));

        if ((baselinePath is null) == (storePath is null))
            throw new InvalidOperationException("Give exactly one baseline source: --baseline <manifest> or --store <root>.");

        IReadOnlyDictionary<string, BenchmarkStats> baseline;
        string baselineDescription;
        if (baselinePath is not null)
        {
            var pinned = ManifestWriter.Read(Path.GetFullPath(baselinePath));
            baseline = pinned.Run.Benchmark;
            baselineDescription = $"pinned manifest {Path.GetFileName(baselinePath)}";
        }
        else
        {
            var environment = environmentName ?? current.Run.Environment ?? "default";
            var store = new FileSystemTrendStore(storePath!);
            var recent = (await store.ReadRecentAsync(environment, current.Run.Name, baselineRuns + 1, ct))
                // Compare-then-publish is the documented order, but never compare a run to itself
                // if it has already been pushed.
                .Where(m => m.Run.StartedUtc != current.Run.StartedUtc)
                .Take(baselineRuns)
                .ToList();
            baseline = BenchmarkComparer.MedianBaseline([.. recent.Select(m => m.Run.Benchmark)]);
            baselineDescription = recent.Count == 0
                ? $"no stored runs for '{current.Run.Name}' in environment '{environment}' — gates skip"
                : $"median of last {recent.Count} stored run(s) ({environment}/{current.Run.Name})";
        }

        var rows = BenchmarkComparer.Compare(current.Run.Benchmark, baseline, stat);
        Console.WriteLine(BenchmarkCompareReport.RenderTable(rows, stat, baselineDescription));

        var gates = LoadGates(environmentName, policyFilePath);
        var results = BenchmarkComparer.EvaluateGates(current.Run.Benchmark, baseline, gates);
        foreach (var result in results)
            Console.WriteLine($"gate {(result.Passed ? "PASS" : "FAIL")}: {result.Message}");
        if (gates.Count == 0)
            Console.WriteLine(environmentName is null
                ? "No perf gates evaluated (pass --env to load them from the policy file)."
                : "No perf gates declared for this environment.");

        foreach (var (format, path) in reports)
        {
            if (!string.Equals(format, "junit", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"bench compare supports only junit reports, not '{format}'.");
            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, BenchmarkCompareReport.RenderJUnit(results, baselineDescription), ct);
            Console.WriteLine($"junit report written: {fullPath}");
        }

        var failures = results.Count(r => !r.Passed);
        if (failures == 0) return 0;
        if (quarantine)
        {
            Console.WriteLine($"{failures} gate failure(s) quarantined (--quarantine): reported, not failing the pipeline.");
            return 0;
        }
        Console.Error.WriteLine($"{failures} perf gate failure(s).");
        return 2;
    }

    private static List<BenchmarkGate> LoadGates(string? environmentName, string policyFilePath)
    {
        if (environmentName is null) return [];
        var policyPath = Path.GetFullPath(policyFilePath);
        if (!File.Exists(policyPath))
        {
            Console.WriteLine($"note: no policy file at {policyPath} — no perf gates evaluated.");
            return [];
        }
        var policy = Tdm.Policy.PolicyDocument.Load(policyPath);
        return policy.Environments.TryGetValue(environmentName, out var env)
            ? env.Benchmarks?.Gates ?? []
            : [];
    }
}
