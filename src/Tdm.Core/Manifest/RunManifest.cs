using System.Text.Json.Serialization;
using Tdm.Core.Settings;

namespace Tdm.Core.Manifest;

public enum RunOutcome { Succeeded, CompletedWithWarnings, Failed }

public enum ScenarioOutcome { Succeeded, CompletedWithWarnings, Failed, Skipped }

/// <summary>
/// The JSON run report + seeding manifest (handoff §11). Records full final property values
/// and the effective seed per scenario — the artefact that makes any run exactly
/// reproducible/playback-able and serves the audit-evidence posture.
/// </summary>
public sealed class RunManifest
{
    public RunInfo Run { get; set; } = new();
    public List<ScenarioManifest> Scenarios { get; set; } = [];
    public TeardownManifest Teardown { get; set; } = new();

    [JsonIgnore]
    public int ExitCode => Run.Outcome switch
    {
        RunOutcome.Succeeded => 0,
        RunOutcome.CompletedWithWarnings => 1,
        _ => 2,
    };
}

public sealed class RunInfo
{
    public string Name { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public double DurationMs { get; set; }
    public FailurePolicy FailurePolicy { get; set; }
    public LifecycleMode Lifecycle { get; set; }
    public string TdmVersion { get; set; } = "";
    /// <summary>Recorded because Bogus determinism can shift across major versions (handoff §7).</summary>
    public string BogusVersion { get; set; } = "";
    public string EfVersion { get; set; } = "";
    public string IdentityContractVersion { get; set; } = "";
    public bool DryRun { get; set; }
    public RunOutcome Outcome { get; set; }
    /// <summary>Resolved plugin package versions per domain ("{domain}:{packageId}" → version)
    /// when NuGet acquisition is used — a run is reproducible down to the plugin version (W1-D2).</summary>
    public Dictionary<string, string> PluginPackages { get; set; } = [];
    public Dictionary<string, BenchmarkStats> Benchmark { get; set; } = [];
    /// <summary>Who/what ran this and against which config (W2-D1). Consumed alongside the
    /// checksum/signature written next to the manifest file — see docs/audit-and-signing.md.</summary>
    public AttributionInfo Attribution { get; set; } = new();
    /// <summary>Synthetic-data attestation (W2-D1): classifies every generator source used in
    /// the run. All sources are synthetic by construction in v1.</summary>
    public AttestationInfo Attestation { get; set; } = new();
    /// <summary>Target environment name from --env (W2-D3), if given.</summary>
    public string? Environment { get; set; }
    /// <summary>Policy (tdm.policy.json) and key-registry (tdm.keys.json) violations found
    /// before persistence. Non-empty and not overridden → the run refuses to start (exit 2).</summary>
    public List<PolicyViolationInfo> PolicyViolations { get; set; } = [];
    /// <summary>True when environment-policy violations were present but bypassed via a
    /// validated --approval token (W2-D4). Key-registry violations are never overridable.</summary>
    public bool PolicyOverrideApplied { get; set; }
    /// <summary>Run id assigned by the run registry (W2-D7), when registry.url is configured —
    /// the manifest links back to the registry entry that links to it.</summary>
    public string? RegistryRunId { get; set; }
}

/// <summary>One policy or key-registry violation (W2-D3/W2-D6), structured for the SARIF emitter.</summary>
public sealed class PolicyViolationInfo
{
    /// <summary>Rule identifier, e.g. "AllowedLifecycles", "MaxBulkRowsPerStep", "KeyRegistry".</summary>
    public string Rule { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>Runner identity, git state and config hash captured at manifest-build time (W2-D1).
/// Populated by the host (Tdm.Observability.Audit.AttributionCollector) — Tdm.Core stays
/// environment-free.</summary>
public sealed class AttributionInfo
{
    /// <summary>"github-actions:{server}/{repo}/actions/runs/{id}#{actor}", "ci:{url}", or "local:{username}".</summary>
    public string RunnerId { get; set; } = "";
    public string Hostname { get; set; } = "";
    /// <summary>HEAD commit of the repository containing the settings/feature files, if resolvable.</summary>
    public string? GitSha { get; set; }
    /// <summary>True if that repository had uncommitted changes at run time.</summary>
    public bool? GitDirty { get; set; }
    /// <summary>SHA-256 (hex) of tdm.settings.json as loaded — tamper-evidence for the config, not just the data.</summary>
    public string? SettingsFileSha256 { get; set; }
}

/// <summary>Classification of the generator sources that produced this run's data (W2-D1).
/// Becomes falsifiable once Wave 4 explores production-data subsetting.</summary>
public sealed class AttestationInfo
{
    public bool SyntheticOnly { get; set; } = true;
    /// <summary>Distinct sources observed: ConventionFaker, AutoFaker, Override, IdentityContract.</summary>
    public List<string> Sources { get; set; } = [];
}

public sealed class ScenarioManifest
{
    public string Feature { get; set; } = "";
    /// <summary>Source feature file path ("&lt;inline&gt;" for text-parsed features) — SARIF location anchor (W1-D3).</summary>
    public string FeatureFile { get; set; } = "";
    public string Scenario { get; set; } = "";
    /// <summary>Line of the Scenario keyword in <see cref="FeatureFile"/>.</summary>
    public int Line { get; set; }
    public int Seed { get; set; }
    public List<string> Tags { get; set; } = [];
    public LifecycleMode Lifecycle { get; set; }
    public List<EntityManifest> Entities { get; set; } = [];
    /// <summary>One summary per count-bulk create (W3-D4). In Sample/None modes the rows not
    /// carried in <see cref="Entities"/> are represented here by count + value hash.</summary>
    public List<BulkOperationManifest> BulkOperations { get; set; } = [];
    public List<ReferenceManifest> References { get; set; } = [];
    public List<UnmatchedStepManifest> UnmatchedSteps { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public ScenarioOutcome Outcome { get; set; }
    public Dictionary<string, BenchmarkStats> Benchmark { get; set; } = [];
    public ScenarioTeardown? Teardown { get; set; }
}

public sealed class EntityManifest
{
    public int Ordinal { get; set; }
    public string Entity { get; set; } = "";
    public string Verb { get; set; } = "";
    public string Domain { get; set; } = "";
    public string? PersistedVia { get; set; }
    public string? Id { get; set; }
    public string? IdStrategy { get; set; }
    public string? NaturalKey { get; set; }
    /// <summary>Convention faker type name, or "auto" for the heuristic fallback faker.</summary>
    public string? FakerSource { get; set; }
    /// <summary>Full final values — faker output plus overrides — stringified invariant.</summary>
    public Dictionary<string, string?> Values { get; set; } = [];
    public List<string> OverridesApplied { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public double DurationMs { get; set; }
}

/// <summary>
/// Aggregate record of one count-bulk create (W3-D4). Sampled rows (and any failed rows,
/// which always keep their full entries) live in <see cref="ScenarioManifest.Entities"/>;
/// the rest are audit-summarised as a count plus an ordinal-ordered SHA-256 value hash —
/// the manifest stays usable at a million rows without losing tamper-evidence.
/// </summary>
public sealed class BulkOperationManifest
{
    public string Entity { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Verb { get; set; } = "Create";
    /// <summary>Rows the step asked for.</summary>
    public int Requested { get; set; }
    /// <summary>Rows successfully persisted ("dry-run" rows in a validate pass count here too).</summary>
    public int Count { get; set; }
    public int Failed { get; set; }
    public string? PersistedVia { get; set; }
    public BulkManifestMode Mode { get; set; }
    /// <summary>Rows carried with full values in the scenario's entities list.</summary>
    public int SampledRows { get; set; }
    /// <summary>Rows represented only by <see cref="ValuesSha256"/>.</summary>
    public int HashedRows { get; set; }
    /// <summary>SHA-256 (hex) over the ordinal-ordered canonical snapshots of the unsampled rows.</summary>
    public string? ValuesSha256 { get; set; }
    public int FirstOrdinal { get; set; }
    public int LastOrdinal { get; set; }
    public double DurationMs { get; set; }
}

public sealed class ReferenceManifest
{
    public int Step { get; set; }
    /// <summary>"Entity:naturalKey".</summary>
    public string Target { get; set; } = "";
    /// <summary>contextBag | database | identityContract | dry-run-skipped.</summary>
    public string ResolvedFrom { get; set; } = "";
    public string? Id { get; set; }
    /// <summary>Owning domain, for external references.</summary>
    public string? OwningDomain { get; set; }
    public ExternalReferenceMode? Mode { get; set; }
    public ExternalBehavior? Behavior { get; set; }
    public string? VerifyOutcome { get; set; }
}

public sealed class UnmatchedStepManifest
{
    public string Text { get; set; } = "";
    public int Line { get; set; }
}

public sealed class ScenarioTeardown
{
    public int Deleted { get; set; }
    public List<string> Orphaned { get; set; } = [];
}

public sealed class TeardownManifest
{
    public int Deleted { get; set; }
    /// <summary>Teardown failures are never silently swallowed (handoff §9).</summary>
    public List<string> Orphaned { get; set; } = [];
}

public sealed class BenchmarkStats
{
    public int Count { get; set; }
    public double TotalMs { get; set; }
    public double MeanMs { get; set; }
    public double P50Ms { get; set; }
    public double P95Ms { get; set; }
    public double MaxMs { get; set; }
}
