using System.Reflection;
using Microsoft.Extensions.Logging;
using Tdm.Core.Execution;
using Tdm.Core.Generation;
using Tdm.Core.Naming;
using Tdm.Core.Settings;

namespace Tdm.Api;

/// <summary>One API-seedable entity: descriptor + endpoint templates + generation bindings.</summary>
internal sealed class ApiEntityBinding
{
    public required EntityDescriptor Descriptor { get; init; }
    public required ApiEntityEndpoints Endpoints { get; init; }
    public FakerBinding? Faker { get; init; }
    /// <summary>Key + FK columns + navigations — never auto-faked (same rule as the EF runtime).</summary>
    public required IReadOnlySet<string> AutoFakerSkip { get; init; }
}

/// <summary>
/// Builds an <see cref="ApiDomainRuntime"/> (W4-D6). The <c>api.entities</c> endpoint map is
/// the domain's entity list; CLR types are resolved from the same plugin-loaded assemblies by
/// the profile's <c>entityClassPattern</c> ("{Name}Entity" → CustomerEntity) — the v1
/// engine/runtime seam means the engine does not change at all.
/// </summary>
public static class ApiRuntimeBuilder
{
    private static readonly string[] AllowedMethods = ["GET", "POST", "PUT", "PATCH", "DELETE"];

    public static ApiDomainRuntime Build(DomainSettings domain, TdmSettings root,
        IReadOnlyList<Assembly> assemblies, ILogger? logger = null, string? authToken = null)
    {
        var api = domain.Api ?? throw new InvalidOperationException(
            $"Domain '{domain.Name}': persistence is 'Api' but no \"api\" section is configured.");
        if (string.IsNullOrWhiteSpace(api.BaseUrl))
            throw new InvalidOperationException($"Domain '{domain.Name}': api.baseUrl is required.");
        if (api.Entities.Count == 0)
        {
            throw new InvalidOperationException(
                $"Domain '{domain.Name}': api.entities is empty — list each seedable entity with its endpoint templates.");
        }

        var profile = root.ProfileFor(domain);
        var warnings = new List<string>();
        var policyViolations = new List<string>();

        // Two passes: types first, so navigation/FK detection can see every mapped entity.
        var types = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        foreach (var (logicalName, endpoints) in api.Entities.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            ValidateTemplates(domain.Name, logicalName, endpoints);
            types[logicalName] = ResolveClrType(domain.Name, logicalName, profile, assemblies);
        }

        var bindings = new List<ApiEntityBinding>();
        foreach (var (logicalName, type) in types.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            var entityConfig = root.EntityFor(logicalName);
            var keyProperty = FindProperty(type, "Id") ?? FindProperty(type, $"{logicalName}Id");

            var naturalKeyName = entityConfig.NaturalKey ?? profile.NaturalKeyDefault;
            var naturalKeyProperty = type.GetProperties()
                .FirstOrDefault(p => NameMatcher.Matches(naturalKeyName, p.Name));
            if (naturalKeyProperty is null && entityConfig.NaturalKey is not null)
            {
                throw new InvalidOperationException(
                    $"Domain '{domain.Name}': entities.{logicalName}.naturalKey '{entityConfig.NaturalKey}' " +
                    $"matches no property on {type.Name}.");
            }
            if (naturalKeyProperty is null)
                warnings.Add($"{logicalName}: no natural key property matching '{naturalKeyName}' — key-based steps will not resolve.");

            var navigationNames = type.GetProperties()
                .Where(p => IsNavigation(p.PropertyType, types.Values))
                .Select(p => p.Name)
                .ToList();

            var (idStrategy, keyIsServerAssigned) = ResolveIdStrategy(entityConfig, keyProperty);
            var descriptor = new EntityDescriptor
            {
                LogicalName = logicalName,
                DomainName = domain.Name,
                ClrType = type,
                KeyProperty = keyProperty,
                KeyIsDbGenerated = keyIsServerAssigned,
                NaturalKeyProperty = naturalKeyProperty,
                IdStrategy = idStrategy,
                NavigationNames = navigationNames,
            };

            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (keyProperty is not null) skip.Add(keyProperty.Name);
            skip.UnionWith(navigationNames);
            foreach (var other in types.Keys) skip.Add($"{other}Id"); // FK columns by convention

            bindings.Add(new ApiEntityBinding
            {
                Descriptor = descriptor,
                Endpoints = api.Entities[logicalName],
                Faker = FakerBinding.Find(logicalName, type, profile, assemblies),
                AutoFakerSkip = skip,
            });
        }

        // Transactional is unsupported (no cross-API transaction) and must fail validation
        // with a clear message (W4-D6) — surfaced pre-persistence like ADR-0001 violations.
        if (root.Run.Lifecycle == LifecycleMode.Transactional)
        {
            policyViolations.Add(
                $"Domain '{domain.Name}' uses persistence 'Api' — the Transactional lifecycle is unsupported " +
                "(there is no cross-API transaction). Set run.lifecycle to Persistent or TrackedTeardown.");
        }

        return new ApiDomainRuntime(new ApiRuntimeCore(domain, root, bindings, warnings, policyViolations,
            ValueGeneratorDiscovery.DiscoverFrom(assemblies), new StatisticalGenerator(root),
            CreateHttpClient(api, authToken), logger));
    }

    private static Type ResolveClrType(string domainName, string logicalName, ConventionProfile profile,
        IReadOnlyList<Assembly> assemblies)
    {
        var patternName = NameMatcher.Expand(profile.EntityClassPattern, logicalName);
        var candidates = assemblies.SelectMany(AssemblyScan.SafeGetTypes)
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .ToList();
        return candidates.FirstOrDefault(t => string.Equals(t.Name, patternName, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(t => string.Equals(t.Name, logicalName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Domain '{domainName}': api.entities names '{logicalName}' but no CLR type '{patternName}' " +
                $"(or '{logicalName}') exists in the plugin assemblies.");
    }

    private static PropertyInfo? FindProperty(Type type, string name) =>
        type.GetProperties().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The identity contract still applies (W4-D6): client-settable Guid keys are
    /// deterministic uuid-v5; numeric keys are server-assigned and captured from responses —
    /// exactly like DB-generated ints.</summary>
    private static (IdStrategy Strategy, bool ServerAssigned) ResolveIdStrategy(
        EntitySettings config, PropertyInfo? keyProperty)
    {
        if (config.IdStrategy == IdStrategy.Deterministic) return (IdStrategy.Deterministic, false);
        if (config.IdStrategy == IdStrategy.DbGenerated) return (IdStrategy.DbGenerated, true);
        var keyType = keyProperty is null
            ? null
            : Nullable.GetUnderlyingType(keyProperty.PropertyType) ?? keyProperty.PropertyType;
        return keyType == typeof(Guid)
            ? (IdStrategy.Deterministic, false)
            : (IdStrategy.DbGenerated, true);
    }

    private static bool IsNavigation(Type propertyType, IEnumerable<Type> entityTypes)
    {
        var types = entityTypes as ICollection<Type> ?? entityTypes.ToList();
        if (types.Contains(propertyType)) return true;
        if (propertyType.IsGenericType &&
            propertyType.GetGenericArguments() is [var element] && types.Contains(element)) return true;
        return false;
    }

    private static void ValidateTemplates(string domainName, string logicalName, ApiEntityEndpoints endpoints)
    {
        foreach (var (kind, template) in new[]
                 {
                     ("create", endpoints.Create), ("update", endpoints.Update), ("delete", endpoints.Delete),
                     ("getByKey", endpoints.GetByKey), ("getById", endpoints.GetById),
                 })
        {
            if (template is null) continue;
            var separator = template.IndexOf(' ');
            var method = separator > 0 ? template[..separator].ToUpperInvariant() : "";
            if (separator <= 0 || !AllowedMethods.Contains(method))
            {
                throw new InvalidOperationException(
                    $"Domain '{domainName}': api.entities.{logicalName}.{kind} '{template}' is not " +
                    "\"METHOD /path\" (METHOD one of GET, POST, PUT, PATCH, DELETE).");
            }
        }
    }

    private static HttpClient CreateHttpClient(ApiSettings api, string? authToken)
    {
        var client = new HttpClient
        {
            // Trailing slash so a base *path* ("https://host/v1") keeps its last segment
            // when endpoint templates resolve relative to it.
            BaseAddress = new Uri(api.BaseUrl.EndsWith('/') ? api.BaseUrl : api.BaseUrl + "/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(api.TimeoutSeconds),
        };
        if (authToken is { Length: > 0 })
        {
            var auth = api.Auth!;
            var value = string.IsNullOrEmpty(auth.Scheme) ? authToken : $"{auth.Scheme} {authToken}";
            client.DefaultRequestHeaders.TryAddWithoutValidation(auth.HeaderName, value);
        }
        return client;
    }
}
