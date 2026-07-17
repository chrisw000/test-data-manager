using System.Text.Json;
using Tdm.Core.Manifest;
using Tdm.Core.Settings;

namespace Tdm.Observability.Trends;

/// <summary>
/// The benchmark trend store (W3-D7): manifests pushed under <c>{env}/{run-name}/{timestamp}</c>
/// with a small JSON index at the root — the cheapest durable thing CI can write to, no
/// database or service. TDM ships the filesystem implementation (a local path, network share,
/// or blob storage mounted/synced by CI); Azure Blob / S3 adapters implement this interface
/// host-side, the same posture as ISecretProvider — TDM ships no cloud SDKs.
/// </summary>
public interface ITrendStore
{
    /// <summary>Stores one manifest; returns its store-relative path.</summary>
    Task<string> PublishAsync(RunManifest manifest, string environment, CancellationToken ct = default);

    Task<TrendIndex> ReadIndexAsync(CancellationToken ct = default);

    /// <summary>The last <paramref name="count"/> manifests for (environment, runName),
    /// newest first — the rolling-baseline input for `tdm bench compare` (W3-D8).</summary>
    Task<IReadOnlyList<RunManifest>> ReadRecentAsync(string environment, string runName, int count,
        CancellationToken ct = default);
}

/// <summary>Root-level index.json: one line of metadata per stored manifest, newest last.</summary>
public sealed class TrendIndex
{
    public List<TrendIndexEntry> Entries { get; set; } = [];
}

public sealed class TrendIndexEntry
{
    public string Environment { get; set; } = "";
    public string RunName { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public RunOutcome Outcome { get; set; }
    public double DurationMs { get; set; }
    /// <summary>Store-relative path of the manifest, e.g. "ci/orders-seed/20260717-140302.tdm.json".</summary>
    public string Path { get; set; } = "";
}

public sealed class FileSystemTrendStore(string root) : ITrendStore
{
    private const string IndexFileName = "index.json";
    private readonly string _root = System.IO.Path.GetFullPath(root);

    public async Task<string> PublishAsync(RunManifest manifest, string environment, CancellationToken ct = default)
    {
        var safeRun = Sanitize(manifest.Run.Name);
        var relative = System.IO.Path.Combine(
            Sanitize(environment), safeRun, $"{manifest.Run.StartedUtc:yyyyMMdd-HHmmss}.tdm.json");
        var target = System.IO.Path.Combine(_root, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, ManifestWriter.Serialize(manifest), ct).ConfigureAwait(false);

        var index = await ReadIndexAsync(ct).ConfigureAwait(false);
        var normalized = relative.Replace('\\', '/');
        index.Entries.RemoveAll(e => e.Path == normalized); // republish overwrites, not duplicates
        index.Entries.Add(new TrendIndexEntry
        {
            Environment = environment,
            RunName = manifest.Run.Name,
            StartedUtc = manifest.Run.StartedUtc,
            Outcome = manifest.Run.Outcome,
            DurationMs = manifest.Run.DurationMs,
            Path = normalized,
        });
        index.Entries.Sort((a, b) => a.StartedUtc.CompareTo(b.StartedUtc));
        await File.WriteAllTextAsync(System.IO.Path.Combine(_root, IndexFileName),
            JsonSerializer.Serialize(index, TdmSettings.JsonOptions), ct).ConfigureAwait(false);
        return normalized;
    }

    public async Task<TrendIndex> ReadIndexAsync(CancellationToken ct = default)
    {
        var indexPath = System.IO.Path.Combine(_root, IndexFileName);
        if (!File.Exists(indexPath)) return new TrendIndex();
        await using var stream = File.OpenRead(indexPath);
        return await JsonSerializer.DeserializeAsync<TrendIndex>(stream, TdmSettings.JsonOptions, ct)
            .ConfigureAwait(false) ?? new TrendIndex();
    }

    public async Task<IReadOnlyList<RunManifest>> ReadRecentAsync(string environment, string runName, int count,
        CancellationToken ct = default)
    {
        var index = await ReadIndexAsync(ct).ConfigureAwait(false);
        var manifests = new List<RunManifest>();
        foreach (var entry in index.Entries
                     .Where(e => string.Equals(e.Environment, environment, StringComparison.OrdinalIgnoreCase) &&
                                 string.Equals(e.RunName, runName, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(e => e.StartedUtc)
                     .Take(count))
        {
            var path = System.IO.Path.Combine(_root, entry.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (File.Exists(path)) manifests.Add(ManifestWriter.Read(path));
        }
        return manifests;
    }

    private static string Sanitize(string name)
    {
        var safe = string.Join("-", name.Split(System.IO.Path.GetInvalidFileNameChars(),
            StringSplitOptions.RemoveEmptyEntries));
        return safe.Length == 0 ? "unnamed" : safe;
    }
}
