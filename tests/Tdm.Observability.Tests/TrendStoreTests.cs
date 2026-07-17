using AwesomeAssertions;
using Tdm.Core.Manifest;
using Tdm.Observability.Trends;
using Xunit;

namespace Tdm.Observability.Tests;

/// <summary>Trend store (W3-D7): {env}/{run}/{timestamp} layout, index maintenance, and
/// newest-first recent-manifest retrieval for rolling baselines.</summary>
public class TrendStoreTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "tdm-trend-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static RunManifest Manifest(string name, DateTime startedUtc, double p95 = 10)
    {
        return new RunManifest
        {
            Run = new RunInfo
            {
                Name = name,
                StartedUtc = startedUtc,
                Outcome = RunOutcome.Succeeded,
                DurationMs = 1234,
                Benchmark = { ["create:Order"] = new BenchmarkStats { Count = 5, P95Ms = p95 } },
            },
        };
    }

    [Fact]
    public async Task Publish_LaysOutByEnvRunTimestamp_AndMaintainsTheIndex()
    {
        var store = new FileSystemTrendStore(_root);

        var first = await store.PublishAsync(Manifest("orders-seed", new DateTime(2026, 7, 16, 10, 0, 0, DateTimeKind.Utc)), "ci", Ct);
        var second = await store.PublishAsync(Manifest("orders-seed", new DateTime(2026, 7, 17, 10, 0, 0, DateTimeKind.Utc)), "ci", Ct);

        first.Should().Be("ci/orders-seed/20260716-100000.tdm.json");
        second.Should().Be("ci/orders-seed/20260717-100000.tdm.json");
        File.Exists(Path.Combine(_root, "ci", "orders-seed", "20260716-100000.tdm.json")).Should().BeTrue();

        var index = await store.ReadIndexAsync(Ct);
        index.Entries.Should().HaveCount(2);
        index.Entries.Select(e => e.Path).Should().Equal(first, second);
        index.Entries.Should().OnlyContain(e => e.Environment == "ci" && e.RunName == "orders-seed");
    }

    [Fact]
    public async Task Republish_OfTheSameTimestamp_Overwrites_NotDuplicates()
    {
        var store = new FileSystemTrendStore(_root);
        var when = new DateTime(2026, 7, 17, 10, 0, 0, DateTimeKind.Utc);
        await store.PublishAsync(Manifest("orders-seed", when), "ci", Ct);
        await store.PublishAsync(Manifest("orders-seed", when), "ci", Ct);

        (await store.ReadIndexAsync(Ct)).Entries.Should().HaveCount(1);
    }

    [Fact]
    public async Task ReadRecent_ReturnsNewestFirst_ScopedToEnvAndRun()
    {
        var store = new FileSystemTrendStore(_root);
        for (var day = 1; day <= 5; day++)
        {
            await store.PublishAsync(
                Manifest("orders-seed", new DateTime(2026, 7, day, 10, 0, 0, DateTimeKind.Utc), p95: day * 10), "ci", Ct);
        }
        await store.PublishAsync(Manifest("other-run", new DateTime(2026, 7, 9, 10, 0, 0, DateTimeKind.Utc)), "ci", Ct);
        await store.PublishAsync(Manifest("orders-seed", new DateTime(2026, 7, 9, 10, 0, 0, DateTimeKind.Utc)), "staging", Ct);

        var recent = await store.ReadRecentAsync("ci", "orders-seed", count: 3, Ct);
        recent.Should().HaveCount(3);
        recent.Select(m => m.Run.StartedUtc.Day).Should().Equal(5, 4, 3);
        recent[0].Run.Benchmark["create:Order"].P95Ms.Should().Be(50);
    }
}
