using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Tdm.Core.Settings;
using Tdm.EfCore.Providers;

namespace Tdm.EfCore;

/// <summary>
/// Instantiates a domain DbContext the way `dotnet ef` design-time tooling does (handoff §3):
/// prefer an <see cref="IDesignTimeDbContextFactory{TContext}"/> found in the plugin assemblies
/// (covers contexts whose constructors need services), else the constructor accepting
/// <c>DbContextOptions&lt;TContext&gt;</c> with provider + connection string from run config.
/// </summary>
public static class DbContextActivator
{
    public static DbContext Activate(Type contextType, DomainSettings domain, IReadOnlyList<Assembly> assemblies)
    {
        var factoryInterface = typeof(IDesignTimeDbContextFactory<>).MakeGenericType(contextType);
        var factoryType = assemblies
            .SelectMany(SafeGetTypes)
            .FirstOrDefault(t => t is { IsClass: true, IsAbstract: false } && factoryInterface.IsAssignableFrom(t));

        if (factoryType is not null)
        {
            var factory = Activator.CreateInstance(factoryType)!;
            var create = factoryInterface.GetMethod("CreateDbContext")!;
            return (DbContext)create.Invoke(factory, [Array.Empty<string>()])!;
        }

        var options = BuildOptions(contextType, domain);
        var ctor = contextType.GetConstructors()
            .FirstOrDefault(c => c.GetParameters() is [{ } p] && p.ParameterType.IsInstanceOfType(options))
            ?? throw new InvalidOperationException(
                $"DbContext '{contextType.FullName}' has no public constructor accepting DbContextOptions<{contextType.Name}> " +
                "and no IDesignTimeDbContextFactory was found in the plugin assemblies. " +
                "Domain data packages must expose one or the other (handoff §3).");

        return (DbContext)ctor.Invoke([options]);
    }

    public static DbContextOptions BuildOptions(Type contextType, DomainSettings domain)
    {
        var builder = (DbContextOptionsBuilder)Activator.CreateInstance(
            typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType))!;
        ApplyProvider(builder, domain);
        return builder.Options;
    }

    public static void ApplyProvider(DbContextOptionsBuilder builder, DomainSettings domain)
    {
        var bootstrap = ProviderRegistry.Resolve(domain.Provider, domain.Name);
        var connectionString = bootstrap.PrepareConnectionString(domain.ResolveConnectionString());
        bootstrap.Configure(builder, connectionString);
    }

    internal static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }
}
