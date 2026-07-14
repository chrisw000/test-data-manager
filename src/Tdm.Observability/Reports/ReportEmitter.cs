using Tdm.Core.Manifest;

namespace Tdm.Observability.Reports;

/// <summary>Dispatches host `--report &lt;format&gt;=&lt;path&gt;` specs to the emitters (W1-D3).</summary>
public static class ReportEmitter
{
    public static readonly IReadOnlyList<string> Formats = ["sarif", "junit"];

    /// <summary>Parses "sarif=./out/tdm.sarif" — throws a user-actionable error otherwise.</summary>
    public static (string Format, string Path) ParseSpec(string spec)
    {
        var separator = spec.IndexOf('=');
        var format = separator > 0 ? spec[..separator].Trim().ToLowerInvariant() : "";
        var path = separator > 0 ? spec[(separator + 1)..].Trim() : "";
        if (format.Length == 0 || path.Length == 0 || !Formats.Contains(format))
        {
            throw new ArgumentException(
                $"Invalid --report '{spec}'. Expected <format>=<path> with format one of: {string.Join(", ", Formats)}.");
        }
        return (format, path);
    }

    /// <summary>Renders and writes one report; returns the full path written.</summary>
    public static string Write(RunManifest manifest, string format, string path, string? baseDirectory = null)
    {
        var content = format switch
        {
            "sarif" => SarifReport.Render(manifest, baseDirectory),
            "junit" => JUnitReport.Render(manifest),
            _ => throw new ArgumentException($"Unknown report format '{format}'."),
        };
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }
}
