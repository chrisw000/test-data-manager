using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using Tdm.Core.Settings;
using Xunit;

namespace Tdm.Plugins.Tests;

/// <summary>Seed packs ride the plugin acquisition/lockfile flow (W4-D7) — same feed
/// fixture as the plugin acquirer tests, pack payload under content/.</summary>
public sealed class SeedPackResolverTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestFeed _feed = new();

    public void Dispose() => _feed.Dispose();

    private SeedPackResolver Resolver(bool update = false) =>
        new(_feed.Plugins, _feed.WorkDir) { UpdatePlugins = update };

    /// <summary>Builds {id}.{version}.nupkg whose payload is pack content (features + fragment + keys).</summary>
    private void AddPackPackage(string id, string version, string featureText,
        string? fragmentJson = null, string? keysJson = null)
    {
        var nuspec = $"""
            <?xml version="1.0"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{id}</id>
                <version>{version}</version>
                <authors>tdm-tests</authors>
                <description>seed pack fixture</description>
              </metadata>
            </package>
            """;
        using var zip = ZipFile.Open(Path.Combine(_feed.FeedDir, $"{id}.{version}.nupkg"), ZipArchiveMode.Create);
        WriteEntry(zip, $"{id}.nuspec", nuspec);
        WriteEntry(zip, "content/features/customers.feature", featureText);
        if (fragmentJson is not null) WriteEntry(zip, "content/tdm.entities.json", fragmentJson);
        if (keysJson is not null) WriteEntry(zip, "content/tdm.keys.json", keysJson);

        static void WriteEntry(ZipArchive zip, string path, string content)
        {
            using var stream = zip.CreateEntry(path).Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }
    }

    [Fact]
    public async Task FolderPack_IsUsedInPlace()
    {
        var folder = Path.Combine(_feed.WorkDir, "local-pack");
        Directory.CreateDirectory(Path.Combine(folder, "features"));
        File.WriteAllText(Path.Combine(folder, "features", "x.feature"), "Feature: X\n");

        var packs = await Resolver().ResolveAsync([new SeedPackSettings { Path = folder }], Ct);

        var pack = packs.Should().ContainSingle().Subject;
        pack.Name.Should().Be("local-pack");
        pack.Version.Should().Be("(local)");
        pack.RootFolder.Should().Be(folder);
    }

    [Fact]
    public async Task NuGetPack_ExtractsContent_AndPinsTheLockfile()
    {
        AddPackPackage("Acme.SeedPacks.EuCustomers", "2.1.0",
            "Feature: EU customers\n  Scenario: S\n    Given a Customer exists\n",
            fragmentJson: """{ "entities": { "Customer": { "naturalKey": "Name" } } }""",
            keysJson: """{ "domain": "Orders", "entities": {} }""");

        var packs = await Resolver().ResolveAsync(
            [new SeedPackSettings { Package = "Acme.SeedPacks.EuCustomers", Version = "2.1.0" }], Ct);

        var pack = packs.Should().ContainSingle().Subject;
        pack.Version.Should().Be("2.1.0");
        File.Exists(Path.Combine(pack.RootFolder, "features", "customers.feature")).Should().BeTrue();
        pack.Fragment.Entities.Should().ContainKey("Customer");
        pack.KeyRegistry!.Domain.Should().Be("Orders");

        var lockFile = PluginLockFile.Load(_feed.WorkDir);
        lockFile.Packs["Acme.SeedPacks.EuCustomers"].Version.Should().Be("2.1.0");
        lockFile.Packs["Acme.SeedPacks.EuCustomers"].Sha512.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Lockfile_PinsAcrossNewerVersions_UntilUpdate()
    {
        AddPackPackage("Acme.SeedPacks.Base", "1.0.0", "Feature: V1\n");
        var first = await Resolver().ResolveAsync([new SeedPackSettings { Package = "Acme.SeedPacks.Base" }], Ct);
        first[0].Version.Should().Be("1.0.0");

        // A newer version appears on the feed — the locked run must not move.
        AddPackPackage("Acme.SeedPacks.Base", "2.0.0", "Feature: V2\n");
        var locked = await Resolver().ResolveAsync([new SeedPackSettings { Package = "Acme.SeedPacks.Base" }], Ct);
        locked[0].Version.Should().Be("1.0.0", "the lockfile pins the resolved pack version");

        var updated = await Resolver(update: true).ResolveAsync([new SeedPackSettings { Package = "Acme.SeedPacks.Base" }], Ct);
        updated[0].Version.Should().Be("2.0.0");
    }

    [Fact]
    public async Task MissingPackage_And_MissingConfig_FailActionably()
    {
        await FluentActions.Awaiting(() => Resolver().ResolveAsync(
                [new SeedPackSettings { Package = "Acme.SeedPacks.Nope" }], Ct))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*'Acme.SeedPacks.Nope'*not found*");

        await FluentActions.Awaiting(() => Resolver().ResolveAsync([new SeedPackSettings()], Ct))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*need either \"package\"*or \"path\"*");
    }
}
