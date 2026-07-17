using AwesomeAssertions;
using Tdm.Core.Execution;
using Tdm.Core.Manifest;
using Tdm.Core.Settings;
using Xunit;

namespace Tdm.Core.Tests.Execution;

public class ManifestPlaybackTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly EntityDescriptor Widget = FakeDomainRuntime.Describe<Widget>("Widget", "Fake");

    private static FakeDomainRuntime Runtime() => new("Fake", Widget);

    private static EntityManifest Entry(string verb, Guid id, string name, string colour = "red", int size = 1,
        int ordinal = 1, string? persistedVia = "FakeStore") => new()
    {
        Ordinal = ordinal,
        Entity = "Widget",
        Verb = verb,
        Domain = "Fake",
        Id = id.ToString(),
        NaturalKey = name,
        PersistedVia = persistedVia,
        Values = new Dictionary<string, string?>
        {
            ["Id"] = id.ToString(),
            ["Name"] = name,
            ["Colour"] = colour,
            ["Size"] = size.ToString(),
        },
    };

    private static RunManifest Manifest(LifecycleMode lifecycle = LifecycleMode.Persistent, params EntityManifest[] entries) => new()
    {
        Scenarios =
        [
            new ScenarioManifest
            {
                Feature = "F", Scenario = "S", Seed = 1, Lifecycle = lifecycle,
                Outcome = ScenarioOutcome.Succeeded, Entities = [.. entries],
            },
        ],
    };

    // ---------------------------------------------------------------- Replay

    [Fact]
    public async Task SampledBulkOperations_WarnOnReplayAndVerify_SampledRowsStillPlayBack()
    {
        var runtime = Runtime();
        var id = Guid.NewGuid();
        var manifest = Manifest(entries: Entry("Create", id, "Sampled-1"));
        manifest.Scenarios[0].BulkOperations.Add(new BulkOperationManifest
        {
            Entity = "Widget", Domain = "Fake", Requested = 1000, Count = 1000,
            Mode = BulkManifestMode.Sample, SampledRows = 10, HashedRows = 990,
            ValuesSha256 = "abc123",
        });

        var replay = await ManifestPlayback.ReplayAsync(manifest, [runtime], ct: Ct);
        replay.Created.Should().Be(1); // the sampled entry still replays
        replay.Warnings.Should().ContainSingle().Which.Should()
            .Contain("1000 Widget").And.Contain("Sample").And.Contain("10 sampled");

        var verify = await ManifestPlayback.VerifyAsync(manifest, [runtime], ct: Ct);
        verify.Warnings.Should().Contain(w => w.Contains("Sample") && w.Contains("verifiable"));
        verify.Drift.Should().BeEmpty();
    }

    [Fact]
    public async Task Replay_CreatesRowsWithRecordedValues()
    {
        var runtime = Runtime();
        var id = Guid.NewGuid();
        var report = await ManifestPlayback.ReplayAsync(
            Manifest(entries: Entry("Create", id, "Alpha", colour: "blue", size: 7)), [runtime], ct: Ct);

        report.ExitCode.Should().Be(0);
        report.Created.Should().Be(1);
        var row = runtime.Store["Widget"].Should().ContainSingle().Subject.Should().BeOfType<Widget>().Subject;
        row.Id.Should().Be(id);
        row.Name.Should().Be("Alpha");
        row.Colour.Should().Be("blue");
        row.Size.Should().Be(7);
    }

    [Fact]
    public async Task Replay_SkipsNonPersistentScenarios_AndDryRunEntries()
    {
        var runtime = Runtime();
        var manifest = new RunManifest
        {
            Scenarios =
            [
                new ScenarioManifest
                {
                    Scenario = "torn-down", Lifecycle = LifecycleMode.TrackedTeardown,
                    Entities = [Entry("Create", Guid.NewGuid(), "Gone")],
                },
                new ScenarioManifest
                {
                    Scenario = "validated", Lifecycle = LifecycleMode.Persistent,
                    Entities = [Entry("Create", Guid.NewGuid(), "DryRun", persistedVia: "dry-run")],
                },
            ],
        };
        var report = await ManifestPlayback.ReplayAsync(manifest, [runtime], ct: Ct);

        report.Created.Should().Be(0);
        report.Skipped.Should().Be(2);
        runtime.Store["Widget"].Should().BeEmpty();
    }

    [Fact]
    public async Task Replay_ExistingRow_ReAppliesValues_InsteadOfDuplicating()
    {
        var runtime = Runtime();
        var id = Guid.NewGuid();
        var existing = new Widget { Id = id, Name = "Alpha", Colour = "faded", Size = 0 };
        runtime.Store["Widget"].Add(existing);

        var report = await ManifestPlayback.ReplayAsync(
            Manifest(entries: Entry("Create", id, "Alpha", colour: "blue", size: 7)), [runtime], ct: Ct);

        report.Created.Should().Be(0);
        report.Updated.Should().Be(1);
        runtime.Store["Widget"].Should().ContainSingle();
        existing.Colour.Should().Be("blue");
        existing.Size.Should().Be(7);
    }

    [Fact]
    public async Task Replay_UpdateEntry_AppliesToRowCreatedEarlierInTheManifest()
    {
        var runtime = Runtime();
        var id = Guid.NewGuid();
        var report = await ManifestPlayback.ReplayAsync(Manifest(entries:
        [
            Entry("Create", id, "Alpha", colour: "red", ordinal: 1),
            Entry("Update", id, "Alpha", colour: "platinum", ordinal: 2),
        ]), [runtime], ct: Ct);

        report.ExitCode.Should().Be(0);
        runtime.Store["Widget"].Cast<Widget>().Single().Colour.Should().Be("platinum");
    }

    [Fact]
    public async Task Replay_UpdateEntry_MissingRow_IsAFailure()
    {
        var report = await ManifestPlayback.ReplayAsync(
            Manifest(entries: Entry("Update", Guid.NewGuid(), "Ghost")), [Runtime()], ct: Ct);
        report.ExitCode.Should().Be(2);
        report.Failures.Should().ContainSingle().Which.Should().Contain("row to update not found");
    }

    [Fact]
    public async Task Replay_DeleteById_RemovesTheRow()
    {
        var runtime = Runtime();
        var id = Guid.NewGuid();
        var report = await ManifestPlayback.ReplayAsync(Manifest(entries:
        [
            Entry("Create", id, "Doomed", ordinal: 1),
            Entry("Delete", id, "Doomed", ordinal: 2),
        ]), [runtime], ct: Ct);

        report.Deleted.Should().Be(1);
        runtime.Store["Widget"].Should().BeEmpty();
    }

    [Fact]
    public async Task Replay_DeleteAll_CannotBeReplayed_Warning()
    {
        var deleteAll = new EntityManifest { Ordinal = 1, Entity = "Widget", Verb = "Delete", Domain = "Fake" };
        var report = await ManifestPlayback.ReplayAsync(Manifest(entries: deleteAll), [Runtime()], ct: Ct);
        report.ExitCode.Should().Be(1);
        report.Warnings.Should().ContainSingle().Which.Should().Contain("cannot be replayed exactly");
    }

    [Fact]
    public async Task Replay_RecordedPropertyGoneFromSchema_WarnsAndContinues()
    {
        var runtime = Runtime();
        var entry = Entry("Create", Guid.NewGuid(), "Alpha");
        entry.Values["RemovedInV2"] = "value";
        var report = await ManifestPlayback.ReplayAsync(Manifest(entries: entry), [runtime], ct: Ct);

        report.Created.Should().Be(1);
        report.Warnings.Should().ContainSingle().Which.Should().Contain("RemovedInV2");
    }

    [Fact]
    public async Task Replay_UnknownDomain_IsAFailure()
    {
        var entry = Entry("Create", Guid.NewGuid(), "Alpha");
        entry.Domain = "NotConfigured";
        var report = await ManifestPlayback.ReplayAsync(Manifest(entries: entry), [Runtime()], ct: Ct);
        report.ExitCode.Should().Be(2);
        report.Failures.Should().ContainSingle().Which.Should().Contain("NotConfigured");
    }

    // ---------------------------------------------------------------- Verify

    private static async Task<(FakeDomainRuntime Runtime, RunManifest Manifest)> ReplayedAsync(params EntityManifest[] entries)
    {
        var runtime = Runtime();
        var manifest = Manifest(entries: entries);
        (await ManifestPlayback.ReplayAsync(manifest, [runtime], ct: Ct)).ExitCode.Should().Be(0);
        return (runtime, manifest);
    }

    [Fact]
    public async Task Verify_AfterReplay_NoDrift()
    {
        var (runtime, manifest) = await ReplayedAsync(Entry("Create", Guid.NewGuid(), "Alpha", colour: "blue", size: 7));
        var report = await ManifestPlayback.VerifyAsync(manifest, [runtime], ct: Ct);
        report.ExitCode.Should().Be(0);
        report.RowsChecked.Should().Be(1);
        report.Drift.Should().BeEmpty();
    }

    [Fact]
    public async Task Verify_MissingRow_IsDrift()
    {
        var (runtime, manifest) = await ReplayedAsync(Entry("Create", Guid.NewGuid(), "Alpha"));
        runtime.Store["Widget"].Clear(); // someone deleted the row

        var report = await ManifestPlayback.VerifyAsync(manifest, [runtime], ct: Ct);
        report.ExitCode.Should().Be(1);
        report.Drift.Should().ContainSingle().Which.Should().Contain("missing");
    }

    [Fact]
    public async Task Verify_ChangedValue_IsDrift_NamingPropertyAndValues()
    {
        var (runtime, manifest) = await ReplayedAsync(Entry("Create", Guid.NewGuid(), "Alpha", colour: "blue"));
        runtime.Store["Widget"].Cast<Widget>().Single().Colour = "vandalised";

        var report = await ManifestPlayback.VerifyAsync(manifest, [runtime], ct: Ct);
        report.ExitCode.Should().Be(1);
        report.Drift.Should().ContainSingle().Which
            .Should().Contain("Colour").And.Contain("vandalised").And.Contain("blue");
    }

    [Fact]
    public async Task Verify_LastWriteWins_UpdateValuesAreTheExpectation()
    {
        var id = Guid.NewGuid();
        var (runtime, manifest) = await ReplayedAsync(
            Entry("Create", id, "Alpha", colour: "red", ordinal: 1),
            Entry("Update", id, "Alpha", colour: "platinum", ordinal: 2));

        // Row holds the updated value; verifying against the manifest must expect it too.
        (await ManifestPlayback.VerifyAsync(manifest, [runtime], ct: Ct)).ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task Verify_DeletedRow_StayingDeleted_IsFine_Reappearing_IsDrift()
    {
        var id = Guid.NewGuid();
        var (runtime, manifest) = await ReplayedAsync(
            Entry("Create", id, "Doomed", ordinal: 1),
            Entry("Delete", id, "Doomed", ordinal: 2));

        (await ManifestPlayback.VerifyAsync(manifest, [runtime], ct: Ct)).ExitCode.Should().Be(0);

        runtime.Store["Widget"].Add(new Widget { Id = id, Name = "Doomed" }); // zombie row
        var report = await ManifestPlayback.VerifyAsync(manifest, [runtime], ct: Ct);
        report.ExitCode.Should().Be(1);
        report.Drift.Should().ContainSingle().Which.Should().Contain("exists again");
    }

    [Fact]
    public async Task Verify_DeleteAll_MakesEarlierRowsUnverifiable_NoFalseDrift()
    {
        var runtime = Runtime();
        var manifest = Manifest(entries:
        [
            Entry("Create", Guid.NewGuid(), "Victim", ordinal: 1),
            new EntityManifest { Ordinal = 2, Entity = "Widget", Verb = "Delete", Domain = "Fake" }, // delete-all
        ]);

        var report = await ManifestPlayback.VerifyAsync(manifest, [runtime], ct: Ct);
        report.ExitCode.Should().Be(0); // nothing verifiable — but no false "missing row" drift
        report.RowsChecked.Should().Be(0);
        report.Warnings.Should().ContainSingle().Which.Should().Contain("unverifiable");
    }

    [Fact]
    public async Task Verify_SkipsNonPersistentScenarios()
    {
        var runtime = Runtime();
        var manifest = Manifest(LifecycleMode.TrackedTeardown, Entry("Create", Guid.NewGuid(), "Ephemeral"));
        var report = await ManifestPlayback.VerifyAsync(manifest, [runtime], ct: Ct);
        report.RowsChecked.Should().Be(0);
        report.SkippedScenarios.Should().Be(1);
        report.ExitCode.Should().Be(0);
    }
}
