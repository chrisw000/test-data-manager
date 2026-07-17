using AwesomeAssertions;
using Tdm.Core.Benchmarks;
using Tdm.Core.Manifest;
using Xunit;

namespace Tdm.Core.Tests.Benchmarks;

/// <summary>Perf gates + baseline math (W3-D8), including the acceptance case: a deliberate
/// 25% p95 regression on create:Order fails a 20% gate.</summary>
public class BenchmarkComparerTests
{
    private static Dictionary<string, BenchmarkStats> Stats(params (string Key, double P95)[] entries) =>
        entries.ToDictionary(e => e.Key, e => new BenchmarkStats { Count = 10, P95Ms = e.P95, MeanMs = e.P95 * 0.8 });

    private static readonly BenchmarkGate OrderGate = new()
    {
        Operation = "create:Order", Stat = "p95Ms", MaxRegressionPct = 20,
    };

    [Fact]
    public void TwentyFivePercentRegression_FailsATwentyPercentGate()
    {
        var results = BenchmarkComparer.EvaluateGates(
            current: Stats(("create:Order", 125)),
            baseline: Stats(("create:Order", 100)),
            gates: [OrderGate]);

        var result = results.Single();
        result.Passed.Should().BeFalse();
        result.RegressionPct.Should().Be(25);
        result.Message.Should().Contain("create:Order").And.Contain("+25%").And.Contain("+20%");
    }

    [Fact]
    public void RegressionWithinTheGate_Passes()
    {
        var results = BenchmarkComparer.EvaluateGates(
            current: Stats(("create:Order", 115)),
            baseline: Stats(("create:Order", 100)),
            gates: [OrderGate]);

        results.Single().Passed.Should().BeTrue();
        results.Single().RegressionPct.Should().Be(15);
    }

    [Fact]
    public void ImprovementsAndMissingData_NeverFail()
    {
        var results = BenchmarkComparer.EvaluateGates(
            current: Stats(("create:Order", 80), ("create:Invoice", 50)),
            baseline: Stats(("create:Order", 100)),
            gates:
            [
                OrderGate,
                new BenchmarkGate { Operation = "create:Invoice", Stat = "p95Ms", MaxRegressionPct = 20 }, // no baseline
                new BenchmarkGate { Operation = "delete:Order", Stat = "p95Ms", MaxRegressionPct = 20 },   // not in current
                new BenchmarkGate { Operation = "create:Order", Stat = "p99Ms", MaxRegressionPct = 20 },   // unknown stat
            ]);

        results.Should().OnlyContain(r => r.Passed);
        results[0].RegressionPct.Should().Be(-20);
        results[1].Message.Should().Contain("no baseline");
        results[2].Message.Should().Contain("not present in the current run");
        results[3].Message.Should().Contain("unknown stat");
    }

    [Fact]
    public void MedianBaseline_TakesPerStatMedians_AcrossRunsThatHaveTheKey()
    {
        var baseline = BenchmarkComparer.MedianBaseline(
        [
            Stats(("create:Order", 100)),
            Stats(("create:Order", 500)), // one noisy CI agent run
            Stats(("create:Order", 110)),
            Stats(("create:Invoice", 40)),
        ]);

        baseline["create:Order"].P95Ms.Should().Be(110, "the median absorbs the noisy outlier");
        baseline["create:Invoice"].P95Ms.Should().Be(40, "keys contribute wherever they appear");
    }

    [Fact]
    public void Compare_CoversKeysFromBothSides_WithRegressionPct()
    {
        var rows = BenchmarkComparer.Compare(
            current: Stats(("create:Order", 110), ("load:Order", 5)),
            baseline: Stats(("create:Order", 100), ("delete:Order", 8)));

        rows.Select(r => r.Operation).Should().Equal("create:Order", "delete:Order", "load:Order");
        rows[0].RegressionPct.Should().Be(10);
        rows[1].CurrentMs.Should().BeNull();
        rows[2].BaselineMs.Should().BeNull();
    }

    [Fact]
    public void GateStat_SelectsTheDeclaredStat()
    {
        var current = new Dictionary<string, BenchmarkStats>
        {
            ["create:Order"] = new() { MeanMs = 200, P95Ms = 100 },
        };
        var baseline = new Dictionary<string, BenchmarkStats>
        {
            ["create:Order"] = new() { MeanMs = 100, P95Ms = 100 },
        };

        var results = BenchmarkComparer.EvaluateGates(current, baseline,
            [new BenchmarkGate { Operation = "create:Order", Stat = "meanMs", MaxRegressionPct = 50 }]);

        results.Single().Passed.Should().BeFalse("mean regressed 100% even though p95 is flat");
    }
}
