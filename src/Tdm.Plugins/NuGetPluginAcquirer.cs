using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Tdm.Core.Settings;

namespace Tdm.Plugins;

/// <summary>
/// NuGet feed acquisition (W1-D2): resolves <c>domains[].package</c> (+ optional
/// <c>packageVersion</c>, floating allowed) from the configured feeds, downloads the package
/// and its transitive dependencies — excluding shared assemblies via the same prefix list as
/// <see cref="PluginLoadContext.IsShared"/> — and extracts <c>lib/{bestTfm}</c> into the
/// domain's plugin folder. Resolved versions and nupkg hashes are pinned in
/// tdm.plugins.lock.json; pass <see cref="UpdatePlugins"/> to re-resolve.
/// Feed auth uses the standard NuGet credential chain (nuget.config) — no custom secrets.
/// </summary>
public sealed class NuGetPluginAcquirer(PluginsSettings plugins, string? baseDirectory = null, ILogger? logger = null)
    : IPluginAcquirer
{
    private static readonly NuGetFramework HostFramework = NuGetFramework.Parse("net10.0");

    private readonly string _baseDirectory = baseDirectory ?? Directory.GetCurrentDirectory();
    private readonly NuGet.Common.ILogger _nugetLog = NuGet.Common.NullLogger.Instance;

    /// <summary>When true, ignore the lockfile and re-resolve (host --update-plugins).</summary>
    public bool UpdatePlugins { get; init; }

    public async Task<AcquiredPlugin> AcquireAsync(DomainSettings domain, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(domain.Package))
        {
            throw new InvalidOperationException(
                $"Domain '{domain.Name}': plugins.acquisition is NuGet but domains[].package is not set.");
        }
        if (plugins.Feeds.Count == 0)
            throw new InvalidOperationException("plugins.acquisition is NuGet but plugins.feeds is empty.");

        var cachePath = plugins.ResolveCachePath();
        Directory.CreateDirectory(cachePath);

        var lockFile = PluginLockFile.Load(_baseDirectory);
        var locked = UpdatePlugins ? null : lockFile.For(domain.Name);

        using var sourceCache = new SourceCacheContext();
        var repositories = BuildRepositories();

        Dictionary<string, (string Version, string Sha512, string NupkgPath)> resolved;
        if (locked is not null)
        {
            resolved = await RestoreLockedAsync(domain, locked, repositories, sourceCache, cachePath, ct).ConfigureAwait(false);
            logger?.LogInformation("Domain {Domain}: restored {Count} package(s) from lockfile", domain.Name, resolved.Count);
        }
        else
        {
            resolved = await ResolveAsync(domain, repositories, sourceCache, cachePath, ct).ConfigureAwait(false);
            lockFile.Domains[domain.Name] = resolved.ToDictionary(
                kv => kv.Key,
                kv => new LockedPackage { Version = kv.Value.Version, Sha512 = kv.Value.Sha512 },
                StringComparer.OrdinalIgnoreCase);
            lockFile.Save();
            logger?.LogInformation("Domain {Domain}: resolved {Count} package(s); lockfile written to {Path}",
                domain.Name, resolved.Count, lockFile.Path);
        }

        var folder = domain.PluginPath is { Length: > 0 } explicitPath
            ? Path.GetFullPath(explicitPath, _baseDirectory)
            : Path.GetFullPath(Path.Combine("plugins", domain.Name), _baseDirectory);
        ExtractAll(resolved.Values.Select(v => v.NupkgPath), folder);

        return new AcquiredPlugin(folder,
            resolved.ToDictionary(kv => kv.Key, kv => kv.Value.Version, StringComparer.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- Resolution

    private async Task<Dictionary<string, (string, string, string)>> ResolveAsync(DomainSettings domain,
        IReadOnlyList<SourceRepository> repositories, SourceCacheContext sourceCache, string cachePath, CancellationToken ct)
    {
        var resolved = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Id, VersionRange? Range)>();
        queue.Enqueue((domain.Package!, ParseRange(domain.PackageVersion)));

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (id, range) = queue.Dequeue();
            if (resolved.ContainsKey(id)) continue;

            var (repository, version) = await FindBestVersionAsync(id, range, repositories, sourceCache, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Domain '{domain.Name}': package '{id}' " +
                    (range is null ? "(latest stable)" : $"matching '{range.OriginalString ?? range.ToString()}'") +
                    $" not found on any configured feed ({string.Join(", ", plugins.Feeds.Select(f => f.Url))}).");

            var nupkgPath = await DownloadAsync(repository, id, version, sourceCache, cachePath, ct).ConfigureAwait(false);
            resolved[id] = (version.ToNormalizedString(), HashFile(nupkgPath), nupkgPath);

            // Transitive dependencies for the nearest framework, minus host-shared assemblies —
            // the exclusion list is PluginLoadContext.IsShared, the single source of truth.
            using var reader = new PackageArchiveReader(nupkgPath);
            var dependencyGroups = (await reader.GetPackageDependenciesAsync(ct).ConfigureAwait(false)).ToList();
            var nearest = new FrameworkReducer().GetNearest(HostFramework, dependencyGroups.Select(g => g.TargetFramework));
            var dependencies = dependencyGroups.FirstOrDefault(g => Equals(g.TargetFramework, nearest))?.Packages ?? [];
            foreach (var dependency in dependencies)
            {
                if (PluginLoadContext.IsShared(dependency.Id) || resolved.ContainsKey(dependency.Id)) continue;
                queue.Enqueue((dependency.Id, dependency.VersionRange));
            }
        }
        return resolved;
    }

    private async Task<Dictionary<string, (string, string, string)>> RestoreLockedAsync(DomainSettings domain,
        Dictionary<string, LockedPackage> locked, IReadOnlyList<SourceRepository> repositories,
        SourceCacheContext sourceCache, string cachePath, CancellationToken ct)
    {
        var resolved = new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, entry) in locked)
        {
            ct.ThrowIfCancellationRequested();
            var version = NuGetVersion.Parse(entry.Version);
            var nupkgPath = CachedNupkgPath(cachePath, id, version);
            if (!File.Exists(nupkgPath))
            {
                var repository = await FirstRepositoryWithAsync(id, version, repositories, sourceCache, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"Domain '{domain.Name}': locked package {id} {entry.Version} is not in the cache and no configured feed has it.");
                nupkgPath = await DownloadAsync(repository, id, version, sourceCache, cachePath, ct).ConfigureAwait(false);
            }

            var hash = HashFile(nupkgPath);
            if (!string.Equals(hash, entry.Sha512, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Domain '{domain.Name}': content hash mismatch for {id} {entry.Version} — the package on disk/feed " +
                    $"does not match tdm.plugins.lock.json. Delete the lockfile entry or run with --update-plugins if intentional.");
            }
            resolved[id] = (entry.Version, hash, nupkgPath);
        }
        return resolved;
    }

    private static VersionRange? ParseRange(string? packageVersion) =>
        string.IsNullOrWhiteSpace(packageVersion) ? null : VersionRange.Parse(packageVersion, allowFloating: true);

    private async Task<(SourceRepository, NuGetVersion)?> FindBestVersionAsync(string id, VersionRange? range,
        IReadOnlyList<SourceRepository> repositories, SourceCacheContext sourceCache, CancellationToken ct)
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

    private async Task<SourceRepository?> FirstRepositoryWithAsync(string id, NuGetVersion version,
        IReadOnlyList<SourceRepository> repositories, SourceCacheContext sourceCache, CancellationToken ct)
    {
        foreach (var repository in repositories)
        {
            var byId = await repository.GetResourceAsync<FindPackageByIdResource>(ct).ConfigureAwait(false);
            var versions = await byId.GetAllVersionsAsync(id, sourceCache, _nugetLog, ct).ConfigureAwait(false);
            if (versions.Contains(version)) return repository;
        }
        return null;
    }

    private async Task<string> DownloadAsync(SourceRepository repository, string id, NuGetVersion version,
        SourceCacheContext sourceCache, string cachePath, CancellationToken ct)
    {
        var nupkgPath = CachedNupkgPath(cachePath, id, version);
        if (File.Exists(nupkgPath)) return nupkgPath;

        var byId = await repository.GetResourceAsync<FindPackageByIdResource>(ct).ConfigureAwait(false);
        await using var file = File.Create(nupkgPath);
        var copied = await byId.CopyNupkgToStreamAsync(id, version, file, sourceCache, _nugetLog, ct).ConfigureAwait(false);
        if (!copied)
        {
            file.Close();
            File.Delete(nupkgPath);
            throw new InvalidOperationException($"Failed to download {id} {version} from {repository.PackageSource.Source}.");
        }
        return nupkgPath;
    }

    private static string CachedNupkgPath(string cachePath, string id, NuGetVersion version) =>
        Path.Combine(cachePath, $"{id.ToLowerInvariant()}.{version.ToNormalizedString()}.nupkg");

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToBase64String(SHA512.HashData(stream));
    }

    // ---------------------------------------------------------------- Extraction

    private static void ExtractAll(IEnumerable<string> nupkgPaths, string folder)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);

        foreach (var nupkgPath in nupkgPaths)
        {
            using var reader = new PackageArchiveReader(nupkgPath);
            var libGroups = reader.GetLibItems().ToList();
            var nearest = new FrameworkReducer().GetNearest(HostFramework, libGroups.Select(g => g.TargetFramework));
            if (nearest is null) continue; // meta/contentless package

            foreach (var item in libGroups.First(g => Equals(g.TargetFramework, nearest)).Items)
            {
                if (!item.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                    !item.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.Combine(folder, Path.GetFileName(item));
                using var source = reader.GetStream(item);
                using var destination = File.Create(target);
                source.CopyTo(destination);
            }
        }
    }

    // ---------------------------------------------------------------- Feeds

    /// <summary>Feed credentials come from the standard nuget.config chain when a configured
    /// feed URL matches a source there; anonymous otherwise (W1-D2: no custom secret handling).</summary>
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
