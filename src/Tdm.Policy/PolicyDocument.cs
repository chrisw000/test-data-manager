using System.Text.Json;
using Tdm.Core.Settings;

namespace Tdm.Policy;

/// <summary>
/// tdm.policy.json (W2-D3): environment-scoped rules evaluated by <see cref="PolicyEvaluator"/>
/// before any persistence — at `tdm validate` always, and at `tdm run` start. JSON with
/// comments/trailing commas allowed, same as tdm.settings.json; a "$schema" key is accepted
/// and ignored (see docs/schemas/tdm.policy.schema.json).
/// </summary>
public sealed class PolicyDocument
{
    public const string FileName = "tdm.policy.json";

    public int PolicyVersion { get; set; } = 1;
    public Dictionary<string, EnvironmentPolicy> Environments { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static PolicyDocument Load(string path)
    {
        using var stream = File.OpenRead(path);
        var document = JsonSerializer.Deserialize<PolicyDocument>(stream, TdmSettings.JsonOptions)
                       ?? throw new InvalidOperationException($"Policy file '{path}' deserialised to null.");
        // System.Text.Json replaces the whole dictionary on deserialize, discarding the
        // OrdinalIgnoreCase comparer from the property initializer — rebuild it explicitly.
        document.Environments = new Dictionary<string, EnvironmentPolicy>(document.Environments, StringComparer.OrdinalIgnoreCase);
        return document;
    }
}

/// <summary>Rules for one named environment. Every field is optional — an unset rule is not enforced.</summary>
public sealed class EnvironmentPolicy
{
    /// <summary>Lifecycles permitted for both run.lifecycle and any scenario's @persistent/@ephemeral override.</summary>
    public List<LifecycleMode>? AllowedLifecycles { get; set; }
    /// <summary>Minimum failure-policy strictness required (BestEffort &lt; FailObject &lt; FailRun).</summary>
    public FailurePolicy? RequireFailurePolicyAtLeast { get; set; }
    public int? MaxBulkRowsPerStep { get; set; }
    public int? MaxCreatedRowsPerRun { get; set; }
    /// <summary>Allowed connection-string sources per domain: "inline" | "env".</summary>
    public List<string>? ConnectionStringSources { get; set; }
    public List<string> BannedEntities { get; set; } = [];
    /// <summary>Every scenario must carry a tag matching each requirement (exact, or a
    /// "@seed:" style prefix match against "@seed").</summary>
    public List<string> RequiredTags { get; set; } = [];
    /// <summary>Escape hatch (W2-D4): a matching --approval token bypasses this environment's
    /// violations (except key-registry violations, which are never overridable) and is
    /// recorded in the manifest.</summary>
    public PolicyOverride? Override { get; set; }
}

public sealed class PolicyOverride
{
    public string Kind { get; set; } = "approvalToken";
    /// <summary>Name of the environment variable holding the token that --approval must match.</summary>
    public string TokenEnv { get; set; } = "";
}

/// <summary>One violation of an environment rule or the key registry.</summary>
public sealed record PolicyViolation(string Rule, string Message);

public sealed class PolicyEvaluationResult
{
    public List<PolicyViolation> Violations { get; init; } = [];
    public bool OverrideApplied { get; init; }
}
