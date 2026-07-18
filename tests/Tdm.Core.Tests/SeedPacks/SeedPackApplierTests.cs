using AwesomeAssertions;
using Tdm.Core.Execution;
using Tdm.Core.Grammar;
using Tdm.Core.Manifest;
using Tdm.Core.Registry;
using Tdm.Core.SeedPacks;
using Tdm.Core.Settings;
using Tdm.Core.Tests.Execution;
using Tdm.Identity;
using Xunit;

namespace Tdm.Core.Tests.SeedPacks;

public class SeedPackApplierTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"tdm-packs-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Writes a pack folder: features + optional fragment + optional key registry.</summary>
    private SeedPackContent Pack(string name, string version = "1.0.0",
        (string File, string Text)[]? features = null, string? fragmentJson = null, string? keysJson = null)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(folder, "features"));
        foreach (var (file, text) in features ?? [])
            File.WriteAllText(Path.Combine(folder, "features", file), text);
        if (fragmentJson is not null)
            File.WriteAllText(Path.Combine(folder, SeedPackContent.FragmentFileName), fragmentJson);
        if (keysJson is not null)
            File.WriteAllText(Path.Combine(folder, KeyRegistryDocument.FileName), keysJson);
        return SeedPackContent.Load(name, version, folder);
    }

    // ---------------------------------------------------------------- config merge

    [Fact]
    public void MergeConfig_PackFragmentsApply_LocalSettingsWin()
    {
        var pack = Pack("base-data", fragmentJson: """
            {
              "entities": {
                "Widget": { "naturalKey": "Colour" },
                "Gadget": { "naturalKey": "Name" }
              },
              "datasets": { "cities": { "path": "datasets/cities.csv" } }
            }
            """);
        var settings = new TdmSettings
        {
            Entities = { ["Widget"] = new EntitySettings { NaturalKey = "Name" } }, // local wins
        };

        SeedPackApplier.MergeConfig(settings, [pack]);

        settings.Entities["Widget"].NaturalKey.Should().Be("Name");
        settings.Entities["Gadget"].NaturalKey.Should().Be("Name");
        // Pack dataset paths anchor at the pack root, not the consuming repo's settings file.
        settings.Datasets["cities"].Path.Should().Be(
            Path.GetFullPath(Path.Combine(_root, "base-data", "datasets", "cities.csv")));
    }

    [Fact]
    public void MergeConfig_TwoPacksConfiguringTheSameKey_FailLoudly()
    {
        var first = Pack("pack-a", fragmentJson: """{ "entities": { "Widget": { "naturalKey": "Name" } } }""");
        var second = Pack("pack-b", fragmentJson: """{ "entities": { "Widget": { "naturalKey": "Colour" } } }""");

        FluentActions.Invoking(() => SeedPackApplier.MergeConfig(new TdmSettings(), [first, second]))
            .Should().Throw<InvalidOperationException>()
            .WithMessage("*entity 'Widget'*'pack-a'*'pack-b'*local entities*wins*");
    }

    // ---------------------------------------------------------------- plan ordering

    [Fact]
    public void BuildPlan_PackFeaturesRunFirst_PackListOrder_AlphabeticalWithin()
    {
        var packOne = Pack("one", features:
        [
            ("b-second.feature", "Feature: One-B\n  Scenario: S\n    Given a Widget exists\n"),
            ("a-first.feature", "Feature: One-A\n  Scenario: S\n    Given a Widget exists\n"),
        ]);
        var packTwo = Pack("two", features:
        [
            ("z.feature", "Feature: Two-Z\n  Scenario: S\n    Given a Widget exists\n"),
        ]);
        var localDirectory = Directory.CreateDirectory(Path.Combine(_root, "repo", "features")).FullName;
        File.WriteAllText(Path.Combine(localDirectory, "local.feature"),
            "Feature: Local\n  Scenario: S\n    Given a Widget exists\n");

        var plan = SeedPackApplier.BuildPlan(new GherkinPlanParser(), [packOne, packTwo],
            ["features/**/*.feature"], Path.Combine(_root, "repo"));

        plan.Features.Select(f => f.Name).Should().Equal("One-A", "One-B", "Two-Z", "Local");
    }

    /// <summary>The §5 acceptance criterion: two repos consuming the same seed pack version
    /// produce identical customer identities — the identity contract makes ids a pure
    /// function of (domain, entity, key), and pack execution order is deterministic.</summary>
    [Fact]
    public async Task TwoRepos_SameSeedPack_IdenticalIdentities()
    {
        var pack = Pack("eu-reference-customers", "2.1.0", features:
        [
            ("customers.feature", """
                Feature: EU reference customers
                  Scenario: Base customers
                    Given a Widget exists with name "EU-001"
                    And a Widget exists with name "EU-002"
                """),
        ]);

        async Task<List<string?>> RunFromRepoAsync(string repoName)
        {
            var repoDirectory = Directory.CreateDirectory(Path.Combine(_root, repoName)).FullName;
            var plan = SeedPackApplier.BuildPlan(new GherkinPlanParser(), [pack], [], repoDirectory);
            var runtime = new FakeDomainRuntime("Orders", FakeDomainRuntime.Describe<Widget>("Widget", "Orders"));
            var settings = new TdmSettings { Run = new RunSettings { Name = repoName, Lifecycle = LifecycleMode.Persistent } };
            settings.ApplyDefaults();
            var manifest = await new TdmEngine(settings, [runtime]).RunAsync(plan, ct: Ct);
            manifest.Run.Outcome.Should().Be(RunOutcome.Succeeded);
            return [.. manifest.Scenarios.SelectMany(s => s.Entities).Select(e => e.Id)];
        }

        var first = await RunFromRepoAsync("repo-one");
        var second = await RunFromRepoAsync("repo-two");

        second.Should().Equal(first);
        first[0].Should().Be(TdmIdentity.ForNaturalKey("Orders", "Widget", "EU-001").ToString());
        first[1].Should().Be(TdmIdentity.ForNaturalKey("Orders", "Widget", "EU-002").ToString());
    }

    // ---------------------------------------------------------------- key registries

    [Fact]
    public void CollectKeyRegistries_PacksAddDomains_PluginRegistryStaysAuthoritative()
    {
        var pack = Pack("crm-keys", keysJson: """
            { "domain": "CRM", "entities": { "Customer": { "naturalKey": "Name", "keys": ["Acme Ltd"] } } }
            """);
        var pluginRegistries = new Dictionary<string, KeyRegistryDocument>(StringComparer.OrdinalIgnoreCase)
        {
            ["Orders"] = new KeyRegistryDocument { Domain = "Orders" },
        };

        var merged = SeedPackApplier.CollectKeyRegistries([pack], pluginRegistries);
        merged.Keys.Should().BeEquivalentTo(["Orders", "CRM"]);
        merged["CRM"].IsKeyKnown("Customer", "Acme Ltd").Should().BeTrue();
        merged["CRM"].IsKeyKnown("Customer", "Unknown Ltd").Should().BeFalse();

        // The domain's own plugin-published registry wins over a pack's copy.
        var packWithOrders = Pack("orders-keys", keysJson: """{ "domain": "Orders", "entities": {} }""");
        var stillPlugin = SeedPackApplier.CollectKeyRegistries([packWithOrders], pluginRegistries);
        stillPlugin["Orders"].Should().BeSameAs(pluginRegistries["Orders"]);
    }

    [Fact]
    public void CollectKeyRegistries_TwoPacksForOneDomain_FailLoudly()
    {
        var first = Pack("keys-a", keysJson: """{ "domain": "CRM", "entities": {} }""");
        var second = Pack("keys-b", keysJson: """{ "domain": "crm", "entities": {} }""");

        FluentActions.Invoking(() => SeedPackApplier.CollectKeyRegistries([first, second],
                new Dictionary<string, KeyRegistryDocument>()))
            .Should().Throw<InvalidOperationException>().WithMessage("*'keys-a'*'keys-b'*crm*");
    }

    [Fact]
    public void Load_MissingFolder_FailsActionably()
    {
        FluentActions.Invoking(() => SeedPackContent.Load("nope", "1.0.0", Path.Combine(_root, "missing")))
            .Should().Throw<InvalidOperationException>().WithMessage("*'nope'*folder not found*");
    }
}
