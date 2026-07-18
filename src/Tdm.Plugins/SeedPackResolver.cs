using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using NuGet.Configuration;
using NuGet.Packaging;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Tdm.Core.SeedPacks;
using Tdm.Core.Settings;

namespace Tdm.Plugins;

/// <summary>
/// Resolves seed packs (W4-D7) onto disk. Local-folder packs (dev / CI-restored) are used
/// in place; NuGet packs ride the existing plugin feeds and pin their resolved versions in
/// tdm.plugins.lock.json (a "packs" section) with the same content-hash verification as
/// plugin packages — versioned, reviewable, reproducible shared data.
/// </summary>
public sealed class SeedPackResolver(PluginsSettings plugins, string? baseDirectory = null, ILogger? logger = null)
{
    private readonly string _baseDirectory = baseDirectory ?? Directory.GetCurrentDirectory();
    private readonly NuGet.Common.ILogger _nugetLog = NuGet.Common.NullLogger.Instance;

    /// <summary>When true, ignore the lockfile and re-resolve (host --update-plugins).</summary>
    public bool UpdatePlugins { get; init; }

    public async Task<IReadOnlyList<SeedPackContent>> ResolveAsync(
        IReadOnlyList<SeedPackSettings> packs, CancellationToken ct = default)
    {
        if (packs.Count == 0) return [];
        var resolved = new List<SeedPackContent>();
        PluginLockFile? lockFile = null;

        foreach (var pack in packs)
        {
            if (pack.Path is { Length: > 0 } localPath)
            {
                var folder = Path.GetFullPath(localPath, _baseDirectory);
                resolved.Add(SeedPackContent.Load(
                    pack.Package ?? Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, '/')),
                    pack.Version ?? "(local)", folder));
                continue;
            }
            if (string.IsNullOrWhiteSpace(pack.Package))
                throw new InvalidOperationException("seedPacks entries need either \"package\" (NuGet) or \"path\" (local folder).");

            lockFile ??= PluginLockFile.Load(_baseDirectory);
            resolved.Add(await ResolveNuGetPackAsync(pack, lockFile, ct).ConfigureAwait(false));
        }
        lockFile?.Save();
        return resolved;
    }

    private async Task<SeedPackContent> ResolveNuGetPackAsync(SeedPackSettings pack, PluginLockFile lockFile,
        CancellationToken ct)
    {
        if (plugins.Feeds.Count == 0)
        {
            throw new InvalidOperationException(
                $"Seed pack '{pack.Package}': no plugins.feeds configured — packs resolve from the same feeds as plugins.");
        }
        var cachePath = plugins.ResolveCachePath();
        Directory.CreateDirectory(cachePath);
        using var sourceCache = new SourceCacheContext();
        var repositories = BuildRepositories();

        string version;
        string nupkgPath;
        var locked = !UpdatePlugins && lockFile.Packs.TryGetValue(pack.Package!, out var entry) ? entry : null;
        if (locked is not null)
        {
            version = locked.Version;
            nupkgPath = await EnsureCachedAsync(pack.Package!, NuGetVersion.Parse(version), repositories,
                sourceCache, cachePath, ct).ConfigureAwait(false);
            var hash = HashFile(nupkgPath);
            if (!string.Equals(hash, locked.Sha512, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Seed pack '{pack.Package}': content hash mismatch for {version} — the package does not match " +
                    "tdm.plugins.lock.json. Delete the lockfile entry or run with --update-plugins if intentional.");
            }
            logger?.LogInformation("Seed pack {Pack}: restored {Version} from lockfile", pack.Package, version);
        }
        else
        {
            var range = string.IsNullOrWhiteSpace(pack.Version)
                ? null
                : VersionRange.Parse(pack.Version, allowFloating: true);
            var (_, bestVersion) = await FindBestVersionAsync(pack.Package!, range, repositories, sourceCache, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Seed pack '{pack.Package}' " +
                    (range is null ? "(latest stable)" : $"matching '{pack.Version}'") +
                    $" not found on any configured feed ({string.Join(", ", plugins.Feeds.Select(f => f.Url))}).");
            version = bestVersion.ToNormalizedString();
            nupkgPath = await EnsureCachedAsync(pack.Package!, bestVersion, repositories,
                sourceCache, cachePath, ct).ConfigureAwait(false);
            lockFile.Packs[pack.Package!] = new LockedPackage { Version = version, Sha512 = HashFile(nupkgPath) };
            logger?.LogInformation("Seed pack {Pack}: resolved {Version}; pinned in {Lock}",
                pack.Package, version, lockFile.Path);
        }

        var folder = Path.GetFullPath(Path.Combine("packs", pack.Package!), _baseDirectory);
        ExtractContent(nupkgPath, folder);
        return SeedPackContent.Load(pack.Package!, version, folder);
    }

    /// <summary>Pack payload is content, not lib: entries under content/ or contentFiles/any/any/
    /// map to the pack root; root-level features/, datasets/, tdm.entities.json and
    /// tdm.keys.json are taken as-is.</summary>
    private static void ExtractContent(string nupkgPath, string folder)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);

        using var reader = new PackageArchiveReader(nupkgPath);
        foreach (var entry in reader.GetFiles())
        {
            var relative = MapEntry(entry.Replace('\\', '/'));
            if (relative is null) continue;
            var target = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = reader.GetStream(entry);
            using var destination = File.Create(target);
            source.CopyTo(destination);
        }
    }

    private static string? MapEntry(string entry)
    {
        foreach (var prefix in new[] { "content/", "contentFiles/any/any/" })
            if (entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return entry[prefix.Length..];
        if (entry.StartsWith("features/", StringComparison.OrdinalIgnoreCase) ||
            entry.StartsWith("datasets/", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry, SeedPackContent.FragmentFileName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry, Tdm.Core.Registry.KeyRegistryDocument.FileName, StringComparison.OrdinalIgnoreCase))
        {
            return entry;
        }
        return null;
    }

    private async Task<(SourceRepository Repository, NuGetVersion Version)?> FindBestVersionAsync(string id,
        VersionRange? range, IReadOnlyList<SourceRepository> repositories, SourceCacheContext sourceCache,
        CancellationToken ct)
    {
        foreach (var repository in repositories)
        {
            var byId = await repository.GetResourceAsync<FindPackageByIdResource>(ct).ConfigureAwait(false);
            var versions = (await byId.GetAllVersionsAsync(id, sourceCache, _nugetLog, ct).ConfigureAwait(false)).ToList();
            if (versions.Count == 0) continue;
            var best = range is null
                ? versions.Where(v => !v.IsPrerelease).DefaultIfEmpty(versions.Max()).Max()
                : range.FindBestMatch(versions);
            if (best is not null) return (repository, best);
        }
        return null;
    }

    private async Task<string> EnsureCachedAsync(string id, NuGetVersion version,
        IReadOnlyList<SourceRepository> repositories, SourceCacheContext sourceCache, string cachePath,
        CancellationToken ct)
    {
        var nupkgPath = Path.Combine(cachePath, $"{id.ToLowerInvariant()}.{version.ToNormalizedString()}.nupkg");
        if (File.Exists(nupkgPath)) return nupkgPath;

        foreach (var repository in repositories)
        {
            var byId = await repository.GetResourceAsync<FindPackageByIdResource>(ct).ConfigureAwait(false);
            var versions = await byId.GetAllVersionsAsync(id, sourceCache, _nugetLog, ct).ConfigureAwait(false);
            if (!versions.Contains(version)) continue;

            await using var file = File.Create(nupkgPath);
            if (await byId.CopyNupkgToStreamAsync(id, version, file, sourceCache, _nugetLog, ct).ConfigureAwait(false))
                return nupkgPath;
        }
        File.Delete(nupkgPath);
        throw new InvalidOperationException($"Seed pack '{id}' {version} is not in the cache and no configured feed has it.");
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToBase64String(SHA512.HashData(stream));
    }

    private IReadOnlyList<SourceRepository> BuildRepositories()
    {
        var nugetSettings = Settings.LoadDefaultSettings(_baseDirectory);
        var knownSources = PackageSourceProvider.LoadPackageSources(nugetSettings).ToList();
        return plugins.Feeds.Select(feed =>
        {
            var known = knownSources.FirstOrDefault(s =>
                string.Equals(s.Source, feed.Url, StringComparison.OrdinalIgnoreCase));
            return Repository.Factory.GetCoreV3(known ?? new PackageSource(feed.Url));
        }).ToList();
    }
}
