using AwesomeAssertions;
using Tdm.Core.Execution;
using Tdm.Core.Grammar;
using Tdm.Core.Manifest;
using Tdm.Core.Settings;
using Xunit;

namespace Tdm.Core.Tests.Execution;

/// <summary>
/// The streaming count-bulk pipeline (W3-D3/W3-D4): chunked persistence, manifest sampling
/// modes with the value hash, and failure handling.
/// </summary>
public class TdmEngineBulkTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SeedingPlan Plan(string featureText) =>
        new() { Features = { new GherkinPlanParser().ParseText(featureText) } };

    private const string TwelveWidgets = """
        Feature: F
          Scenario: S
            Given 12 Widgets exist with colour "Blue"
        """;

    private static TdmSettings Settings(BulkManifestMode mode, int sampleRows = 2, int chunkSize = 4)
    {
        var settings = new TdmSettings
        {
            Run = new RunSettings
            {
                Name = "engine-bulk-test",
                Lifecycle = LifecycleMode.Persistent,
                BulkChunkSize = chunkSize,
                ManifestBulkValues = mode,
                ManifestBulkSampleRows = sampleRows,
            },
        };
        settings.ApplyDefaults();
        return settings;
    }

    private static FakeDomainRuntime NewRuntime() => new("Test",
        FakeDomainRuntime.Describe<Widget>("Widget", "Test"),
        FakeDomainRuntime.Describe<Gadget>("Gadget", "Test"));

    [Fact]
    public async Task SampleMode_KeepsHeadAndTail_HashesTheRest_AndStreamsInChunks()
    {
        var runtime = NewRuntime();
        var manifest = await new TdmEngine(Settings(BulkManifestMode.Sample), [runtime]).RunAsync(Plan(TwelveWidgets), ct: Ct);

        manifest.Run.Outcome.Should().Be(RunOutcome.Succeeded);
        runtime.Store["Widget"].Should().HaveCount(12);
        // O(chunk) streaming: one persist call per chunk, not one buffered call for all rows.
        runtime.Calls.Where(c => c.StartsWith("createBulk", StringComparison.Ordinal))
            .Should().Equal("createBulk:Widget:4", "createBulk:Widget:4", "createBulk:Widget:4");

        var scenario = manifest.Scenarios.Single();
        scenario.Entities.Select(e => e.Ordinal).Should().Equal(1, 2, 11, 12); // head + tail
        scenario.Entities.Should().OnlyContain(e => e.Values.Count > 0 && e.PersistedVia == "FakeStore(bulk)");

        var bulk = scenario.BulkOperations.Should().ContainSingle().Subject;
        (bulk.Entity, bulk.Requested, bulk.Count, bulk.Failed).Should().Be(("Widget", 12, 12, 0));
        (bulk.SampledRows, bulk.HashedRows).Should().Be((4, 8));
        (bulk.FirstOrdinal, bulk.LastOrdinal).Should().Be((1, 12));
        bulk.ValuesSha256.Should().NotBeNullOrEmpty();
        bulk.Mode.Should().Be(BulkManifestMode.Sample);
    }

    [Fact]
    public async Task NoneMode_RecordsCountAndHashOnly()
    {
        var runtime = NewRuntime();
        var manifest = await new TdmEngine(Settings(BulkManifestMode.None), [runtime]).RunAsync(Plan(TwelveWidgets), ct: Ct);

        manifest.Run.Outcome.Should().Be(RunOutcome.Succeeded);
        runtime.Store["Widget"].Should().HaveCount(12);
        var scenario = manifest.Scenarios.Single();
        scenario.Entities.Should().BeEmpty();
        var bulk = scenario.BulkOperations.Single();
        (bulk.SampledRows, bulk.HashedRows, bulk.Count).Should().Be((0, 12, 12));
        bulk.ValuesSha256.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AllMode_KeepsEveryRow_LikeV1()
    {
        var runtime = NewRuntime();
        var manifest = await new TdmEngine(Settings(BulkManifestMode.All), [runtime]).RunAsync(Plan(TwelveWidgets), ct: Ct);

        var scenario = manifest.Scenarios.Single();
        scenario.Entities.Should().HaveCount(12);
        var bulk = scenario.BulkOperations.Single();
        (bulk.SampledRows, bulk.HashedRows).Should().Be((12, 0));
        bulk.ValuesSha256.Should().BeNull();
    }

    [Fact]
    public async Task ValueHash_IsDeterministic_AcrossRuns()
    {
        var first = await new TdmEngine(Settings(BulkManifestMode.Sample), [NewRuntime()]).RunAsync(Plan(TwelveWidgets), ct: Ct);
        var second = await new TdmEngine(Settings(BulkManifestMode.Sample), [NewRuntime()]).RunAsync(Plan(TwelveWidgets), ct: Ct);

        var firstHash = first.Scenarios.Single().BulkOperations.Single().ValuesSha256;
        second.Scenarios.Single().BulkOperations.Single().ValuesSha256.Should().Be(firstHash);
    }

    [Fact]
    public async Task FailedChunks_AlwaysKeepTheirEntries_WithWarnings()
    {
        var runtime = NewRuntime();
        runtime.FailCreates = true;
        var manifest = await new TdmEngine(Settings(BulkManifestMode.Sample), [runtime]).RunAsync(Plan(TwelveWidgets), ct: Ct);

        manifest.Run.Outcome.Should().Be(RunOutcome.CompletedWithWarnings);
        var scenario = manifest.Scenarios.Single();
        scenario.Entities.Should().HaveCount(12); // failed rows bypass sampling
        scenario.Entities.Should().OnlyContain(e => e.Warnings.Count > 0);
        var bulk = scenario.BulkOperations.Single();
        (bulk.Count, bulk.Failed, bulk.HashedRows).Should().Be((0, 12, 0));
        bulk.ValuesSha256.Should().BeNull();
    }

    [Fact]
    public async Task DryRunValidate_StreamsAndSamples_WithoutPersisting()
    {
        var runtime = NewRuntime();
        var manifest = await new TdmEngine(Settings(BulkManifestMode.Sample), [runtime]).ValidateAsync(Plan(TwelveWidgets), Ct);

        manifest.Run.Outcome.Should().Be(RunOutcome.Succeeded);
        runtime.Store["Widget"].Should().BeEmpty();
        runtime.Calls.Should().NotContain(c => c.StartsWith("createBulk", StringComparison.Ordinal));
        var scenario = manifest.Scenarios.Single();
        scenario.Entities.Should().HaveCount(4);
        scenario.Entities.Should().OnlyContain(e => e.PersistedVia == "dry-run");
        var bulk = scenario.BulkOperations.Single();
        (bulk.Count, bulk.SampledRows, bulk.HashedRows).Should().Be((12, 4, 8));
    }

    [Fact]
    public async Task BulkRows_AreNotRegisteredInTheContextBag()
    {
        // A later step referencing a bulk-created row must fall back to the database lookup
        // (FakeDomainRuntime's store) — the bag deliberately never holds bulk instances.
        var runtime = NewRuntime();
        var settings = Settings(BulkManifestMode.Sample);
        var manifest = await new TdmEngine(settings, [runtime]).RunAsync(Plan("""
            Feature: F
              Scenario: S
                Given 3 Widgets exist with name "BulkWidget"
                And a Gadget exists for Widget "BulkWidget" with name "G1"
            """), ct: Ct);

        // All 3 share the natural key, so the DB lookup reports ambiguity or picks — either
        // way resolution went to the store, not the bag; with distinct keys it succeeds:
        var reference = manifest.Scenarios.Single().References.LastOrDefault();
        reference.Should().NotBeNull();
        reference!.ResolvedFrom.Should().NotBe("contextBag");
    }
}
