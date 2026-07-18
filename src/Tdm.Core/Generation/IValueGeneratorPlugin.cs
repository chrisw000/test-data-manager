using System.Reflection;
using Bogus;

namespace Tdm.Core.Generation;

/// <summary>Everything a generator plugin sees about the property being generated.</summary>
public sealed record ValueGenerationContext(string Domain, string Entity, PropertyInfo Property, string? Locale);

/// <summary>
/// Generator plugin API (W4-D4): teams extend the auto-faker heuristic table with code, not
/// forks. Implementations are discovered from the domain's plugin assemblies (same rules as
/// provider bootstraps: concrete class, parameterless constructor) and consulted per
/// property *before* the built-in heuristics. Draw exclusively from the supplied
/// per-scenario <see cref="Randomizer"/> — that is what keeps same-seed runs identical (W4-D5).
/// </summary>
public interface IValueGeneratorPlugin
{
    /// <summary>Stable name, recorded in the manifest's fakerSource / attestation.</summary>
    string Name { get; }

    /// <summary>Whether this plugin supplies values for the given property.</summary>
    bool Matches(ValueGenerationContext context);

    /// <summary>The value to assign; null means "no value after all" and falls through to
    /// the next plugin / the built-in heuristics.</summary>
    object? Generate(ValueGenerationContext context, Randomizer random);
}

public static class ValueGeneratorDiscovery
{
    /// <summary>All concrete <see cref="IValueGeneratorPlugin"/> types with a parameterless
    /// constructor in the assemblies, ordinal-sorted by <see cref="IValueGeneratorPlugin.Name"/>
    /// so the consult order (and therefore the draw sequence) is deterministic.</summary>
    public static IReadOnlyList<IValueGeneratorPlugin> DiscoverFrom(IEnumerable<Assembly> assemblies)
    {
        var plugins = new List<IValueGeneratorPlugin>();
        foreach (var type in assemblies.SelectMany(SafeGetTypes))
        {
            if (type is not { IsClass: true, IsAbstract: false } ||
                !typeof(IValueGeneratorPlugin).IsAssignableFrom(type) ||
                type.GetConstructor(Type.EmptyTypes) is null) continue;
            plugins.Add((IValueGeneratorPlugin)Activator.CreateInstance(type)!);
        }
        return [.. plugins.OrderBy(p => p.Name, StringComparer.Ordinal)];
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }
}
