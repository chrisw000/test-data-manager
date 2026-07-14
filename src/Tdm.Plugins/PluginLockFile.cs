using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tdm.Plugins;

/// <summary>
/// tdm.plugins.lock.json (W1-D2): exact resolved package versions + content hashes per
/// domain, written on first resolve and honoured thereafter (refresh with --update-plugins).
/// Two runs with an unchanged lockfile load byte-identical plugin packages.
/// </summary>
public sealed class PluginLockFile
{
    public const string FileName = "tdm.plugins.lock.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public int Version { get; set; } = 1;

    /// <summary>domain → packageId → locked entry.</summary>
    public Dictionary<string, Dictionary<string, LockedPackage>> Domains { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public string Path { get; private set; } = FileName;

    public static PluginLockFile Load(string directory)
    {
        var path = System.IO.Path.Combine(directory, FileName);
        var lockFile = File.Exists(path)
            ? JsonSerializer.Deserialize<PluginLockFile>(File.ReadAllText(path), JsonOptions) ?? new PluginLockFile()
            : new PluginLockFile();
        lockFile.Path = path;
        return lockFile;
    }

    public void Save() => File.WriteAllText(Path, JsonSerializer.Serialize(this, JsonOptions));

    public Dictionary<string, LockedPackage>? For(string domain) =>
        Domains.TryGetValue(domain, out var packages) && packages.Count > 0 ? packages : null;
}

public sealed class LockedPackage
{
    public string Version { get; set; } = "";
    /// <summary>Base64 SHA-512 of the .nupkg — verified on every restore from cache or feed.</summary>
    public string Sha512 { get; set; } = "";
}
