using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using Tdm.Core.Settings;
using Tdm.Plugins;
using Xunit;

namespace Tdm.Plugins.Tests;

/// <summary>
/// Local folder-based NuGet feed fixture (W1-P2): builds real .nupkg files (nuspec +
/// lib/net10.0 payload) into a temp feed folder that NuGet.Protocol treats as a source.
/// </summary>
public sealed class TestFeed : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "tdm-plugins-tests", Guid.NewGuid().ToString("N"));

    public TestFeed()
    {
        Directory.CreateDirectory(FeedDir);
        Directory.CreateDirectory(WorkDir);
    }

    public string FeedDir => Path.Combine(_root, "feed");
    /// <summary>Base directory for the acquirer — tdm.plugins.lock.json lands here.</summary>
    public string WorkDir => Path.Combine(_root, "work");
    public string CacheDir => Path.Combine(_root, "cache");
    public string PluginsDir => Path.Combine(_root, "plugins");
    public string LockPath => Path.Combine(WorkDir, PluginLockFile.FileName);

    public PluginsSettings Plugins => new()
    {
        Acquisition = PluginAcquisitionMode.NuGet,
        Feeds = [new PluginFeedSettings { Url = FeedDir }],
        CachePath = CacheDir,
    };

    public NuGetPluginAcquirer Acquirer(bool update = false) =>
        new(Plugins, WorkDir) { UpdatePlugins = update };

    public DomainSettings Domain(string package, string? version = null) => new()
    {
        Name = "Orders",
        Package = package,
        PackageVersion = version,
        PluginPath = PluginsDir,
    };

    /// <summary>Adds {id}.{version}.nupkg with a lib/net10.0 payload and optional dependencies.</summary>
    public void AddPackage(string id, string version,
        (string Id, string Range)[]? dependencies = null,
        string? payloadFileName = null, byte[]? payload = null)
    {
        var dependencyXml = dependencies is { Length: > 0 }
            ? "<dependencies><group targetFramework=\"net10.0\">" +
              string.Concat(dependencies.Select(d => $"<dependency id=\"{d.Id}\" version=\"{d.Range}\" />")) +
              "</group></dependencies>"
            : "";
        var nuspec = $"""
            <?xml version="1.0"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{id}</id>
                <version>{version}</version>
                <authors>tdm-tests</authors>
                <description>feed fixture</description>
                {dependencyXml}
              </metadata>
            </package>
            """;

        using var zip = ZipFile.Open(Path.Combine(FeedDir, $"{id}.{version}.nupkg"), ZipArchiveMode.Create);
        WriteEntry(zip, $"{id}.nuspec", Encoding.UTF8.GetBytes(nuspec));
        WriteEntry(zip, $"lib/net10.0/{payloadFileName ?? id + ".dll"}",
            payload ?? Encoding.UTF8.GetBytes($"payload {id} {version}"));
    }

    /// <summary>Adds a package whose payload is the real sample-domain assembly, so the
    /// full PluginLoader path can load what the acquirer extracted.</summary>
    public void AddRealDomainPackage(string version)
    {
        var assemblyPath = typeof(Acme.Orders.Data.Persistence.OrdersDbContext).Assembly.Location;
        AddPackage("Acme.Orders.Data.Persistence", version,
            payloadFileName: Path.GetFileName(assemblyPath), payload: File.ReadAllBytes(assemblyPath));
    }

    private static void WriteEntry(ZipArchive zip, string path, byte[] content)
    {
        using var stream = zip.CreateEntry(path).Open();
        stream.Write(content);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}

public class NuGetPluginAcquirerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Resolve_LatestStable_ExtractsAndWritesLockfile()
    {
        using var feed = new TestFeed();
        feed.AddPackage("Acme.Orders.Data.Persistence", "1.0.0");
        feed.AddPackage("Acme.Orders.Data.Persistence", "1.1.0");

        var acquired = await feed.Acquirer().AcquireAsync(feed.Domain("Acme.Orders.Data.Persistence"), Ct);

        acquired.Folder.Should().Be(feed.PluginsDir);
        acquired.Packages.Should().Contain("Acme.Orders.Data.Persistence", "1.1.0");
        File.Exists(Path.Combine(feed.PluginsDir, "Acme.Orders.Data.Persistence.dll")).Should().BeTrue();

        var lockFile = PluginLockFile.Load(feed.WorkDir);
        var entry = lockFile.Domains["Orders"]["Acme.Orders.Data.Persistence"];
        entry.Version.Should().Be("1.1.0");
        entry.Sha512.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Lockfile_Honoured_OverNewerFeedVersion_UntilUpdate()
    {
        using var feed = new TestFeed();
        feed.AddPackage("Acme.Orders.Data.Persistence", "1.1.0");
        var domain = feed.Domain("Acme.Orders.Data.Persistence");

        (await feed.Acquirer().AcquireAsync(domain, Ct))
            .Packages["Acme.Orders.Data.Persistence"].Should().Be("1.1.0");

        // A newer version appears on the feed — the lockfile pins the run to 1.1.0 …
        feed.AddPackage("Acme.Orders.Data.Persistence", "2.0.0");
        (await feed.Acquirer().AcquireAsync(domain, Ct))
            .Packages["Acme.Orders.Data.Persistence"].Should().Be("1.1.0");

        // … until an explicit --update-plugins re-resolve.
        (await feed.Acquirer(update: true).AcquireAsync(domain, Ct))
            .Packages["Acme.Orders.Data.Persistence"].Should().Be("2.0.0");
    }

    [Fact]
    public async Task FloatingRange_ResolvesBestMatchWithinRange()
    {
        using var feed = new TestFeed();
        feed.AddPackage("Acme.Orders.Data.Persistence", "1.0.0");
        feed.AddPackage("Acme.Orders.Data.Persistence", "1.1.0");
        feed.AddPackage("Acme.Orders.Data.Persistence", "2.0.0");

        var acquired = await feed.Acquirer()
            .AcquireAsync(feed.Domain("Acme.Orders.Data.Persistence", version: "1.*"), Ct);

        acquired.Packages["Acme.Orders.Data.Persistence"].Should().Be("1.1.0");
    }

    [Fact]
    public async Task TransitiveDependencies_Followed_SharedPrefixesExcluded()
    {
        using var feed = new TestFeed();
        // The root depends on a real feed package AND on shared-prefix packages that the feed
        // does NOT carry — success proves they were excluded rather than resolved.
        feed.AddPackage("Acme.Orders.Data.Persistence", "1.0.0", dependencies:
        [
            ("Acme.Orders.Contracts", "1.0.0"),
            ("Microsoft.EntityFrameworkCore", "8.0.0"),
            ("Tdm.Identity", "0.1.0"),
        ]);
        feed.AddPackage("Acme.Orders.Contracts", "1.0.0");

        var acquired = await feed.Acquirer().AcquireAsync(feed.Domain("Acme.Orders.Data.Persistence"), Ct);

        acquired.Packages.Keys.Should().BeEquivalentTo("Acme.Orders.Data.Persistence", "Acme.Orders.Contracts");
        File.Exists(Path.Combine(feed.PluginsDir, "Acme.Orders.Contracts.dll")).Should().BeTrue();
    }

    [Fact]
    public async Task LockedHashMismatch_Throws()
    {
        using var feed = new TestFeed();
        feed.AddPackage("Acme.Orders.Data.Persistence", "1.0.0");
        var domain = feed.Domain("Acme.Orders.Data.Persistence");
        await feed.Acquirer().AcquireAsync(domain, Ct);

        var lockFile = PluginLockFile.Load(feed.WorkDir);
        lockFile.Domains["Orders"]["Acme.Orders.Data.Persistence"].Sha512 = Convert.ToBase64String(new byte[64]);
        lockFile.Save();

        await FluentActions.Awaiting(() => feed.Acquirer().AcquireAsync(domain, Ct))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*hash mismatch*");
    }

    [Fact]
    public async Task MissingPackage_Throws_NamingFeeds()
    {
        using var feed = new TestFeed();
        await FluentActions.Awaiting(() => feed.Acquirer().AcquireAsync(feed.Domain("No.Such.Package"), Ct))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No.Such.Package*not found on any configured feed*");
    }

    [Fact]
    public async Task FullPath_PluginLoaderLoadsAcquiredAssemblies_AndCarriesPackages()
    {
        using var feed = new TestFeed();
        feed.AddRealDomainPackage("1.2.3");

        var loader = new PluginLoader(feed.Acquirer());
        var plugin = await loader.LoadAsync(feed.Domain("Acme.Orders.Data.Persistence"), Ct);
        try
        {
            plugin.Assemblies.Should().ContainSingle()
                .Which.GetName().Name.Should().Be("Acme.Orders.Data.Persistence");
            plugin.Packages["Acme.Orders.Data.Persistence"].Should().Be("1.2.3");
        }
        finally
        {
            plugin.LoadContext.Unload();
        }
    }
}
