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
