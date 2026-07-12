using System.Text;
using Microsoft.Extensions.Logging;
using Tdm.Core.Manifest;

namespace Tdm.Observability;

/// <summary>Run-end ILogger summary: outcome, per-scenario status, benchmark table (handoff §12).</summary>
public static class RunSummary
{
    public static void Log(ILogger logger, RunManifest manifest)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"── TDM run '{manifest.Run.Name}' — {manifest.Run.Outcome} in {manifest.Run.DurationMs:F0} ms ──");

        foreach (var scenario in manifest.Scenarios)
        {
            var created = scenario.Entities.Count(e => e.Verb is "Create" or "Projection");
            var warnings = scenario.Warnings.Count + scenario.Entities.Sum(e => e.Warnings.Count);
            sb.AppendLine($"  [{scenario.Outcome,-22}] {scenario.Feature} / {scenario.Scenario} " +
                          $"(seed {scenario.Seed}, {created} created, {warnings} warning(s))");
        }

        if (manifest.Teardown.Deleted > 0 || manifest.Teardown.Orphaned.Count > 0)
            sb.AppendLine($"  Teardown: {manifest.Teardown.Deleted} deleted, {manifest.Teardown.Orphaned.Count} orphaned");

        if (manifest.Run.Benchmark.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  {"operation",-28} {"count",7} {"total ms",10} {"mean",9} {"p50",9} {"p95",9} {"max",9}");
            foreach (var (operation, s) in manifest.Run.Benchmark.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                sb.AppendLine($"  {operation,-28} {s.Count,7} {s.TotalMs,10:F1} {s.MeanMs,9:F1} {s.P50Ms,9:F1} {s.P95Ms,9:F1} {s.MaxMs,9:F1}");
            }
        }

        var level = manifest.Run.Outcome switch
        {
            RunOutcome.Succeeded => LogLevel.Information,
            RunOutcome.CompletedWithWarnings => LogLevel.Warning,
            _ => LogLevel.Error,
        };
        logger.Log(level, "{Summary}", sb.ToString());
    }
}
