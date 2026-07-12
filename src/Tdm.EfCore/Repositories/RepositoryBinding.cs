using System.Reflection;
using Tdm.Core.Naming;
using Tdm.Core.Settings;

namespace Tdm.EfCore.Repositories;

/// <summary>
/// A discovered repository for one entity (handoff §5.2): the profile-pattern interface
/// (e.g. <c>ICustomerRepository</c>) plus its implementation, with persist methods matched
/// in order: exact well-known <c>IRepository&lt;T&gt;</c>, else duck-typed method-name
/// conventions with a single entity-typed parameter.
/// </summary>
internal sealed class RepositoryBinding
{
    public required Type InterfaceType { get; init; }
    public required Type ImplementationType { get; init; }
    public MethodInfo? AddMethod { get; init; }
    public MethodInfo? UpdateMethod { get; init; }
    public MethodInfo? DeleteMethod { get; init; }

    public static RepositoryBinding? Find(string logicalName, Type entityType,
        ConventionProfile profile, IReadOnlyList<Assembly> assemblies)
    {
        var wantedName = NameMatcher.Expand(profile.RepositoryPattern, logicalName);
        var allTypes = assemblies.SelectMany(DbContextActivator.SafeGetTypes).ToList();

        var interfaceType = allTypes.FirstOrDefault(t =>
            t.IsInterface && string.Equals(t.Name, wantedName, StringComparison.OrdinalIgnoreCase));
        if (interfaceType is null) return null;

        var implementationType = allTypes.FirstOrDefault(t =>
            t is { IsClass: true, IsAbstract: false } && interfaceType.IsAssignableFrom(t));
        if (implementationType is null) return null;

        // All methods reachable through the repository interface, well-known IRepository<T> first.
        var interfaces = new List<Type> { interfaceType };
        interfaces.AddRange(interfaceType.GetInterfaces());
        var wellKnown = interfaces.FirstOrDefault(i =>
            i.IsGenericType && i.Name is "IRepository`1" && i.GetGenericArguments()[0] == entityType);
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
