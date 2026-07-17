using AwesomeAssertions;
using Tdm.Core.Execution;
using Tdm.Core.Grammar;
using Tdm.Core.Journal;
using Tdm.Core.Manifest;
using Tdm.Core.Settings;
using Tdm.Core.Tests.Execution;
using Xunit;

namespace Tdm.Core.Tests.Journal;

/// <summary>
/// Run journal + resume (W3-D6): the JSONL journal records progress crash-safely, and a
/// resumed run converges on exactly the row set an uninterrupted run would have produced.
/// </summary>
public class ResumeJournalTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "tdm-journal-tests", Guid.NewGuid().ToString("N"));

    public ResumeJournalTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }

    private string JournalFile(string name) => Path.Combine(_directory, name + ".tdm.journal.jsonl");

    private static SeedingPlan Plan(string featureText) =>
        new() { Features = { new GherkinPlanParser().ParseText(featureText) } };

    private static TdmSettings Settings(FailurePolicy policy = FailurePolicy.BestEffort, int bulkChunkSize = 500)
    {
        var settings = new TdmSettings
        {
            Run = new RunSettings
            {
                Name = "journal-test",
                FailurePolicy = policy,
                Lifecycle = LifecycleMode.Persistent,
                BulkChunkSize = bulkChunkSize,
            },
        };
        settings.ApplyDefaults();
        return settings;
    }

    private static FakeDomainRuntime NewRuntime() => new("Test",
        FakeDomainRuntime.Describe<Widget>("Widget", "Test"),
        FakeDomainRuntime.Describe<Gadget>("Gadget", "Test"));

    private const string TwoScenarioFeature = """
        Feature: Journal
          Scenario: First
            Given a Widget exists with name "W1" and colour "Red" and size "1"
            And a Widget exists with name "W2" and colour "Blue" and size "2"
            And a Widget exists with name "W3" and colour "Green" and size "3"
          Scenario: Second
            Given a Widget exists with name "W4" and colour "Black" and size "4"
            And a Widget exists with name "W5" and colour "White" and size "5"
        """;

    private static List<(string? Name, string? Id)> Rows(FakeDomainRuntime runtime) =>
        [.. runtime.Store["Widget"].Cast<Widget>()
            .Select(w => ((string?)w.Name, (string?)w.Id.ToString()))
            .OrderBy(w => w.Item1)];

    // ---------------------------------------------------------------- Writer/reader round-trip

    [Fact]
    public void Journal_RoundTrips_AndTruncatedLastLineIsIgnored()
    {
        var path = JournalFile("roundtrip");
        using (var writer = new RunJournalWriter(path))
        {
            writer.RunStarted("journal-test");
            writer.ScenarioStarted("Journal|First|2", seed: 5);
            writer.Entity("Journal|First|2", new EntityManifest { Ordinal = 1, Entity = "Widget", Verb = "Create", Id = "id-1" }, persisted: true);
            writer.Entity("Journal|First|2", new EntityManifest { Ordinal = 2, Entity = "Widget", Verb = "Create", Id = "id-2" }, persisted: false);
            writer.ScenarioCompleted("Journal|First|2", ScenarioOutcome.Succeeded);
            writer.ScenarioStarted("Journal|Second|7", seed: 5);
            writer.Entity("Journal|Second|7", new EntityManifest { Ordinal = 1, Entity = "Widget", Verb = "Create", Id = "id-3" }, persisted: true);
        }
        // A killed process can leave a truncated final line — it must not poison the resume.
        File.AppendAllText(path, "{\"kind\":\"entity\",\"scenario\":\"Journal|Sec");

        var state = ResumeState.Load(path);
        state.IsScenarioComplete("Journal|First|2").Should().BeTrue();
        state.IsScenarioComplete("Journal|Second|7").Should().BeFalse("no scenario-complete line was written");
        state.IsPersisted("Journal|First|2", 1).Should().BeTrue();
        state.IsPersisted("Journal|First|2", 2).Should().BeFalse("persisted: false outcomes are retried on resume");
        state.IsPersisted("Journal|Second|7", 1).Should().BeTrue();
        state.RecordedSeed("Journal|Second|7").Should().Be(5);
    }

    // ---------------------------------------------------------------- Interrupted run → resume

    [Fact]
    public async Task InterruptedRun_Resumed_ProducesTheSameRowSet_AsUninterrupted()
    {
        // Baseline: the uninterrupted run.
        var baselineRuntime = NewRuntime();
        var baseline = await new TdmEngine(Settings(), [baselineRuntime]).RunAsync(Plan(TwoScenarioFeature), ct: Ct);
        baseline.Run.Outcome.Should().Be(RunOutcome.Succeeded);

        // Interrupted: the third create fails and FailRun aborts — two rows persisted, journalled.
        var runtime = NewRuntime();
        runtime.FailCreatesAfterRows = 2;
        using (var journal = new RunJournalWriter(JournalFile("interrupted")))
        {
            var aborted = await new TdmEngine(Settings(FailurePolicy.FailRun), [runtime], journal: journal)
                .RunAsync(Plan(TwoScenarioFeature), ct: Ct);
            aborted.Run.Outcome.Should().Be(RunOutcome.Failed);
        }
        runtime.Store["Widget"].Should().HaveCount(2);

        // Resume against the same store: journalled rows are skipped, the rest are written.
        runtime.FailCreatesAfterRows = null;
        var resumed = await new TdmEngine(Settings(), [runtime],
                resume: ResumeState.Load(JournalFile("interrupted")))
            .RunAsync(Plan(TwoScenarioFeature), ct: Ct);

        resumed.Run.Outcome.Should().Be(RunOutcome.Succeeded);
        Rows(runtime).Should().Equal(Rows(baselineRuntime));

        var first = resumed.Scenarios.Single(s => s.Scenario == "First");
        first.Entities.Where(e => e.PersistedVia == "resumed").Should().HaveCount(2);
        first.Entities.Where(e => e.PersistedVia == "FakeStore").Should().HaveCount(1);
    }

    [Fact]
    public async Task CompletedScenario_IsSkippedWhole_AndTheNewJournalIsItselfResumable()
    {
        var runtime = NewRuntime();
        using (var journal = new RunJournalWriter(JournalFile("first-pass")))
        {
            // Interrupt after scenario First completes: 4th row (Second's first) fails.
            runtime.FailCreatesAfterRows = 3;
            await new TdmEngine(Settings(FailurePolicy.FailRun), [runtime], journal: journal)
                .RunAsync(Plan(TwoScenarioFeature), ct: Ct);
        }

        runtime.FailCreatesAfterRows = null;
        var createsBefore = runtime.Calls.Count(c => c.StartsWith("create:"));
        using (var journal = new RunJournalWriter(JournalFile("second-pass")))
        {
            var resumed = await new TdmEngine(Settings(), [runtime], journal: journal,
                    resume: ResumeState.Load(JournalFile("first-pass")))
                .RunAsync(Plan(TwoScenarioFeature), ct: Ct);

            var first = resumed.Scenarios.Single(s => s.Scenario == "First");
            first.Outcome.Should().Be(ScenarioOutcome.Skipped);
            first.Warnings.Should().ContainSingle(w => w.Contains("recorded complete"));
        }

        // Scenario First was not re-executed at all — no new creates for its rows.
        runtime.Calls.Count(c => c.StartsWith("create:")).Should().Be(createsBefore + 2, "only Second's two rows persist");
        runtime.Store["Widget"].Should().HaveCount(5);

        // The resumed run's own journal records First complete too (as Skipped).
        var chained = ResumeState.Load(JournalFile("second-pass"));
        chained.IsScenarioComplete(ScenarioKeyOf("First")).Should().BeTrue();
        chained.IsScenarioComplete(ScenarioKeyOf("Second")).Should().BeTrue();
    }

    [Fact]
    public async Task SeedMismatch_ReRunsTheScenario_WithAWarning()
    {
        var runtime = NewRuntime();
        runtime.FailCreatesAfterRows = 2;
        using (var journal = new RunJournalWriter(JournalFile("seeded")))
        {
            await new TdmEngine(Settings(FailurePolicy.FailRun), [runtime], journal: journal)
                .RunAsync(Plan(TwoScenarioFeature), ct: Ct);
        }

        runtime.FailCreatesAfterRows = null;
        var settings = Settings();
        settings.Run.DefaultSeed = 42; // journal recorded seed 1
        var resumed = await new TdmEngine(settings, [runtime],
                resume: ResumeState.Load(JournalFile("seeded")))
            .RunAsync(Plan(TwoScenarioFeature), ct: Ct);

        var first = resumed.Scenarios.Single(s => s.Scenario == "First");
        first.Warnings.Should().ContainSingle(w => w.Contains("seed 1") && w.Contains("seed 42"));
        // Nothing was skipped ordinal-wise; idempotent create-or-reuse converged on the
        // existing natural-keyed rows instead.
        first.Entities.Should().NotContain(e => e.PersistedVia == "resumed");
        runtime.Store["Widget"].Should().HaveCount(5);
    }

    // ---------------------------------------------------------------- Bulk resume

    [Fact]
    public async Task InterruptedBulkCreate_Resumed_WritesOnlyTheMissingChunks()
    {
        const string bulkFeature = """
            Feature: Bulk
              Scenario: Load
                Given 30 Widgets exist with colour "Blue"
            """;

        var baselineRuntime = NewRuntime();
        await new TdmEngine(Settings(bulkChunkSize: 10), [baselineRuntime]).RunAsync(Plan(bulkFeature), ct: Ct);
        baselineRuntime.Store["Widget"].Should().HaveCount(30);

        // Interrupted after the first chunk of 10.
        var runtime = NewRuntime();
        runtime.FailCreatesAfterRows = 10;
        using (var journal = new RunJournalWriter(JournalFile("bulk")))
        {
            await new TdmEngine(Settings(FailurePolicy.FailRun, bulkChunkSize: 10), [runtime], journal: journal)
                .RunAsync(Plan(bulkFeature), ct: Ct);
        }
        runtime.Store["Widget"].Should().HaveCount(10);
        // Every persisted bulk row is journalled — not just the manifest-sampled ones.
        File.ReadLines(JournalFile("bulk")).Count(l => l.Contains("\"kind\":\"entity\"") && l.Contains("\"persisted\":true"))
            .Should().Be(10);

        runtime.FailCreatesAfterRows = null;
        var bulkCallsBefore = runtime.Calls.Count(c => c.StartsWith("createBulk:"));
        var resumed = await new TdmEngine(Settings(bulkChunkSize: 10), [runtime],
                resume: ResumeState.Load(JournalFile("bulk")))
            .RunAsync(Plan(bulkFeature), ct: Ct);

        resumed.Run.Outcome.Should().Be(RunOutcome.Succeeded);
        runtime.Store["Widget"].Should().HaveCount(30, "resumed ordinals must not be re-inserted");
        // Ordinal-derived ids are seed-deterministic, so the resumed set equals the baseline set.
        runtime.Store["Widget"].Cast<Widget>().Select(w => w.Id).Order().Should()
            .Equal(baselineRuntime.Store["Widget"].Cast<Widget>().Select(w => w.Id).Order());
        // The first chunk was entirely resumed — the resume made only two bulk writes of 10.
        (runtime.Calls.Count(c => c.StartsWith("createBulk:")) - bulkCallsBefore).Should().Be(2);

        var summary = resumed.Scenarios.Single().BulkOperations.Single();
        summary.Count.Should().Be(30);
        summary.Failed.Should().Be(0);
    }

    private static string ScenarioKeyOf(string scenarioName)
    {
        var plan = Plan(TwoScenarioFeature);
        var scenario = plan.Features[0].Scenarios.Single(s => s.Name == scenarioName);
        return $"{plan.Features[0].Name}|{scenario.Name}|{scenario.Line}";
    }
}
