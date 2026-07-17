using AwesomeAssertions;
using Tdm.Policy;
using Xunit;

namespace Tdm.Policy.Tests;

/// <summary>Perf gates live in the W2 policy file (W3-D8) — one enforcement pipeline.</summary>
public class BenchmarkPolicyTests
{
    [Fact]
    public void PolicyFile_ParsesBenchmarkGates_NextToTheExistingRules()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tdm-policy-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {
              // Perf budgets sit next to volume caps (W3-D8).
              "policyVersion": 1,
              "environments": {
                "ci": {
                  "maxBulkRowsPerStep": 100000,
                  "benchmarks": {
                    "gates": [
                      { "operation": "create:Order", "stat": "p95Ms", "maxRegressionPct": 20 },
                      { "operation": "persist", "maxRegressionPct": 35 },
                    ]
                  }
                }
              }
            }
            """);
        try
        {
            var policy = PolicyDocument.Load(path);
            var gates = policy.Environments["ci"].Benchmarks!.Gates;
            gates.Should().HaveCount(2);
            gates[0].Operation.Should().Be("create:Order");
            gates[0].Stat.Should().Be("p95Ms");
            gates[0].MaxRegressionPct.Should().Be(20);
            gates[1].Stat.Should().Be("p95Ms", "the stat defaults to p95Ms");
            gates[1].MaxRegressionPct.Should().Be(35);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
