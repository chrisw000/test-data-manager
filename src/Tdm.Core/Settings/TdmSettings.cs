using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tdm.Core.Settings;

public enum FailurePolicy { BestEffort, FailObject, FailRun }

public enum LifecycleMode { Persistent, Transactional, TrackedTeardown }

public enum PersistenceMode { RepositoryFirst, DbContextOnly, RepositoryOnly }

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

public sealed class RunSettings
{
    public string Name { get; set; } = "tdm-run";
    public FailurePolicy FailurePolicy { get; set; } = FailurePolicy.BestEffort;
    public LifecycleMode Lifecycle { get; set; } = LifecycleMode.TrackedTeardown;
    public int DefaultSeed { get; set; } = 1;
    public List<string> FeaturePaths { get; set; } = [];
    public bool Benchmark { get; set; }
    /// <summary>Bulk creates are chunked into AddRange + SaveChanges batches of this size (handoff §12).</summary>
    public int BulkChunkSize { get; set; } = 500;
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
    /// <summary>Sqlite | SqlServer. Provider assemblies ship with the TDM host.</summary>
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
}
