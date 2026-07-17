using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tdm.EfCore.Bulk;

namespace Tdm.EfCore.Providers;

/// <summary>
/// Process-wide provider bootstrap registry (W3-D5). Sqlite and SqlServer are pre-registered;
/// provider plugin packages contribute theirs via <see cref="DiscoverFrom"/> (the host scans
/// each domain's plugin assemblies) or an explicit <see cref="Register"/>. Registration is
/// last-wins by name, so a plugin-shipped bootstrap may deliberately supersede an in-box one.
/// </summary>
public static class ProviderRegistry
{
    private static readonly ConcurrentDictionary<string, IProviderBootstrap> ByName =
        new(StringComparer.OrdinalIgnoreCase);

    static ProviderRegistry()
    {
        Register(new SqliteProviderBootstrap());
        Register(new SqlServerProviderBootstrap());
    }

    public static void Register(IProviderBootstrap bootstrap) => ByName[bootstrap.Name] = bootstrap;

    public static IReadOnlyCollection<string> Names =>
        ByName.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();

    public static bool TryResolve(string providerName, out IProviderBootstrap bootstrap) =>
        ByName.TryGetValue(providerName, out bootstrap!);

    public static IProviderBootstrap Resolve(string providerName, string domainName) =>
        TryResolve(providerName, out var bootstrap)
            ? bootstrap
            : throw new InvalidOperationException(
                $"Domain '{domainName}': unknown provider '{providerName}'. Registered providers: " +
                $"{string.Join(", ", Names)}. Additional providers ship as plugin packages exposing " +
                "an IProviderBootstrap (e.g. Tdm.Providers.PostgreSql) — add the package to the " +
                "domain's plugin dependencies.");

    /// <summary>The bulk inserter for a live context, or null when its provider has none
    /// registered — the caller falls back to the portable EF path.</summary>
    public static IBulkInserter? InserterFor(DbContext context)
    {
        var efProviderName = context.Database.ProviderName;
        if (efProviderName is null) return null;
        return ByName.Values
            .FirstOrDefault(b => string.Equals(b.EfProviderName, efProviderName, StringComparison.Ordinal))
            ?.BulkInserter;
    }

    /// <summary>Registers every concrete <see cref="IProviderBootstrap"/> with a parameterless
    /// constructor found in <paramref name="assemblies"/>; returns how many were registered.</summary>
    public static int DiscoverFrom(IEnumerable<Assembly> assemblies, ILogger? logger = null)
    {
        var registered = 0;
        foreach (var type in assemblies.SelectMany(DbContextActivator.SafeGetTypes))
        {
            if (type is not { IsClass: true, IsAbstract: false } ||
                !typeof(IProviderBootstrap).IsAssignableFrom(type) ||
                type.GetConstructor(Type.EmptyTypes) is null) continue;

            var bootstrap = (IProviderBootstrap)Activator.CreateInstance(type)!;
            Register(bootstrap);
            registered++;
            logger?.LogInformation("Registered database provider '{Provider}' from {Assembly}",
                bootstrap.Name, type.Assembly.GetName().Name);
        }
        return registered;
    }
}
