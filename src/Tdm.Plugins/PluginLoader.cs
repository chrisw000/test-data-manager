using System.Reflection;
using Microsoft.Extensions.Logging;
using Tdm.Core.Settings;

namespace Tdm.Plugins;

public sealed record LoadedPlugin(
    string DomainName,
    PluginLoadContext LoadContext,
    IReadOnlyList<Assembly> Assemblies);

/// <summary>
/// Loads a domain's plugin folder into an isolated <see cref="PluginLoadContext"/> and
/// validates EF version alignment between host and plugin, failing fast with an error
/// naming both versions (handoff §3 risk table).
/// </summary>
public sealed class PluginLoader(IPluginAcquirer acquirer, ILogger? logger = null)
{
    public async Task<LoadedPlugin> LoadAsync(DomainSettings domain, CancellationToken ct = default)
    {
        var folder = await acquirer.AcquireAsync(domain, ct).ConfigureAwait(false);
        var context = new PluginLoadContext($"tdm-plugin-{domain.Name}", folder);

        var assemblies = new List<Assembly>();
        foreach (var dll in Directory.EnumerateFiles(folder, "*.dll").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var simpleName = Path.GetFileNameWithoutExtension(dll);
            if (PluginLoadContext.IsShared(simpleName)) continue; // host provides these
            try
            {
                assemblies.Add(context.LoadFromAssemblyPath(Path.GetFullPath(dll)));
            }
            catch (BadImageFormatException)
            {
                // Native or non-.NET file in the folder — skip.
            }
        }

        if (assemblies.Count == 0)
            throw new InvalidOperationException($"Domain '{domain.Name}': no loadable assemblies in {folder}.");

        ValidateEfVersion(domain, assemblies);
        logger?.LogInformation("Domain {Domain}: loaded {Count} plugin assembl{Suffix} from {Folder}",
            domain.Name, assemblies.Count, assemblies.Count == 1 ? "y" : "ies", folder);
        return new LoadedPlugin(domain.Name, context, assemblies);
    }

    private static void ValidateEfVersion(DomainSettings domain, IReadOnlyList<Assembly> assemblies)
    {
        var hostEf = typeof(Microsoft.EntityFrameworkCore.DbContext).Assembly.GetName().Version;
        foreach (var assembly in assemblies)
        {
            var pluginEf = assembly.GetReferencedAssemblies()
                .FirstOrDefault(a => string.Equals(a.Name, "Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase));
            if (pluginEf?.Version is null || hostEf is null) continue;
            if (pluginEf.Version.Major != hostEf.Major)
            {
                throw new InvalidOperationException(
                    $"EF version skew for domain '{domain.Name}': plugin assembly '{assembly.GetName().Name}' was built " +
                    $"against Microsoft.EntityFrameworkCore {pluginEf.Version} but the TDM host ships {hostEf}. " +
                    "Rebuild the domain data package against the org EF baseline, or align the TDM host version. " +
                    "Compatibility matrix: https://github.com/chrisw000/test-data-manager/blob/main/docs/compatibility.md");
            }
        }
    }
}
