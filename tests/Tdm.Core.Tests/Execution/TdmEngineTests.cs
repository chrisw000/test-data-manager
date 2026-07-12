using Tdm.Core.Execution;
using Tdm.Core.Grammar;
using Tdm.Core.Manifest;
using Tdm.Core.Settings;
using Tdm.Identity;
using Xunit;

namespace Tdm.Core.Tests.Execution;

public class TdmEngineTests
{
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
            """));

        Assert.Equal(RunOutcome.Succeeded, manifest.Run.Outcome);
        Assert.Equal(0, manifest.ExitCode);
        var widget = Assert.IsType<Widget>(Assert.Single(runtime.Store["Widget"]));
        Assert.Equal(("W1", "Red", 5), (widget.Name, widget.Colour, widget.Size));
        Assert.Equal(TdmIdentity.ForNaturalKey("Test", "Widget", "W1"), widget.Id);

        var entry = Assert.Single(Assert.Single(manifest.Scenarios).Entities);
        Assert.Equal("FakeStore", entry.PersistedVia);
        Assert.Equal(["Name", "Colour", "Size"], entry.OverridesApplied);
        Assert.Equal("W1", entry.NaturalKey);
        Assert.Equal("Red", entry.Values["Colour"]);
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
            """));

        Assert.Equal(RunOutcome.Succeeded, manifest.Run.Outcome);
        var widget = Assert.IsType<Widget>(Assert.Single(runtime.Store["Widget"]));
        // Declared state re-applied on reuse.
        Assert.Equal("Blue", widget.Colour);
        Assert.StartsWith("already-existed", manifest.Scenarios[1].Entities[0].PersistedVia);
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
            """));

        Assert.Equal(RunOutcome.Succeeded, manifest.Run.Outcome);
        Assert.Equal(5, runtime.Store["Widget"].Count);
        Assert.Contains("createBulk:Widget:5", runtime.Calls);
        var ids = runtime.Store["Widget"].Cast<Widget>().Select(w => w.Id).ToList();
        Assert.Equal(5, ids.Distinct().Count());
        Assert.Equal(TdmIdentity.ForOrdinal("Test", "Widget", "Bulk", 1, 1), ids[0]);
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
            """));

        Assert.Equal(RunOutcome.CompletedWithWarnings, manifest.Run.Outcome);
        Assert.Equal(1, manifest.ExitCode);
        Assert.Empty(runtime.Store["Widget"]);
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
            """));

        Assert.Equal(RunOutcome.Succeeded, manifest.Run.Outcome);
        var widget = Assert.IsType<Widget>(runtime.Store["Widget"][0]);
        var gadget = Assert.IsType<Gadget>(runtime.Store["Gadget"][0]);
        Assert.Equal(widget.Id, gadget.WidgetId);
        var reference = Assert.Single(Assert.Single(manifest.Scenarios).References);
        Assert.Equal("contextBag", reference.ResolvedFrom);
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
            """));

        Assert.Equal(RunOutcome.Succeeded, manifest.Run.Outcome);
        Assert.Equal(existing.Id, Assert.IsType<Gadget>(runtime.Store["Gadget"][0]).WidgetId);
        Assert.Equal("database", Assert.Single(manifest.Scenarios[0].References).ResolvedFrom);
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
            """));

        Assert.Equal(RunOutcome.CompletedWithWarnings, manifest.Run.Outcome);
        Assert.Contains(manifest.Scenarios[0].Warnings, w => w.Contains("Ghost"));
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
            """));

        Assert.Equal(RunOutcome.Succeeded, manifest.Run.Outcome);
        var expected = TdmIdentity.ForNaturalKey("CRM", "Widget", "W9");
        Assert.Equal(expected, Assert.IsType<Gadget>(runtime.Store["Gadget"][0]).WidgetId);
        var external = manifest.Scenarios[0].References.First(r => r.ResolvedFrom == "identityContract");
        Assert.Equal("CRM", external.OwningDomain);
        Assert.Equal(expected.ToString(), external.Id);
        // Nothing persisted in the owning domain — no Widget row was created.
        Assert.Empty(runtime.Store["Widget"]);
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
            """));

        Assert.Equal(RunOutcome.CompletedWithWarnings, manifest.Run.Outcome);
        var widget = Assert.IsType<Widget>(Assert.Single(runtime.Store["Widget"]));
        Assert.Equal("W1", widget.Name);
        Assert.Equal(0, widget.Size); // skipped, left at default
        var entry = manifest.Scenarios[0].Entities[0];
        Assert.Contains(entry.Warnings, w => w.Contains("Size"));
        Assert.DoesNotContain("Size", entry.OverridesApplied);
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
            """));

        // The bad object is rejected; the scenario continues and the good one persists.
        Assert.Empty(runtime.Store["Widget"].Cast<Widget>().Where(w => w.Name == "W1"));
        Assert.Single(runtime.Store["Widget"].Cast<Widget>(), w => w.Name == "W2");
        Assert.NotEqual(RunOutcome.Succeeded, manifest.Run.Outcome);
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
            """));

        Assert.Equal(RunOutcome.Failed, manifest.Run.Outcome);
        Assert.Equal(2, manifest.ExitCode);
        Assert.Single(manifest.Scenarios); // second scenario never started
        Assert.DoesNotContain(runtime.Store["Widget"].Cast<Widget>(), w => w.Name == "W2");
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
            """));

        Assert.Equal(RunOutcome.CompletedWithWarnings, manifest.Run.Outcome);
        var unmatched = Assert.Single(manifest.Scenarios[0].UnmatchedSteps);
        Assert.Contains("ungrammatical", unmatched.Text);
        Assert.Single(runtime.Store["Widget"]); // the typo did not kill the scenario
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
            """));

        Assert.Equal(ScenarioOutcome.Skipped, manifest.Scenarios[0].Outcome);
        Assert.Empty(runtime.Store["Widget"]);
        Assert.Empty(runtime.Calls); // no scenario begin/end either
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
            """));

        Assert.Equal(7, manifest.Scenarios[0].Seed);
        Assert.Equal(3, manifest.Scenarios[1].Seed);
        Assert.Contains("begin:Persistent:7", runtime.Calls);
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
            """));

        Assert.Contains("begin:TrackedTeardown:1", runtime.Calls);
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
            """));

        Assert.Equal(RunOutcome.CompletedWithWarnings, manifest.Run.Outcome);
        Assert.Contains(manifest.Scenarios[0].Warnings, w => w.Contains("ambiguous"));
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
            """));

        Assert.Equal(RunOutcome.Succeeded, manifest.Run.Outcome);
        Assert.Empty(alpha.Store["Widget"]);
        Assert.Single(beta.Store["Widget"]);
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
            """));

        Assert.Single(alpha.Store["Widget"]);
        Assert.Empty(beta.Store["Widget"]);
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
            """));

        Assert.Equal(RunOutcome.CompletedWithWarnings, manifest.Run.Outcome);
        Assert.Contains(manifest.Scenarios[0].Warnings, w => w.Contains("Unicorn"));
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
            """));

        Assert.Equal(RunOutcome.Succeeded, manifest.Run.Outcome);
        Assert.Empty(runtime.Store["Widget"]);
        Assert.Contains("update:Widget", runtime.Calls);
        Assert.Contains("delete:Widget", runtime.Calls);
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
            """));

        Assert.Equal(RunOutcome.Succeeded, manifest.Run.Outcome);
        Assert.Equal("W2", Assert.IsType<Widget>(Assert.Single(runtime.Store["Widget"])).Name);
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
            """));

        Assert.Equal(RunOutcome.CompletedWithWarnings, manifest.Run.Outcome);
        Assert.Contains(manifest.Scenarios[0].Warnings, w => w.Contains("Colour"));
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
            """), dryRun: true);

        Assert.True(manifest.Run.DryRun);
        Assert.Empty(runtime.Store["Widget"]);
        Assert.DoesNotContain(runtime.Calls, c => c.StartsWith("create"));
        Assert.DoesNotContain(runtime.Calls, c => c.StartsWith("begin")); // no lifecycle churn either
        Assert.Equal("dry-run", manifest.Scenarios[0].Entities[0].PersistedVia);
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
            """));

        var json = System.Text.Json.JsonSerializer.Serialize(manifest, TdmSettings.JsonOptions);
        var restored = System.Text.Json.JsonSerializer.Deserialize<RunManifest>(json, TdmSettings.JsonOptions)!;
        Assert.Equal(manifest.Run.Outcome, restored.Run.Outcome);
        Assert.Equal("W1", restored.Scenarios[0].Entities[0].NaturalKey);
        Assert.Equal("Red", restored.Scenarios[0].Entities[0].Values["Colour"]);
    }
}
