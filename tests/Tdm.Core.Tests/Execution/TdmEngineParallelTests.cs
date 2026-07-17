using AwesomeAssertions;
using Tdm.Core.Execution;
using Tdm.Core.Grammar;
using Tdm.Core.Manifest;
using Tdm.Core.Settings;
using Xunit;

namespace Tdm.Core.Tests.Execution;

/// <summary>
/// Parallel scenario execution (W3-D1/W3-D2): identical manifests to a serial run, deterministic
/// plan-order recording, per-domain parallelism caps and the SQLite/Transactional auto-serialise.
/// </summary>
public class TdmEngineParallelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SeedingPlan Plan(string featureText) =>
        new() { Features = { new GherkinPlanParser().ParseText(featureText) } };

    private static TdmSettings Settings(int maxParallel, FailurePolicy policy = FailurePolicy.BestEffort,
        LifecycleMode lifecycle = LifecycleMode.Persistent)
    {
        var settings = new TdmSettings
        {
            Run = new RunSettings
            {
                Name = "engine-parallel-test",
                FailurePolicy = policy,
                Lifecycle = lifecycle,
                MaxParallelScenarios = maxParallel,
            },
        };
        settings.ApplyDefaults();
        return settings;
    }

    private static FakeDomainRuntime NewRuntime() => new("Test",
        FakeDomainRuntime.Describe<Widget>("Widget", "Test"),
        FakeDomainRuntime.Describe<Gadget>("Gadget", "Test"));

    // Disjoint natural keys per scenario — the documented shape parallelism is for.
    private const string DisjointFeature = """
        Feature: Parallel
          Scenario: One
            Given a Widget exists with name "W1" and colour "Red" and size "1"
            And a Gadget exists for Widget "W1" with name "G1"
          Scenario: Two
            Given a Widget exists with name "W2" and colour "Blue" and size "2"
          Scenario: Three
            Given a Widget exists with name "W3" and colour "Green" and size "3"
            And a Gadget exists for Widget "W3" with name "G3"
          Scenario: Four
            Given a Widget exists with name "W4" and colour "Black" and size "4"
          Scenario: Five
            Given a Widget exists with name "W5" and colour "White" and size "5"
          Scenario: Six
            Given a Widget exists with name "W6" and colour "Grey" and size "6"
        """;

    [Fact]
    public async Task ParallelRun_ProducesIdenticalManifest_ToSerialRun()
    {
        var serial = await new TdmEngine(Settings(maxParallel: 1), [NewRuntime()])
            .RunAsync(Plan(DisjointFeature), ct: Ct);
        var parallel = await new TdmEngine(Settings(maxParallel: 4), [NewRuntime()])
            .RunAsync(Plan(DisjointFeature), ct: Ct);

        parallel.Run.Outcome.Should().Be(serial.Run.Outcome);
        parallel.Scenarios.Select(s => s.Scenario).Should()
            .Equal(serial.Scenarios.Select(s => s.Scenario)); // plan order, not completion order

        foreach (var (parallelScenario, serialScenario) in parallel.Scenarios.Zip(serial.Scenarios))
        {
            parallelScenario.Outcome.Should().Be(serialScenario.Outcome);
            parallelScenario.Seed.Should().Be(serialScenario.Seed);
            parallelScenario.Warnings.Should().Equal(serialScenario.Warnings);
            parallelScenario.Entities.Select(e => (e.Ordinal, e.Entity, e.Verb, e.Id, e.NaturalKey)).Should()
                .Equal(serialScenario.Entities.Select(e => (e.Ordinal, e.Entity, e.Verb, e.Id, e.NaturalKey)));
            foreach (var (parallelEntity, serialEntity) in parallelScenario.Entities.Zip(serialScenario.Entities))
                parallelEntity.Values.Should().Equal(serialEntity.Values);
        }
    }

    [Fact]
    public async Task ParallelRun_PersistsEveryScenario()
    {
        var runtime = NewRuntime();
        var manifest = await new TdmEngine(Settings(maxParallel: 6), [runtime])
            .RunAsync(Plan(DisjointFeature), ct: Ct);

        manifest.Run.Outcome.Should().Be(RunOutcome.Succeeded);
        runtime.Store["Widget"].Should().HaveCount(6);
        runtime.Store["Gadget"].Should().HaveCount(2);
    }

    [Fact]
    public async Task FailRunAbort_InParallel_FailsRun_AndOnlySkipsNotStartedScenarios()
    {
        var manifest = await new TdmEngine(Settings(maxParallel: 2, policy: FailurePolicy.FailRun), [NewRuntime()])
            .RunAsync(Plan("""
                Feature: Abort
                  Scenario: Bad
                    Given something no grammar rule matches
                  Scenario: A
                    Given a Widget exists with name "A"
                  Scenario: B
                    Given a Widget exists with name "B"
                  Scenario: C
                    Given a Widget exists with name "C"
                  Scenario: D
                    Given a Widget exists with name "D"
                """), ct: Ct);

        manifest.Run.Outcome.Should().Be(RunOutcome.Failed);
        // Plan order is preserved and every scenario is accounted for — in-flight scenarios
        // finish (Succeeded), not-yet-started ones are recorded as Skipped.
        manifest.Scenarios.Select(s => s.Scenario).Should().Equal("Bad", "A", "B", "C", "D");
        manifest.Scenarios[0].Outcome.Should().Be(ScenarioOutcome.Failed);
        manifest.Scenarios.Skip(1).Should().OnlyContain(s =>
            s.Outcome == ScenarioOutcome.Succeeded || s.Outcome == ScenarioOutcome.Skipped);
        manifest.Scenarios.Where(s => s.Outcome == ScenarioOutcome.Skipped)
            .Should().OnlyContain(s => s.Warnings.Any(w => w.Contains("aborted")));
    }

    [Fact]
    public async Task DomainParallelismCap_SerialisesTheRun()
    {
        var runtime = NewRuntime();
        runtime.Settings.MaxParallelScenarios = 1; // the domain says no, whatever the run asks for

        var manifest = await new TdmEngine(Settings(maxParallel: 8), [runtime])
            .RunAsync(Plan(DisjointFeature), ct: Ct);

        manifest.Run.Outcome.Should().Be(RunOutcome.Succeeded);
        AssertStrictlySerial(runtime.Calls);
    }

    [Fact]
    public async Task TransactionalOnSqlite_AutoSerialises()
    {
        var runtime = NewRuntime(); // FakeDomainRuntime's DomainSettings default to Provider "Sqlite"
        var manifest = await new TdmEngine(Settings(maxParallel: 8, lifecycle: LifecycleMode.Transactional), [runtime])
            .RunAsync(Plan(DisjointFeature), ct: Ct);

        manifest.Run.Outcome.Should().Be(RunOutcome.Succeeded);
        AssertStrictlySerial(runtime.Calls);
    }

    [Fact]
    public async Task LostSameKeyRace_ConvergesOnWinnersRow_InsteadOfFailing()
    {
        var runtime = NewRuntime();
        runtime.SimulateConcurrentCreateRace = true;

        var manifest = await new TdmEngine(Settings(maxParallel: 1), [runtime]).RunAsync(Plan("""
            Feature: F
              Scenario: S
                Given a Widget exists with name "Race" and colour "Red"
            """), ct: Ct);

        manifest.Run.Outcome.Should().Be(RunOutcome.Succeeded);
        var entry = manifest.Scenarios.Single().Entities.Should().ContainSingle().Subject;
        entry.PersistedVia.Should().StartWith("already-existed (concurrent create");
        entry.Warnings.Should().BeEmpty();

        // Converged on the single winner row, with the step's declared values re-applied.
        var widget = runtime.Store["Widget"].Should().ContainSingle().Subject.Should().BeOfType<Widget>().Subject;
        (widget.Name, widget.Colour).Should().Be(("Race", "Red"));
    }

    [Fact]
    public async Task ParallelValidate_DryRun_TouchesNoStore()
    {
        var runtime = NewRuntime();
        var manifest = await new TdmEngine(Settings(maxParallel: 4), [runtime])
            .ValidateAsync(Plan(DisjointFeature), Ct);

        manifest.Run.Outcome.Should().Be(RunOutcome.Succeeded);
        manifest.Scenarios.Should().HaveCount(6);
        runtime.Store["Widget"].Should().BeEmpty();
        runtime.Calls.Should().BeEmpty(); // dry-run never begins scenarios or persists
    }

    /// <summary>Scenarios never overlapped: every begin is closed by an end before the next begin.</summary>
    private static void AssertStrictlySerial(IReadOnlyList<string> calls)
    {
        var open = false;
        foreach (var call in calls)
        {
            if (call.StartsWith("begin", StringComparison.Ordinal))
            {
                open.Should().BeFalse("scenarios must not overlap when the run is serialised");
                open = true;
            }
            else if (call == "end")
            {
                open = false;
            }
        }
    }
}
