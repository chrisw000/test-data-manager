using Tdm.Core.Settings;

namespace Tdm.Core.Grammar;

public sealed record PropertyAssignment(string Name, string RawValue);

/// <summary>Ordered property bag; order matters for deterministic override application.</summary>
public sealed class PropertyBag : List<PropertyAssignment>
{
    public PropertyBag() { }
    public PropertyBag(IEnumerable<PropertyAssignment> items) : base(items) { }
}

public sealed record ReferenceClause(string Entity, string Key);

public abstract class StepPlan
{
    public required string Text { get; init; }
    public required string Keyword { get; init; }
    public required int Line { get; init; }
}

/// <summary>Create verb — single, DataTable bulk, or count bulk.</summary>
public sealed class CreateStep : StepPlan
{
    public string? Domain { get; init; }
    public required string Entity { get; init; }
    public int Count { get; init; } = 1;
    public PropertyBag Overrides { get; init; } = [];
    /// <summary>Rows from a DataTable; when set, one entity per row.</summary>
    public List<PropertyBag>? Rows { get; init; }
    public List<ReferenceClause> References { get; init; } = [];
}

public sealed class UpdateStep : StepPlan
{
    public required string Entity { get; init; }
    public required string Key { get; init; }
    public PropertyBag Overrides { get; init; } = [];
    public List<ReferenceClause> References { get; init; } = [];
}

public sealed class DeleteStep : StepPlan
{
    public required string Entity { get; init; }
    /// <summary>Natural key for single delete; null for delete-all/filtered form.</summary>
    public string? Key { get; init; }
    public PropertyBag Filter { get; init; } = [];
    public bool All { get; init; }
}

/// <summary>Load/verify verb — read + assert; part of the benchmark surface.</summary>
public sealed class LoadStep : StepPlan
{
    public required string Entity { get; init; }
    public string? Key { get; init; }
    public int? ExpectedCount { get; init; }
    public PropertyBag Expected { get; init; } = [];
}

/// <summary>`Given an external Customer reference "Acme Ltd" from CRM` (handoff §8.5).</summary>
public sealed class ExternalReferenceStep : StepPlan
{
    public required string Entity { get; init; }
    public required string Key { get; init; }
    public required string SourceDomain { get; init; }
}

/// <summary>Text that fits no grammar rule — logged, manifested, handled per failure policy.</summary>
public sealed class UnmatchedStep : StepPlan;

public sealed class ScenarioPlan
{
    public required string FeatureName { get; init; }
    public required string Name { get; init; }
    /// <summary>Line of the Scenario keyword in the source feature file (outline expansions
    /// keep the original outline's line) — anchors SARIF report locations (W1-D3).</summary>
    public int Line { get; init; }
    public List<string> Tags { get; init; } = [];
    public List<StepPlan> Steps { get; init; } = [];

    public bool Skip => Tags.Any(t => t.Equals("@skip", StringComparison.OrdinalIgnoreCase));
    public bool ForceBenchmark => Tags.Any(t => t.Equals("@benchmark", StringComparison.OrdinalIgnoreCase));

    public int? Seed
    {
        get
        {
            var tag = Tags.FirstOrDefault(t => t.StartsWith("@seed:", StringComparison.OrdinalIgnoreCase));
            return tag is not null && int.TryParse(tag["@seed:".Length..], out var seed) ? seed : null;
        }
    }

    public string? DomainPin
    {
        get
        {
            var tag = Tags.FirstOrDefault(t => t.StartsWith("@domain:", StringComparison.OrdinalIgnoreCase));
            return tag?["@domain:".Length..];
        }
    }

    public LifecycleMode? LifecycleOverride
    {
        get
        {
            if (Tags.Any(t => t.Equals("@persistent", StringComparison.OrdinalIgnoreCase))) return LifecycleMode.Persistent;
            if (Tags.Any(t => t.Equals("@ephemeral", StringComparison.OrdinalIgnoreCase))) return LifecycleMode.TrackedTeardown;
            return null;
        }
    }
}

public sealed class FeaturePlan
{
    public required string Name { get; init; }
    public required string SourcePath { get; init; }
    public List<ScenarioPlan> Scenarios { get; init; } = [];
}

public sealed class SeedingPlan
{
    public List<FeaturePlan> Features { get; init; } = [];
}
