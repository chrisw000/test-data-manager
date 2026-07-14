using System.Reflection;
using Tdm.Core.Naming;
using Tdm.Core.Settings;

namespace Tdm.EfCore.Repositories;

/// <summary>A discovered read-side repository — reporting only; TDM's verification reads go through the DbContext (ADR-0001).</summary>
internal sealed record ReadRepositoryInfo(Type InterfaceType, Type ImplementationType);

/// <summary>
/// A discovered write-side repository for one entity (handoff §5.2, ADR-0001): the first
/// interface matching the profile's ordered probe patterns (e.g. <c>ICustomerWriteRepository</c>,
/// falling back to <c>ICustomerRepository</c>) plus its implementation, with persist methods
/// matched in order: exact well-known generic (<c>IRepository&lt;T&gt;</c>,
/// <c>IWriteRepository&lt;T&gt;</c>, <c>IRepositoryWrite&lt;T&gt;</c>), else duck-typed
/// method-name conventions with a single entity-typed parameter.
/// </summary>
internal sealed class RepositoryBinding
{
    private static readonly string[] WellKnownGenericNames = ["IRepository`1", "IWriteRepository`1", "IRepositoryWrite`1"];

    public required Type InterfaceType { get; init; }
    public required Type ImplementationType { get; init; }
    public MethodInfo? AddMethod { get; init; }
    public MethodInfo? UpdateMethod { get; init; }
    public MethodInfo? DeleteMethod { get; init; }

    public static RepositoryBinding? Find(string logicalName, Type entityType,
        ConventionProfile profile, EntitySettings entitySettings, IReadOnlyList<Assembly> assemblies)
    {
        var allTypes = assemblies.SelectMany(DbContextActivator.SafeGetTypes).ToList();

        // An explicit per-entity interface name wins over the profile's probe patterns.
        IEnumerable<string> wantedNames = entitySettings.WriteRepository is { Length: > 0 } explicitName
            ? [explicitName]
            : profile.WriteRepositoryPatterns.Select(p => NameMatcher.Expand(p, logicalName));

        var (interfaceType, implementationType) = Probe(wantedNames, allTypes);
        if (interfaceType is null || implementationType is null) return null;

        // All methods reachable through the repository interface, well-known generics first.
        var interfaces = new List<Type> { interfaceType };
        interfaces.AddRange(interfaceType.GetInterfaces());
        var wellKnown = interfaces.FirstOrDefault(i =>
            i.IsGenericType && WellKnownGenericNames.Contains(i.Name) && i.GetGenericArguments()[0] == entityType);
        var searchOrder = wellKnown is null ? interfaces : [wellKnown, .. interfaces.Where(i => i != wellKnown)];
        var methods = searchOrder.SelectMany(i => i.GetMethods()).ToList();

        return new RepositoryBinding
        {
            InterfaceType = interfaceType,
            ImplementationType = implementationType,
            AddMethod = Match(methods, profile.AddMethodNames, logicalName, entityType),
            UpdateMethod = Match(methods, profile.UpdateMethodNames, logicalName, entityType),
            DeleteMethod = Match(methods, profile.DeleteMethodNames, logicalName, entityType),
        };
    }

    public static ReadRepositoryInfo? FindRead(string logicalName,
        ConventionProfile profile, IReadOnlyList<Assembly> assemblies)
    {
        var allTypes = assemblies.SelectMany(DbContextActivator.SafeGetTypes).ToList();
        var wantedNames = profile.ReadRepositoryPatterns.Select(p => NameMatcher.Expand(p, logicalName));
        var (interfaceType, implementationType) = Probe(wantedNames, allTypes);
        return interfaceType is null || implementationType is null
            ? null
            : new ReadRepositoryInfo(interfaceType, implementationType);
    }

    /// <summary>First name whose interface AND a concrete implementation both exist wins.</summary>
    private static (Type? Interface, Type? Implementation) Probe(IEnumerable<string> wantedNames, List<Type> allTypes)
    {
        foreach (var wantedName in wantedNames)
        {
            var interfaceType = allTypes.FirstOrDefault(t =>
                t.IsInterface && string.Equals(t.Name, wantedName, StringComparison.OrdinalIgnoreCase));
            if (interfaceType is null) continue;

            var implementationType = allTypes.FirstOrDefault(t =>
                t is { IsClass: true, IsAbstract: false } && interfaceType.IsAssignableFrom(t));
            if (implementationType is not null) return (interfaceType, implementationType);
        }
        return (null, null);
    }

    private static MethodInfo? Match(List<MethodInfo> methods, List<string> namePatterns,
        string logicalName, Type entityType)
    {
        foreach (var pattern in namePatterns)
        {
            var name = NameMatcher.Expand(pattern, logicalName);
            var method = methods.FirstOrDefault(m =>
                string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase) &&
                m.GetParameters() is [{ } p] && p.ParameterType.IsAssignableFrom(entityType));
            if (method is not null) return method;
        }
        return null;
    }

    public static async Task InvokeAsync(MethodInfo method, object repository, object entity)
    {
        var result = method.Invoke(repository, [entity]);
        if (result is Task task) await task.ConfigureAwait(false);
        else if (result is ValueTask valueTask) await valueTask.ConfigureAwait(false);
    }
}
