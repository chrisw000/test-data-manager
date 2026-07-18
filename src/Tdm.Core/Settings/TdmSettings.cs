using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tdm.Core.Settings;

public enum FailurePolicy { BestEffort, FailObject, FailRun }

public enum LifecycleMode { Persistent, Transactional, TrackedTeardown }

/// <summary>Api (W4-D6): persistence routes through the domain's public HTTP API instead of
/// a database — for domains that forbid direct writes. See <see cref="ApiSettings"/>.</summary>
public enum PersistenceMode { RepositoryFirst, DbContextOnly, RepositoryOnly, Api }

public enum ExternalReferenceMode { Synthesize, Verify, Trust }

/// <summary>Auto = Deterministic where the key is client-settable, detected from EF metadata.</summary>
public enum IdStrategy { Auto, Deterministic, DbGenerated }

public enum ExternalBehavior { FkOnly, Projection }

public sealed class TdmSettings
{
    public RunSettings Run { get; set; } = new();
    public PluginsSettings Plugins { get; set; } = new();
    public RegistrySettings Registry { get; set; } = new();
    public SecretsSettings Secrets { get; set; } = new();
    public List<DomainSettings> Domains { get; set; } = [];
    public Dictionary<string, ConventionProfile> ConventionProfiles { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, EntitySettings> Entities { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Named correlated-tuple datasets (W4-D4): CSV files whose rows are sampled
    /// whole, so city↔postcode↔country style fields stay consistent per entity.</summary>
    public Dictionary<string, DatasetSettings> Datasets { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Shared seed packs (W4-D7): versioned packages of feature files + entity
    /// config fragments + key-registry entries, executed before local features. Kills the
    /// copy-paste economy — "EU reference customers v2" is a dependency, not a snippet.</summary>
    public List<SeedPackSettings> SeedPacks { get; set; } = [];
    /// <summary>Directory of the loaded settings file — dataset paths resolve against it.
    /// Set by <see cref="Load"/>; never serialized.</summary>
    [JsonIgnore]
    public string? BaseDirectory { get; set; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    public static TdmSettings Load(string path)
    {
        using var stream = File.OpenRead(path);
        var settings = JsonSerializer.Deserialize<TdmSettings>(stream, JsonOptions)
                       ?? throw new InvalidOperationException($"Settings file '{path}' deserialised to null.");
        settings.BaseDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
        settings.ApplyDefaults();
        return settings;
    }

    public void ApplyDefaults()
    {
        foreach (var (name, profile) in ConventionProfile.BuiltIn)
            ConventionProfiles.TryAdd(name, profile);
    }

    public ConventionProfile ProfileFor(DomainSettings domain) =>
        ConventionProfiles.TryGetValue(domain.ConventionProfile, out var profile)
            ? profile
            : throw new InvalidOperationException(
                $"Domain '{domain.Name}' references convention profile '{domain.ConventionProfile}' which is not configured. " +
                $"Known profiles: {string.Join(", ", ConventionProfiles.Keys)}.");

    public EntitySettings EntityFor(string logicalName) =>
        Entities.TryGetValue(logicalName, out var e) ? e : EntitySettings.Default;

    public DomainSettings? FindDomain(string name) =>
        Domains.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Bulk-create persistence path (W3-D3). Provider: a provider-native bulk inserter
/// (SqlBulkCopy, multi-row INSERT) when the entity qualifies, else the EF path. EfCore:
/// always the portable chunked AddRange+SaveChanges path (v1 behaviour).</summary>
public enum BulkStrategy { Provider, EfCore }

/// <summary>Manifest detail for count-bulk creates (W3-D4). All: every row's full values
/// (v1 — unusable at millions of rows). Sample: first/last N rows' full values plus a
/// count and value-hash of the rest. None: count and value-hash only.</summary>
public enum BulkManifestMode { All, Sample, None }

public sealed class RunSettings
{
    public string Name { get; set; } = "tdm-run";
    public FailurePolicy FailurePolicy { get; set; } = FailurePolicy.BestEffort;
    public LifecycleMode Lifecycle { get; set; } = LifecycleMode.TrackedTeardown;
    public int DefaultSeed { get; set; } = 1;
    public List<string> FeaturePaths { get; set; } = [];
    public bool Benchmark { get; set; }
    /// <summary>Bulk creates are generated and persisted in bounded batches of this size —
    /// memory stays O(chunk) however large the count (W3-D3). `tdm bench tune` measures the
    /// best value for a target database and writes it here.</summary>
    public int BulkChunkSize { get; set; } = 500;
    public BulkStrategy BulkStrategy { get; set; } = BulkStrategy.Provider;
    /// <summary>How much of a count-bulk create the manifest records (W3-D4).</summary>
    public BulkManifestMode ManifestBulkValues { get; set; } = BulkManifestMode.Sample;
    /// <summary>Rows kept with full values at each end of a bulk create in Sample mode.</summary>
    public int ManifestBulkSampleRows { get; set; } = 5;
    /// <summary>Scenarios run concurrently up to this limit (W3-D1); steps within a scenario stay
    /// sequential. 1 (default) preserves strict serial execution. Any domain's
    /// <see cref="DomainSettings.MaxParallelScenarios"/> caps this further.</summary>
    public int MaxParallelScenarios { get; set; } = 1;
    public string OutputPath { get; set; } = "./output";
    /// <summary>Optional detached-signature manifest signing (W2-D2). A SHA-256 checksum is
    /// always written next to the manifest regardless of whether this is configured.</summary>
    public SigningSettings? Signing { get; set; }
}

/// <summary>
/// Secret resolution (W2-D8): inline (dev only, when configured) → environment → optionally
/// a named cloud provider. Used for connection strings, the manifest-signing certificate
/// password, and registry auth. TDM never stores secrets and ships no cloud SDKs — cloud
/// adapters implement Tdm.Core.Secrets.ISecretProvider (see docs/secrets-and-playback.md).
/// </summary>
public sealed class SecretsSettings
{
    /// <summary>"Environment" (default, shipped) or the name of a host-registered
    /// ISecretProvider (e.g. "AzureKeyVault", "AwsSecretsManager").</summary>
    public string Provider { get; set; } = "Environment";
    /// <summary>Adapter endpoint (e.g. a Key Vault URI) — passed through to the adapter; unused by the shipped providers.</summary>
    public string? VaultUri { get; set; }
    /// <summary>Development-only inline secrets, tried first. Environment policy can ban
    /// inline connection-string sources for shared environments.</summary>
    public Dictionary<string, string> Inline { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public enum RegistryUnavailableBehavior { Warn, Fail }

/// <summary>
/// Run-registry integration (W2-D7). When <see cref="Url"/> is set, `tdm run` registers the
/// run and acquires a lease on every domain's (environment, domain, database) before seeding,
/// heartbeating during. A lock conflict always fails fast naming the holder; an unreachable
/// registry degrades per <see cref="Unavailable"/>.
/// </summary>
public sealed class RegistrySettings
{
    public string? Url { get; set; }
    /// <summary>Name of the environment variable holding the API key sent as X-Tdm-ApiKey; unset = anonymous.</summary>
    public string? ApiKeyEnv { get; set; }
    /// <summary>Warn: log and continue without registry/locks. Fail: refuse to run (exit 2).</summary>
    public RegistryUnavailableBehavior Unavailable { get; set; } = RegistryUnavailableBehavior.Warn;
    public int LockTtlSeconds { get; set; } = 60;
    public int HeartbeatSeconds { get; set; } = 20;
}

public sealed class SigningSettings
{
    /// <summary>Path to a PKCS#12 (.pfx) certificate containing the private signing key.</summary>
    public string CertificatePath { get; set; } = "";
    /// <summary>Name of the environment variable holding the certificate's password; unset/empty = no password.</summary>
    public string? CertificatePasswordEnv { get; set; }
}

public enum PluginAcquisitionMode { Folder, NuGet }

/// <summary>Plugin acquisition settings (W1-D2). Folder is the default (current behaviour);
/// NuGet resolves domains[].package from the configured feeds with a lockfile.</summary>
public sealed class PluginsSettings
{
    public PluginAcquisitionMode Acquisition { get; set; } = PluginAcquisitionMode.Folder;
    public List<PluginFeedSettings> Feeds { get; set; } = [];
    /// <summary>Downloaded .nupkg cache; defaults to ~/.tdm/cache.</summary>
    public string? CachePath { get; set; }

    public string ResolveCachePath() =>
        string.IsNullOrWhiteSpace(CachePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tdm", "cache")
            : Path.GetFullPath(CachePath);
}

/// <summary>Feed auth goes through the standard NuGet credential chain (nuget.config /
/// environment / credential providers) — TDM implements no custom secret handling.</summary>
public sealed class PluginFeedSettings
{
    public string Url { get; set; } = "";
}

public sealed class DomainSettings
{
    public string Name { get; set; } = "";
    /// <summary>NuGet package id of the domain data assembly (resolved into the plugin folder).</summary>
    public string? Package { get; set; }
    /// <summary>Version or floating range for <see cref="Package"/> (e.g. "3.2.1", "3.2.*").
    /// Omit for latest stable. The resolved version is pinned in tdm.plugins.lock.json.</summary>
    public string? PackageVersion { get; set; }
    /// <summary>Explicit plugin folder; defaults to ./plugins/{Name} when omitted.</summary>
    public string? PluginPath { get; set; }
    /// <summary>Sqlite | SqlServer (in-box) | any provider registered via an IProviderBootstrap
    /// plugin package, e.g. PostgreSql from Tdm.Providers.PostgreSql (W3-D5).</summary>
    public string Provider { get; set; } = "Sqlite";
    /// <summary>Inline connection string; wins over <see cref="ConnectionStringName"/>.</summary>
    public string? ConnectionString { get; set; }
    /// <summary>Name resolved through the secret chain (W2-D8) — candidates tried:
    /// TDM_CONNECTIONSTRINGS__{NAME}, ConnectionStrings__{Name}, then the bare name.</summary>
    public string? ConnectionStringName { get; set; }
    /// <summary>Set by the host after secret-chain resolution of <see cref="ConnectionStringName"/>;
    /// never serialized. Wins over the built-in environment-variable fallback.</summary>
    [JsonIgnore]
    public string? ResolvedConnectionString { get; set; }
    public string ConventionProfile { get; set; } = "modern";
    public PersistenceMode Persistence { get; set; } = PersistenceMode.RepositoryFirst;
    public ExternalReferenceMode ExternalReferences { get; set; } = ExternalReferenceMode.Synthesize;
    /// <summary>URL template for Verify mode, e.g. "https://crm.example/api/{entity}/{id}".</summary>
    public string? VerifyEndpoint { get; set; }
    /// <summary>Create the schema on first use (EnsureCreated). For local/demo databases only.</summary>
    public bool EnsureCreated { get; set; }
    /// <summary>Caps <see cref="RunSettings.MaxParallelScenarios"/> when this domain participates
    /// in a run — a fragile database serialises the whole run without touching run settings.</summary>
    public int? MaxParallelScenarios { get; set; }
    /// <summary>Bogus locale for this domain's generated names/addresses (W4-D5), e.g.
    /// "en_GB", "de", "fr". Default "en". Determinism is unchanged — the locale picks the
    /// vocabulary, the per-scenario Randomizer picks from it.</summary>
    public string? Locale { get; set; }
    /// <summary>Endpoint map for <see cref="PersistenceMode.Api"/> (W4-D6).</summary>
    public ApiSettings? Api { get; set; }

    public string ResolveConnectionString()
    {
        if (!string.IsNullOrWhiteSpace(ConnectionString)) return ConnectionString;
        if (!string.IsNullOrWhiteSpace(ResolvedConnectionString)) return ResolvedConnectionString;
        if (!string.IsNullOrWhiteSpace(ConnectionStringName))
        {
            var value = Environment.GetEnvironmentVariable($"TDM_CONNECTIONSTRINGS__{ConnectionStringName.ToUpperInvariant()}")
                     ?? Environment.GetEnvironmentVariable($"ConnectionStrings__{ConnectionStringName}");
            if (!string.IsNullOrWhiteSpace(value)) return value;
            throw new InvalidOperationException(
                $"Domain '{Name}': connection string '{ConnectionStringName}' not found in environment " +
                $"(looked for TDM_CONNECTIONSTRINGS__{ConnectionStringName.ToUpperInvariant()} and ConnectionStrings__{ConnectionStringName}).");
        }
        throw new InvalidOperationException($"Domain '{Name}': neither connectionString nor connectionStringName configured.");
    }
}

public sealed class ConventionProfile
{
    public string? EntityNamespaceSuffix { get; set; }
    /// <summary>Informational only — folders do not exist in compiled assemblies (handoff §4).</summary>
    public string? EntityFolder { get; set; }
    public string EntityClassPattern { get; set; } = "{Name}";

    /// <summary>
    /// Ordered probe patterns for the write-side repository interface. The first interface
    /// found wins; its Add/Update/Delete methods carry TDM's persistence (ADR-0001).
    /// </summary>
    public List<string> WriteRepositoryPatterns { get; set; } = ["I{Name}Repository"];

    /// <summary>
    /// Ordered probe patterns for the read-side repository interface. Discovered for
    /// reporting (list-entities) only — TDM's verification reads go through the DbContext.
    /// </summary>
    public List<string> ReadRepositoryPatterns { get; set; } = ["I{Name}Repository"];

    /// <summary>
    /// Back-compat single pattern: when set, it is prepended to both
    /// <see cref="WriteRepositoryPatterns"/> and <see cref="ReadRepositoryPatterns"/>.
    /// </summary>
    public string? RepositoryPattern
    {
        get => _repositoryPattern;
        set
        {
            _repositoryPattern = value;
            if (string.IsNullOrWhiteSpace(value)) return;
            if (!WriteRepositoryPatterns.Contains(value)) WriteRepositoryPatterns.Insert(0, value);
            if (!ReadRepositoryPatterns.Contains(value)) ReadRepositoryPatterns.Insert(0, value);
        }
    }
    private string? _repositoryPattern;

    /// <summary>
    /// Policy: every persistable entity must have a write repository (ADR-0001). Violations
    /// fail `tdm validate` / refuse `tdm run`; opt out per entity via entities.{Name}.requireRepository.
    /// </summary>
    public bool RequireWriteRepository { get; set; }

    public string FakerPattern { get; set; } = "{Name}Faker";
    public string NaturalKeyDefault { get; set; } = "Name";

    /// <summary>Duck-typed persist method names, probed in order (handoff §5.2). "{Name}" expands per entity.</summary>
    public List<string> AddMethodNames { get; set; } = ["Add", "AddAsync", "Add{Name}", "Add{Name}Async", "Insert", "InsertAsync", "Create", "CreateAsync"];
    public List<string> UpdateMethodNames { get; set; } = ["Update", "UpdateAsync", "Update{Name}", "Update{Name}Async", "Save", "SaveAsync"];
    public List<string> DeleteMethodNames { get; set; } = ["Delete", "DeleteAsync", "Delete{Name}", "Delete{Name}Async", "Remove", "RemoveAsync"];

    public static readonly IReadOnlyDictionary<string, ConventionProfile> BuiltIn =
        new Dictionary<string, ConventionProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["modern"] = new()
            {
                EntityNamespaceSuffix = "Data.Persistence.Domain",
                EntityFolder = "Entity",
                EntityClassPattern = "{Name}Entity",
                // Split read/write repositories per entity; plain I{Name}Repository accepted
                // where a split pair has not been introduced yet.
                WriteRepositoryPatterns = ["I{Name}WriteRepository", "I{Name}Repository"],
                ReadRepositoryPatterns = ["I{Name}ReadRepository", "I{Name}Repository"],
                RequireWriteRepository = true,
            },
            ["legacy"] = new()
            {
                EntityNamespaceSuffix = "Data.Infrastructure",
                EntityFolder = "Model",
                EntityClassPattern = "{Name}Model",
            },
        };
}

public sealed class EntitySettings
{
    public static readonly EntitySettings Default = new();

    public string? NaturalKey { get; set; }
    public IdStrategy IdStrategy { get; set; } = IdStrategy.Auto;
    /// <summary>Overrides the profile's <see cref="ConventionProfile.RequireWriteRepository"/> policy
    /// for this entity — set false for aggregate children / projections persisted via their root.</summary>
    public bool? RequireRepository { get; set; }
    /// <summary>Explicit write-repository interface name when the repo defies the profile patterns.</summary>
    public string? WriteRepository { get; set; }
    public ExternalBehavior ExternalBehavior { get; set; } = ExternalBehavior.FkOnly;
    /// <summary>Logical name of the local read-model entity seeded when <see cref="ExternalBehavior.Projection"/> applies.</summary>
    public string? ProjectionEntity { get; set; }
    /// <summary>Config-declared statistical generation per property (W4-D4): distributions,
    /// categorical weights and dataset columns, applied over the faker's output and drawing
    /// from the per-scenario Randomizer (W4-D5). Overrides still win.</summary>
    public Dictionary<string, PropertyGenerationSettings> Properties { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// One property's statistical generation config (W4-D4). Exactly one of
/// <see cref="Distribution"/>, <see cref="Weights"/> or <see cref="Dataset"/> applies.
/// </summary>
public sealed class PropertyGenerationSettings
{
    /// <summary>normal | lognormal | uniform | exponential.</summary>
    public string? Distribution { get; set; }
    /// <summary>normal: the mean. lognormal: the median (scale) — exp(μ). exponential: the mean (1/rate).</summary>
    public double? Mean { get; set; }
    /// <summary>normal: standard deviation. lognormal: σ of the underlying normal.</summary>
    public double? Sigma { get; set; }
    /// <summary>uniform bounds; for other distributions an optional clamp.</summary>
    public double? Min { get; set; }
    public double? Max { get; set; }
    /// <summary>Rounding for floating targets (default 2). Integer targets always round to whole.</summary>
    public int? Decimals { get; set; }
    /// <summary>Weighted categorical values: value → relative weight (normalised at sample time).</summary>
    public Dictionary<string, double>? Weights { get; set; }
    /// <summary>Name of a <see cref="TdmSettings.Datasets"/> entry. All properties of one
    /// entity naming the same dataset are filled from a single sampled row (W4-D5).</summary>
    public string? Dataset { get; set; }
    /// <summary>Column within <see cref="Dataset"/>; defaults to the property name.</summary>
    public string? Column { get; set; }
}

/// <summary>A named CSV dataset (first row = header), path relative to the settings file.</summary>
public sealed class DatasetSettings
{
    public string Path { get; set; } = "";
}

/// <summary>
/// One seed pack reference (W4-D7): either a NuGet package (riding the plugin feeds +
/// tdm.plugins.lock.json, so packs are reproducible like plugins) or a local folder for
/// development / CI-restored layouts. Pack layout: <c>features/*.feature</c>,
/// optional <c>tdm.entities.json</c> fragment, optional <c>tdm.keys.json</c> registry,
/// optional <c>datasets/</c>.
/// </summary>
public sealed class SeedPackSettings
{
    /// <summary>NuGet package id, resolved from plugins.feeds.</summary>
    public string? Package { get; set; }
    /// <summary>Version or floating range; the resolved version is pinned in the lockfile.</summary>
    public string? Version { get; set; }
    /// <summary>Local pack folder (dev mode); wins over <see cref="Package"/> when set.</summary>
    public string? Path { get; set; }
}

/// <summary>
/// API persistence for one domain (W4-D6): seeding routes through the domain's public HTTP
/// API — which also exercises its validation, auth and side-effects for free. Supported
/// lifecycles: Persistent and TrackedTeardown (deletes via API, reverse order);
/// Transactional is unsupported and fails validation.
/// </summary>
public sealed class ApiSettings
{
    public string BaseUrl { get; set; } = "";
    public ApiAuthSettings? Auth { get; set; }
    /// <summary>Logical entity name → endpoint templates. This map is also the domain's
    /// entity list: only entities named here are seedable via the API.</summary>
    public Dictionary<string, ApiEntityEndpoints> Entities { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public int TimeoutSeconds { get; set; } = 30;
    /// <summary>Retries per request on 5xx/connection failure (bulk risk mitigation, §6).</summary>
    public int MaxRetries { get; set; } = 2;
    public int RetryDelayMs { get; set; } = 200;
}

/// <summary>
/// Endpoint templates: "METHOD /path", with <c>{id}</c> (key value) and <c>{key}</c>
/// (URL-escaped natural key) placeholders, e.g. "GET /api/customers?name={key}".
/// </summary>
public sealed class ApiEntityEndpoints
{
    public string? Create { get; set; }
    public string? Update { get; set; }
    public string? Delete { get; set; }
    public string? GetByKey { get; set; }
    /// <summary>Optional — enables manifest playback/verify by recorded id.</summary>
    public string? GetById { get; set; }
}

/// <summary>
/// API auth (W4-D6): the shipped mode resolves a token through the W2-D8 secret chain and
/// sends it as "{scheme} {token}" in <see cref="HeaderName"/>. Cloud token flows (AzureAd
/// client credentials etc.) belong host-side behind the same posture as ISecretProvider —
/// resolve the token there and hand it to TDM via the chain; TDM ships no cloud SDKs.
/// </summary>
public sealed class ApiAuthSettings
{
    /// <summary>Secret-chain name of the token (inline → environment → cloud adapter).</summary>
    public string? TokenSecret { get; set; }
    public string HeaderName { get; set; } = "Authorization";
    /// <summary>Prefix before the token; empty for raw header values (API keys).</summary>
    public string Scheme { get; set; } = "Bearer";
}
