using Tdm.Core.Benchmarks;
using Xunit;

namespace Tdm.Core.Tests.Benchmarks;

public class BenchmarkAggregatorTests
{
    [Fact]
    public void Compute_NearestRankPercentiles_OneToHundred()
    {
        var stats = BenchmarkAggregator.Compute([.. Enumerable.Range(1, 100).Select(i => (double)i)]);
        Assert.Equal(100, stats.Count);
        Assert.Equal(50, stats.P50Ms);
        Assert.Equal(95, stats.P95Ms);
        Assert.Equal(100, stats.MaxMs);
        Assert.Equal(50.5, stats.MeanMs);
        Assert.Equal(5050, stats.TotalMs);
    }

    [Fact]
    public void Compute_SingleSample()
    {
        var stats = BenchmarkAggregator.Compute([7.5]);
        Assert.Equal(1, stats.Count);
        Assert.Equal(7.5, stats.P50Ms);
        Assert.Equal(7.5, stats.P95Ms);
        Assert.Equal(7.5, stats.MaxMs);
    }

    [Fact]
    public void Compute_Empty_AllZero()
    {
        var stats = BenchmarkAggregator.Compute([]);
        Assert.Equal(0, stats.Count);
        Assert.Equal(0, stats.TotalMs);
    }

    [Fact]
    public void ByOperation_GroupsAcrossEntities()
    {
        var aggregator = new BenchmarkAggregator();
        aggregator.Record("create", "Customer", 10);
        aggregator.Record("create", "Order", 20);
        aggregator.Record("load", "Customer", 5);

        var byOperation = aggregator.ByOperation();
        Assert.Equal(2, byOperation["create"].Count);
        Assert.Equal(1, byOperation["load"].Count);

        var byBoth = aggregator.ByOperationAndEntity();
        Assert.Contains("create:Customer", byBoth.Keys);
        Assert.Contains("create:Order", byBoth.Keys);
    }

    [Fact]
    public void MergeInto_CombinesSamples()
    {
        var source = new BenchmarkAggregator();
        source.Record("create", "Customer", 10);
        var target = new BenchmarkAggregator();
        target.Record("create", "Customer", 20);
        source.MergeInto(target);
        Assert.Equal(2, target.ByOperation()["create"].Count);
    }
}
