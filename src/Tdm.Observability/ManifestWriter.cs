using System.Text.Json;
using Tdm.Core.Manifest;
using Tdm.Core.Settings;

namespace Tdm.Observability;

/// <summary>Writes the JSON run report + seeding manifest to ./output/{run}-{timestamp}.tdm.json.</summary>
public static class ManifestWriter
{
    public static string Write(RunManifest manifest, string outputPath)
    {
        Directory.CreateDirectory(outputPath);
        var safeName = string.Join("-", manifest.Run.Name.Split(Path.GetInvalidFileNameChars(),
            StringSplitOptions.RemoveEmptyEntries));
        var file = Path.Combine(outputPath,
            $"{safeName}-{manifest.Run.StartedUtc:yyyyMMdd-HHmmss}.tdm.json");
        File.WriteAllText(file, Serialize(manifest));
        return Path.GetFullPath(file);
    }

    public static string Serialize(RunManifest manifest) =>
        JsonSerializer.Serialize(manifest, TdmSettings.JsonOptions);

    public static RunManifest Read(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<RunManifest>(stream, TdmSettings.JsonOptions)
               ?? throw new InvalidOperationException($"Manifest '{path}' deserialised to null.");
    }
}
