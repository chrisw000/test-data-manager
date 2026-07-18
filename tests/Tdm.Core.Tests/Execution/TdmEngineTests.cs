using AwesomeAssertions;
using Tdm.Core.Execution;
using Tdm.Core.Grammar;
using Tdm.Core.Manifest;
using Tdm.Core.Settings;
using Tdm.Identity;
using Xunit;

namespace Tdm.Core.Tests.Execution;

public class TdmEngineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SeedingPlan Plan(string featureText) =>
        new() { Features = { new GherkinPlanParser().ParseText(featureText) } };

    private static TdmSettings Settings(FailurePolicy policy = FailurePolicy.BestEffort)
    {
        var settings = new TdmSettings
        {
            Run = new RunSettings { Name = "engine-test", FailurePolicy = policy, Lifecycle = LifecycleMode.Persistent },
        };
        settings.ApplyDefaults();
        return settings;
    }

    private static FakeDomainRuntime NewRuntime(string domain = "Test") => new(domain,
        FakeDomainRuntime.Describe<Widget>("Widget", domain),
        FakeDomainRuntime.Describe<Gadget>("Gadget", domain));

    // ---------------------------------------------------------------- Create

    [Fact]
    public async Task Create_AppliesOverrides_AndDeterministicIdentity()
    {
        var runtime = NewRuntime();
        var engine = new TdmEngine(Settings(), [runtime]);

        var manifest = await engine.RunAsync(Plan("""
            Feature: F
              Scenario: S
                Given a Widget exists with name "W1" and colour "Red" and size "5"
            """), ct: Ct);

        manifest.Run.Outcome.Should().Be(RunOutcome.Succeeded);
        manifest.ExitCode.Should().Be(0);
        var widget = runtime.Store["Widget"].Should().ContainSingle().Subject.Should().BeOfType<Widget>().Subject;
        (widget.Name, widget.Colour, widget.Size).Should().Be(("W1", "Red", 5));
        widget.Id.Should().Be(TdmIdentity.ForNaturalKey("Test", "Widget", "W1"));

        var scenario = manifest.Scenarios.Should().ContainSingle().Subject;
        var entry = scenario.Entities.Should().ContainSingle().Subject;
        entry.PersistedVia.Should().Be("FakeStore");
        entry.OverridesApplied.Should().Equal("Name", "Colour", "Size");
        entry.NaturalKey.Should().Be("W1");
        entry.Values["Colour"].Should().Be("Red");
    }

    [Fact]
    public async Task Create_SameNaturalKeyInLaterScenario_ReusesExistingRow()
    {
        var runtime = NewRuntime();
        var engine = new TdmEngine(Settings(), [runtime]);

        var manifest = await engine.RunAsync(Plan("""
            Feature: F
              Scenario: A
                Given a Widget exists with name "W1" and colour "Red"
              Scenario: B
                Given a Widget exists with name "W1" and colour "Blue"
            """), ct: Ct);

        manifest.Run.Outcome.Should().Be(RunOutcome.Succeeded);
        var widget = runtime.Store["Widget"].Should().ContainSingle().Subject.Should().BeOfType<Widget>().Subject;
        // Declared state re-applied on reuse.
        widget.Colour.Should().Be("Blue");
        manifest.Scenarios[1].Entities[0].PersistedVia.Should().StartWith("already-existed");
    }

    [Fact]
    public async Task Create_BulkCount_UsesBulkPathWithOrdinalIdentities()
    {
        var runtime = NewRuntime();
        var engine = new TdmEngine(Settings(), [runtime]);

        var manifest = await engine.RunAsync(Plan("""
            Feature: F
              Scenario: Bulk
                Given 5 Widgets exist with colour "Grey"
            """), ct: Ct);

        manifest.Run.Outcome.Should().Be(RunOutcome.Succeeded);
        runtime.Store["Widget"].Should().HaveCount(5);
        runtime.Calls.Should().Contain("createBulk:Widget:5");
        var ids = runtime.Store["Widget"].Cast<Widget>().Select(w => w.Id).ToList();
        ids.Should().OnlyHaveUniqueItems();
        ids[0].Should().Be(TdmIdentity.ForOrdinal("Test", "Widget", "Bulk", 1, 1));
    }

    [Fact]
    public async Task Create_PersistFailure_BestEffort_WarnsAndContinues()
    {
        var runtime = NewRuntime();
        runtime.FailCreates = true;
        var engine = new TdmEngine(Settings(), [runtime]);

        var manifest = await engine.RunAsync(Plan("""
            Feature: F
              Scenario: S
                Given a Widget exists with name "W1"
            """), ct: Ct);

        manifest.Run.Outcome.Should().Be(RunOutcome.CompletedWithWarnings);
        manifest.ExitCode.Should().Be(1);
        runtime.Store["Widget"].Should().BeEmpty();
    }

    // ---------------------------------------------------------------- References

    [Fact]
    public async Task Reference_ResolvedFromContextBag()
    {
        var runtime = NewRuntime();
        var engine = new TdmEngine(Settings(), [runtime]);

        var manifest = await engine.RunAsync(Plan("""
            Feature: F
              Scenario: S
                Given a Widget exists with name "W1"
                And a Gadget exists for Widget "W1" with name "G1"
            """), ct: Ct);

        manifest.Run.Outcome.Should().Be(RunOutcome.Succeeded);
        var widget = runtime.Store["Widget"][0].Should().BeOfType<Widget>().Subject;
        var gadget = runtime.Store["Gadget"][0].Should().BeOfType<Gadget>().Subject;
        gadget.WidgetId.Should().Be(widget.Id);
        var scenario = manifest.Scenarios.Should().ContainSingle().Subject;
        var reference = scenario.References.Should().ContainSingle().Subject;
        reference.ResolvedFrom.Should().Be("contextBag");
        // Lineage-edge source (W4-D1): the reference belongs to the Gadget entry.
        reference.SourceOrdinal.Should().Be(scenario.Entities.Single(e => e.Entity == "Gadget").Ordinal);
    }

    [Fact]
    public async Task Reference_FallsBackToDatabaseLookup()
    {
        var runtime = NewRuntime();
        var existing = new Widget { Id = Guid.NewGuid(), Name = "PreSeeded" };
        runtime.Store["Widget"].Add(existing);
        var engine = new TdmEngine(Settings(), [runtime]);

        var manifest = await engine.RunAsync(Plan("""
            Feature: F
              Scenario: S
                Given a Gadget exists for Widget "PreSeeded" with name "G1"
            """), ct: Ct);

        manifest.Run.Outcome.Should().Be(RunOutcome.Succeeded);
        runtime.Store["Gadget"][0].Should().BeOfType<Gadget>().Which.WidgetId.Should().Be(existing.Id);
        manifest.Scenarios[0].References.Should().ContainSingle().Which.ResolvedFrom.Should().Be("database");
    }

    [Fact]
    public async Task Reference_NotFound_WarnsPerPolicy()
    {
        var runtime = NewRuntime();
        var engine = new TdmEngine(Settings(), [runtime]);

        var manifest = await engine.RunAsync(Plan("""
            Feature: F
              Scenario: S
                Given a Gadget exists for Widget "Ghost" with name "G1"
            """), ct: Ct);

        manifest.Run.Outcome.Should().Be(RunOutcome.CompletedWithWarnings);
        manifest.Scenarios[0].Warnings.Should().Contain(w => w.Contains("Ghost"));
    }

    [Fact]
    public async Task ExternalReference_ComputesIdentityContractGuid()
    {
        var runtime = NewRuntime();
        var engine = new TdmEngine(Settings(), [runtime]);

        var manifest = await engine.RunAsync(Plan("""
            Feature: F
              Scenario: S
                Given an external Widget reference "W9" from CRM
                And a Gadget exists for Widget "W9" with name "G1"
            """), ct: Ct);

        manifest.Run.Outcome.Should().Be(RunOutcome.Succeeded);
        var expected = TdmIdentity.ForNaturalKey("CRM", "Widget", "W9");
        runtime.Store["Gadget"][0].Should().BeOfType<Gadget>().Which.WidgetId.Should().Be(expected);
        var external = manifest.Scenarios[0].References.First(r => r.ResolvedFrom == "identityContract");
        external.OwningDomain.Should().Be("CRM");
        external.Id.Should().Be(expected.ToString());
        external.SourceOrdinal.Should().BeNull("the declaration only publishes an identity — it is applied to no entity");
        // Nothing persisted in the owning domain — no Widget row was created.
        runtime.Store["Widget"].Should().BeEmpty();
    }

    // ---------------------------------------------------------------- Failure policies

    [Fact]
    public async Task PropertyFailure_BestEffort_SkipsPropertyOnly()
    {
        var runtime = NewRuntime();
        var engine = new TdmEngine(Settings(FailurePolicy.BestEffort), [runtime]);

        var manifest = await engine.RunAsync(Plan("""
            Feature: F
              Scenario: S
                Given a Widget exists with name "W1" and size "not-a-number"
            """), ct: Ct);

        manifest.Run.Outcome.Should().Be(RunOutcome.CompletedWithWarnings);
        var widget = runtime.Store["Widget"].Should().ContainSingle().Subject.Should().BeOfType<Widget>().Subject;
        widget.Name.Should().Be("W1");
        widget.Size.Should().Be(0); // skipped, left at default
        var entry = manifest.Scenarios[0].Entities[0];
        entry.Warnings.Should().Contain(w => w.Contains("Size"));
        entry.OverridesApplied.Should().NotContain("Size");
    }

    [Fact]
    public async Task PropertyFailure_FailObject_RejectsObject()
    {
        var runtime = NewRuntime();
        var engine = new TdmEngine(Settings(FailurePolicy.FailObject), [runtime]);

        var manifest = await engine.RunAsync(Plan("""
            Feature: F
              Scenario: S
                Given a Widget exists with name "W1" and size "not-a-number"
                And a Widget exists with name "W2" and size "3"
            """), ct: Ct);

        // The bad object is rejected; the scenario continues and the good one persists.
        runtime.Store["Widget"].Cast<Widget>().Should().NotContain(w => w.Name == "W1");
        runtime.Store["Widget"].Cast<Widget>().Should().ContainSingle(w => w.Name == "W2");
        manifest.Run.Outcome.Should().NotBe(RunOutcome.Succeeded);
    }

    [Fact]
    public async Task PropertyFailure_FailRun_AbortsRemainingScenarios()
    {
        var runtime = NewRuntime();
        var engine = new TdmEngine(Settings(FailurePolicy.FailRun), [runtime]);

        var manifest = await engine.RunAsync(Plan("""
            Feature: F
              Scenario: Bad
                Given a Widget exists with name "W1" and size "not-a-number"
              Scenario: NeverRuns
                Given a Widget exists with name "W2"
            """), ct: Ct);

        manifest.Run.Outcome.Should().Be(RunOutcome.Failed);
        manifest.ExitCode.Should().Be(2);
        manifest.Scenarios.Should().ContainSingle(); // second scenario never started
        runtime.Store["Widget"].Cast<Widget>().Should().NotContain(w => w.Name == "W2");
    }

    [Fact]
    public async Task UnmatchedStep_RecordedInManifest_RunContinues()
    {
        var runtime = NewRuntime();
        var engine = new TdmEngine(Settings(), [runtime]);

        var manifest = await engine.RunAsync(Plan("""
            Feature: F
              Scenario: S
                Given something entirely ungrammatical happens
                And a Widget exists with name "W1"
            """), ct: Ct);

        manifest.Run.Outcome.Should().Be(RunOutcome.CompletedWithWarnings);
        manifest.Scenarios[0].UnmatchedSteps.Should().ContainSingle()
            .Which.Text.Should().Contain("ungrammatical");
        runtime.Store["Widget"].Should().ContainSingle(); // the typo did not kill the scenario
    }

    // ---------------------------------------------------------------- Tags & lifecycle plumbing

    [Fact]
    public async Task SkippedScenario_NotExecuted()
    {
        var runtime = NewRuntime();
        var engine = new TdmEngine(Settings(), [runtime]);

        var manifest = await engine.RunAsync(Plan("""
            Feature: F
              @skip
              Scenario: S
                Given a Widget exists with name "W1"
            """), ct: Ct);

        manifest.Scenarios[0].Outcome.Should().Be(ScenarioOutcome.Skipped);
        runtime.Store["Widget"].Should().BeEmpty();
        runtime.Calls.Should().BeEmpty(); // no scenario begin/end either
    }

    [Fact]
    public async Task SeedTag_OverridesRunDefault()
    {
        var runtime = NewRuntime();
        var settings = Settings();
        settings.Run.DefaultSeed = 3;
        var engine = new TdmEngine(settings, [runtime]);

        var manifest = await engine.RunAsync(Plan("""
            Feature: F
              @seed:7
              Scenario: Tagged
                Given a Widget exists with name "W1"
              Scenario: Untagged
                Given a Widget exists with name "W2"
            """), ct: Ct);

        manifest.Scenarios[0].Seed.Should().Be(7);
        manifest.Scenarios[1].Seed.Should().Be(3);
        runtime.Calls.Should().Contain("begin:Persistent:7");
    }

    [Fact]
    public async Task LifecycleTag_OverridesRunDefault()
    {
        var runtime = NewRuntime();
        var settings = Settings();
        settings.Run.Lifecycle = LifecycleMode.Persistent;
        var engine = new TdmEngine(settings, [runtime]);

        await engine.RunAsync(Plan("""
            Feature: F
              @ephemeral
              Scenario: S
                Given a Widget exists with name "W1"
            """), ct: Ct);

        runtime.Calls.Should().Contain("begin:TrackedTeardown:1");
    }

    // ---------------------------------------------------------------- Multi-domain

    [Fact]
    public async Task AmbiguousEntity_AcrossDomains_WarnsWithCandidates()
    {
        var engine = new TdmEngine(Settings(), [NewRuntime("Alpha"), NewRuntime("Beta")]);

        var manifest = await engine.RunAsync(Plan("""
            Feature: F
              Scenario: S
                Given a Widget exists with name "W1"
            """), ct: Ct);

        manifest.Run.Outcome.Should().Be(RunOutcome.CompletedWithWarnings);
        manifest.Scenarios[0].Warnings.Should().Contain(w => w.Contains("ambiguous"));
    }

    [Fact]
    public async Task AmbiguousEntity_ResolvedByDomainQualifier()
    {
        var alpha = NewRuntime("Alpha");
        var beta = NewRuntime("Beta");
        var engine = new TdmEngine(Settings(), [alpha, beta]);

        var manifest = await engine.RunAsync(Plan("""
            Feature: F
              Scenario: S
                Given a Beta Widget exists with name "W1"
            """), ct: Ct);

        manifest.Run.Outcome.Should().Be(RunOutcome.Succeeded);
        alpha.Store["Widget"].Should().BeEmpty();
        beta.Store["Widget"].Should().ContainSingle();
    }

    [Fact]
    public async Task AmbiguousEntity_ResolvedByDomainPinTag()
    {
        var alpha = NewRuntime("Alpha");
        var beta = NewRuntime("Beta");
        var engine = new TdmEngine(Settings(), [alpha, beta]);

        await engine.RunAsync(Plan("""
            Feature: F
              @domain:Alpha
              Scenario: S
                Given a Widget exists with name "W1"
            """), ct: Ct);

        alpha.Store["Widget"].Should().ContainSingle();
        beta.Store["Widget"].Should().BeEmpty();
    }

    [Fact]
    public async Task UnknownEntity_Warns()
    {
        var runtime = NewRuntime();
        var engine = new TdmEngine(Settings(), [runtime]);

        var manifest = await engine.RunAsync(Plan("""
            Feature: F
              Scenario: S
                Given a Unicorn exists with name "U1"
            """), ct: Ct);

        manifest.Run.Outcome.Should().Be(RunOutcome.CompletedWithWarnings);
        manifest.Scenarios[0].Warnings.Should().Contain(w => w.Contains("Unicorn"));
    }

    // ---------------------------------------------------------------- Update / Delete / Load

    [Fact]
    public async Task FullVerbFlow_UpdateLoadDelete()
    {
        var runtime = NewRuntime();
        var engine = new TdmEngine(Settings(), [runtime]);

        var manifest = await engine.RunAsync(Plan("""
            Feature: F
              Scenario: S
                Given a Widget exists with name "W1" and size "5"
                When the Widget "W1" is updated with size "9"
                Then a Widget "W1" should exist with size "9"
                When the Widget "W1" is deleted
                Then 0 Widgets should exist
            """), ct: Ct);

        manifest.Run.Outcome.Should().Be(RunOutcome.Succeeded);
        runtime.Store["Widget"].Should().BeEmpty();
        runtime.Calls.Should().Contain("update:Widget").And.Contain("delete:Widget");
    }

    [Fact]
    public async Task DeleteAll_WithFilter()
    {
        var runtime = NewRuntime();
        var engine = new TdmEngine(Settings(), [runtime]);

        var manifest = await engine.RunAsync(Plan("""
            Feature: F
              Scenario: S
                Given a Widget exists with name "W1" and colour "Red"
                And a Widget exists with name "W2" and colour "Blue"
                When all Widgets with colour "Red" are deleted
                Then 1 Widgets should exist
            """), ct: Ct);

        manifest.Run.Outcome.Should().Be(RunOutcome.Succeeded);
        runtime.Store["Widget"].Should().ContainSingle()
            .Which.Should().BeOfType<Widget>().Which.Name.Should().Be("W2");
    }

    [Fact]
    public async Task LoadAssertion_Mismatch_Warns()
    {
        var runtime = NewRuntime();
        var engine = new TdmEngine(Settings(), [runtime]);

        var manifest = await engine.RunAsync(Plan("""
            Feature: F
              Scenario: S
                Given a Widget exists with name "W1" and colour "Red"
                Then a Widget "W1" should exist with colour "Blue"
            """), ct: Ct);

        manifest.Run.Outcome.Should().Be(RunOutcome.CompletedWithWarnings);
        manifest.Scenarios[0].Warnings.Should().Contain(w => w.Contains("Colour"));
    }

    // ---------------------------------------------------------------- Dry run

    [Fact]
    public async Task DryRun_ResolvesAndGenerates_ButNeverPersists()
    {
        var runtime = NewRuntime();
        var engine = new TdmEngine(Settings(), [runtime]);

        var manifest = await engine.RunAsync(Plan("""
            Feature: F
              Scenario: S
                Given a Widget exists with name "W1"
            """), dryRun: true, ct: Ct);

        manifest.Run.DryRun.Should().BeTrue();
        runtime.Store["Widget"].Should().BeEmpty();
        runtime.Calls.Should().NotContain(c => c.StartsWith("create"));
        runtime.Calls.Should().NotContain(c => c.StartsWith("begin")); // no lifecycle churn either
        manifest.Scenarios[0].Entities[0].PersistedVia.Should().Be("dry-run");
    }

    // ---------------------------------------------------------------- Manifest serialization

    [Fact]
    public async Task Manifest_RoundTripsThroughJson()
    {
        var runtime = NewRuntime();
        var engine = new TdmEngine(Settings(), [runtime]);
        var manifest = await engine.RunAsync(Plan("""
            Feature: F
              Scenario: S
                Given a Widget exists with name "W1" and colour "Red"
            """), ct: Ct);

        var json = System.Text.Json.JsonSerializer.Serialize(manifest, TdmSettings.JsonOptions);
        var restored = System.Text.Json.JsonSerializer.Deserialize<RunManifest>(json, TdmSettings.JsonOptions)!;
        restored.Run.Outcome.Should().Be(manifest.Run.Outcome);
        restored.Scenarios[0].Entities[0].NaturalKey.Should().Be("W1");
        restored.Scenarios[0].Entities[0].Values["Colour"].Should().Be("Red");
    }
}
