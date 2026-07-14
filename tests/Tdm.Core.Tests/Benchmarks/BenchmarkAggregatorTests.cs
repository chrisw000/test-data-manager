using AwesomeAssertions;
using Tdm.Core.Benchmarks;
using Xunit;

namespace Tdm.Core.Tests.Benchmarks;

public class BenchmarkAggregatorTests
{
    [Fact]
    public void Compute_NearestRankPercentiles_OneToHundred()
    {
        var stats = BenchmarkAggregator.Compute([.. Enumerable.Range(1, 100).Select(i => (double)i)]);
        stats.Count.Should().Be(100);
        stats.P50Ms.Should().Be(50);
        stats.P95Ms.Should().Be(95);
        stats.MaxMs.Should().Be(100);
        stats.MeanMs.Should().Be(50.5);
        stats.TotalMs.Should().Be(5050);
    }

    [Fact]
    public void Compute_SingleSample()
    {
        var stats = BenchmarkAggregator.Compute([7.5]);
        stats.Count.Should().Be(1);
        stats.P50Ms.Should().Be(7.5);
        stats.P95Ms.Should().Be(7.5);
        stats.MaxMs.Should().Be(7.5);
    }

    [Fact]
    public void Compute_Empty_AllZero()
    {
        var stats = BenchmarkAggregator.Compute([]);
        stats.Count.Should().Be(0);
        stats.TotalMs.Should().Be(0);
    }

    [Fact]
    public void ByOperation_GroupsAcrossEntities()
    {
        var aggregator = new BenchmarkAggregator();
        aggregator.Record("create", "Customer", 10);
        aggregator.Record("create", "Order", 20);
        aggregator.Record("load", "Customer", 5);

        var byOperation = aggregator.ByOperation();
        byOperation["create"].Count.Should().Be(2);
        byOperation["load"].Count.Should().Be(1);

        aggregator.ByOperationAndEntity().Keys.Should().Contain(["create:Customer", "create:Order"]);
    }

    [Fact]
    public void MergeInto_CombinesSamples()
    {
        var source = new BenchmarkAggregator();
        source.Record("create", "Customer", 10);
        var target = new BenchmarkAggregator();
        target.Record("create", "Customer", 20);
        source.MergeInto(target);
        target.ByOperation()["create"].Count.Should().Be(2);
    }
}
