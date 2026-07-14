using Tdm.Core.Settings;

namespace Tdm.Plugins;

/// <summary>Result of acquisition: the folder to load, plus the resolved package versions
/// (packageId → version) when a feed-based acquirer ran — recorded into the run manifest.</summary>
public sealed record AcquiredPlugin(string Folder, IReadOnlyDictionary<string, string> Packages)
{
    public static AcquiredPlugin FromFolder(string folder) =>
        new(folder, new Dictionary<string, string>());
}

/// <summary>
/// Materialises a domain's plugin assemblies into a local folder before loading.
/// Implementations: <see cref="FolderPluginAcquirer"/> (default) and
/// <see cref="NuGetPluginAcquirer"/> (W1-D2, feed acquisition with a lockfile).
/// </summary>
public interface IPluginAcquirer
{
    /// <summary>Returns the folder containing the domain's assemblies, acquiring them if needed.</summary>
    Task<AcquiredPlugin> AcquireAsync(DomainSettings domain, CancellationToken ct = default);
}

/// <summary>
/// Folder-based acquisition: assemblies are already on disk — either dropped by a CI restore
/// step or pointed at directly via <c>DomainSettings.PluginPath</c> (defaults to ./plugins/{name}).
/// </summary>
public sealed class FolderPluginAcquirer(string? baseDirectory = null) : IPluginAcquirer
{
    private readonly string _baseDirectory = baseDirectory ?? Directory.GetCurrentDirectory();

    public Task<AcquiredPlugin> AcquireAsync(DomainSettings domain, CancellationToken ct = default)
    {
        var folder = domain.PluginPath is { Length: > 0 } explicitPath
            ? Path.GetFullPath(explicitPath, _baseDirectory)
            : Path.GetFullPath(Path.Combine("plugins", domain.Name), _baseDirectory);

        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException(
                $"Plugin folder for domain '{domain.Name}' not found: {folder}. " +
                "Populate it (e.g. via NuGet restore of the domain data package) or set domains[].pluginPath.");
        }
        return Task.FromResult(AcquiredPlugin.FromFolder(folder));
    }
}
