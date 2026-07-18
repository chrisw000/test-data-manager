using System.Reflection;

namespace Tdm.Core.Generation;

/// <summary>Type enumeration tolerant of partially loadable plugin assemblies.</summary>
public static class AssemblyScan
{
    public static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }
}
