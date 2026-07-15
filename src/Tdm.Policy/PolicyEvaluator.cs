using Tdm.Core.Grammar;
using Tdm.Core.Settings;

namespace Tdm.Policy;

/// <summary>
/// Evaluates an <see cref="EnvironmentPolicy"/> against a parsed <see cref="SeedingPlan"/> and
/// the run settings (W2-D3). Everything checked here — lifecycles, bulk counts, tags,
/// connection-string sources, banned entities — is statically known from the plan before any
/// step executes, so evaluation needs no domain runtime and no database connection.
/// </summary>
public static class PolicyEvaluator
{
    /// <param name="getEnv">Environment-variable lookup for the approval-token override —
    /// injected so evaluation stays pure and testable (mirrors AttributionCollector).</param>
    public static PolicyEvaluationResult Evaluate(PolicyDocument policy, string environmentName,
        SeedingPlan plan, TdmSettings settings, string? approvalToken, Func<string, string?> getEnv)
    {
        if (!policy.Environments.TryGetValue(environmentName, out var env))
        {
            return new PolicyEvaluationResult
            {
                Violations =
                [
                    new PolicyViolation("UnknownEnvironment",
                        $"Environment '{environmentName}' is not declared in the policy file. " +
                        $"Known: {string.Join(", ", policy.Environments.Keys)}."),
                ],
            };
        }

        var violations = new List<PolicyViolation>();
        CheckLifecycles(env, plan, settings, violations);
        CheckFailurePolicy(env, settings, violations);
        CheckRowCounts(env, plan, violations);
        CheckConnectionStringSources(env, settings, environmentName, violations);
        CheckBannedEntities(env, plan, environmentName, violations);
        CheckRequiredTags(env, plan, violations);

        var overrideApplied = violations.Count > 0 && IsOverrideValid(env.Override, approvalToken, getEnv);
        return new PolicyEvaluationResult { Violations = violations, OverrideApplied = overrideApplied };
    }

    private static void CheckLifecycles(EnvironmentPolicy env, SeedingPlan plan, TdmSettings settings, List<PolicyViolation> violations)
    {
        if (env.AllowedLifecycles is not { Count: > 0 } allowed) return;

        if (!allowed.Contains(settings.Run.Lifecycle))
        {
            violations.Add(new PolicyViolation("AllowedLifecycles",
                $"run.lifecycle '{settings.Run.Lifecycle}' is not permitted (allowed: {string.Join(", ", allowed)})."));
        }
        foreach (var scenario in AllScenarios(plan))
        {
            if (scenario.LifecycleOverride is { } lifecycle && !allowed.Contains(lifecycle))
            {
                violations.Add(new PolicyViolation("AllowedLifecycles",
                    $"Scenario '{scenario.Name}' overrides lifecycle to '{lifecycle}', not permitted (allowed: {string.Join(", ", allowed)})."));
            }
        }
    }

    private static void CheckFailurePolicy(EnvironmentPolicy env, TdmSettings settings, List<PolicyViolation> violations)
    {
        if (env.RequireFailurePolicyAtLeast is not { } minimum) return;
        if (Strictness(settings.Run.FailurePolicy) < Strictness(minimum))
        {
            violations.Add(new PolicyViolation("RequireFailurePolicyAtLeast",
                $"run.failurePolicy '{settings.Run.FailurePolicy}' is weaker than the minimum '{minimum}' required."));
        }
    }

    private static void CheckRowCounts(EnvironmentPolicy env, SeedingPlan plan, List<PolicyViolation> violations)
    {
        var totalRows = 0;
        foreach (var scenario in AllScenarios(plan))
        foreach (var step in scenario.Steps)
        {
            if (step is not CreateStep create) continue;
            var rows = create.Rows?.Count ?? create.Count;
            totalRows += rows;
            if (env.MaxBulkRowsPerStep is { } maxBulk && rows > maxBulk)
            {
                violations.Add(new PolicyViolation("MaxBulkRowsPerStep",
                    $"[{scenario.Name} line {step.Line}] creates {rows} row(s) of '{create.Entity}', exceeding the max of {maxBulk}."));
            }
        }
        if (env.MaxCreatedRowsPerRun is { } maxTotal && totalRows > maxTotal)
        {
            violations.Add(new PolicyViolation("MaxCreatedRowsPerRun",
                $"Run would create {totalRows} row(s) total, exceeding the max of {maxTotal}."));
        }
    }

    private static void CheckConnectionStringSources(EnvironmentPolicy env, TdmSettings settings, string environmentName, List<PolicyViolation> violations)
    {
        if (env.ConnectionStringSources is not { Count: > 0 } allowed) return;
        foreach (var domain in settings.Domains)
        {
            var source = !string.IsNullOrWhiteSpace(domain.ConnectionString) ? "inline"
                : !string.IsNullOrWhiteSpace(domain.ConnectionStringName) ? "env"
                : "unknown";
            if (!allowed.Contains(source, StringComparer.OrdinalIgnoreCase))
            {
                violations.Add(new PolicyViolation("ConnectionStringSources",
                    $"Domain '{domain.Name}' resolves its connection string via '{source}', not permitted in environment " +
                    $"'{environmentName}' (allowed: {string.Join(", ", allowed)})."));
            }
        }
    }

    private static void CheckBannedEntities(EnvironmentPolicy env, SeedingPlan plan, string environmentName, List<PolicyViolation> violations)
    {
        if (env.BannedEntities.Count == 0) return;
        var banned = new HashSet<string>(env.BannedEntities, StringComparer.OrdinalIgnoreCase);
        foreach (var scenario in AllScenarios(plan))
        foreach (var step in scenario.Steps)
        {
            var entity = EntityNameOf(step);
            if (entity is not null && banned.Contains(entity))
            {
                violations.Add(new PolicyViolation("BannedEntities",
                    $"[{scenario.Name} line {step.Line}] targets banned entity '{entity}' in environment '{environmentName}'."));
            }
        }
    }

    private static void CheckRequiredTags(EnvironmentPolicy env, SeedingPlan plan, List<PolicyViolation> violations)
    {
        if (env.RequiredTags.Count == 0) return;
        foreach (var scenario in AllScenarios(plan))
        {
            var missing = env.RequiredTags.Where(required => !scenario.Tags.Any(tag => TagMatches(tag, required))).ToList();
            if (missing.Count > 0)
            {
                violations.Add(new PolicyViolation("RequiredTags",
                    $"Scenario '{scenario.Name}' is missing required tag(s): {string.Join(", ", missing)}."));
            }
        }
    }

    private static bool IsOverrideValid(PolicyOverride? policyOverride, string? approvalToken, Func<string, string?> getEnv)
    {
        if (policyOverride is not { Kind: "approvalToken" } || string.IsNullOrEmpty(policyOverride.TokenEnv)) return false;
        var expected = getEnv(policyOverride.TokenEnv);
        return !string.IsNullOrEmpty(expected) && !string.IsNullOrEmpty(approvalToken) &&
               string.Equals(approvalToken, expected, StringComparison.Ordinal);
    }

    private static IEnumerable<ScenarioPlan> AllScenarios(SeedingPlan plan) =>
        plan.Features.SelectMany(f => f.Scenarios);

    private static int Strictness(FailurePolicy policy) => policy switch
    {
        FailurePolicy.BestEffort => 0,
        FailurePolicy.FailObject => 1,
        FailurePolicy.FailRun => 2,
        _ => 0,
    };

    private static bool TagMatches(string tag, string requirement) =>
        tag.Equals(requirement, StringComparison.OrdinalIgnoreCase) ||
        tag.StartsWith(requirement + ":", StringComparison.OrdinalIgnoreCase);

    private static string? EntityNameOf(StepPlan step) => step switch
    {
        CreateStep c => c.Entity,
        UpdateStep u => u.Entity,
        DeleteStep d => d.Entity,
        LoadStep l => l.Entity,
        ExternalReferenceStep e => e.Entity,
        _ => null,
    };
}
