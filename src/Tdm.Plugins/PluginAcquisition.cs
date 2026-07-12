using Tdm.Core.Settings;

namespace Tdm.Plugins;

/// <summary>
/// Materialises a domain's plugin assemblies into a local folder before loading.
/// Implementations: <see cref="FolderPluginAcquirer"/> (shipped) and a NuGet-feed acquirer
/// (extension point — see remarks).
/// </summary>
/// <remarks>
/// The intended production extension is a NuGet.Protocol-based acquirer that restores the
/// domain's data package (<c>DomainSettings.Package</c>, e.g. "Acme.Orders.Data.Persistence")
/// plus non-shared transitive dependencies from the internal feed into
/// <c>./plugins/{domain}</c> — exactly what already happens for the production API build.
/// Implement this interface and pass it to <c>PluginLoader</c>; nothing else changes.
/// </remarks>
public interface IPluginAcquirer
{
    /// <summary>Returns the folder containing the domain's assemblies, acquiring them if needed.</summary>
    Task<string> AcquireAsync(DomainSettings domain, CancellationToken ct = default);
}

/// <summary>
/// Folder-based acquisition: assemblies are already on disk — either dropped by a CI restore
/// step or pointed at directly via <c>DomainSettings.PluginPath</c> (defaults to ./plugins/{name}).
/// </summary>
public sealed class FolderPluginAcquirer(string? baseDirectory = null) : IPluginAcquirer
{
    private readonly string _baseDirectory = baseDirectory ?? Directory.GetCurrentDirectory();

    public Task<string> AcquireAsync(DomainSettings domain, CancellationToken ct = default)
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
        return Task.FromResult(folder);
    }
}
