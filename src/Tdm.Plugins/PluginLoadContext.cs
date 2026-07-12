using System.Reflection;
using System.Runtime.Loader;

namespace Tdm.Plugins;

/// <summary>
/// Isolated, collectible <see cref="AssemblyLoadContext"/> — one per domain (handoff §3) so
/// transitive dependency conflicts between domains cannot clash. Framework, EF, Tdm and Bogus
/// assemblies are deliberately shared with the default context: the plugin's DbContext must be
/// the host's EF type identity or nothing would unify.
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly string[] SharedPrefixes =
    [
        "System",
        "netstandard",
        "mscorlib",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.Extensions",
        "Microsoft.Data",
        "SQLitePCLRaw",
        "Bogus",
        "Tdm",
    ];

    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string name, string pluginDirectory)
        : base(name, isCollectible: true)
    {
        // Anchor resolution on any assembly in the folder so deps.json-driven probing works.
        var anchor = Directory.EnumerateFiles(pluginDirectory, "*.dll").FirstOrDefault()
            ?? throw new InvalidOperationException($"Plugin directory '{pluginDirectory}' contains no assemblies.");
        _resolver = new AssemblyDependencyResolver(anchor);
        PluginDirectory = pluginDirectory;
    }

    public string PluginDirectory { get; }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsShared(assemblyName.Name)) return null; // defer to the default context

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path is not null) return LoadFromAssemblyPath(path);

        var candidate = Path.Combine(PluginDirectory, assemblyName.Name + ".dll");
        return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : nint.Zero;
    }

    public static bool IsShared(string? assemblySimpleName) =>
        assemblySimpleName is not null &&
        SharedPrefixes.Any(p => assemblySimpleName.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                                assemblySimpleName.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase));
}
