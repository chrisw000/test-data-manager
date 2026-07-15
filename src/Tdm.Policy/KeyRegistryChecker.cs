using Tdm.Core.Grammar;
using Tdm.Core.Naming;
using Tdm.Core.Registry;

namespace Tdm.Policy;

/// <summary>
/// Checks every external reference (`... from {Domain}`) in a plan against the owning
/// domain's published key registry (W2-D6). A domain with no registry, or an entity the
/// registry doesn't govern, is not checked — adoption is incremental and per-entity.
/// Key-registry violations are always fatal: unlike environment-policy rules, there is no
/// approval-token override, since this is a data-integrity contract between teams, not an
/// environment-safety guard.
/// </summary>
public static class KeyRegistryChecker
{
    public static List<PolicyViolation> Check(SeedingPlan plan, IReadOnlyDictionary<string, KeyRegistryDocument> registriesByDomain)
    {
        var violations = new List<PolicyViolation>();
        foreach (var feature in plan.Features)
        foreach (var scenario in feature.Scenarios)
        foreach (var step in scenario.Steps)
        {
            if (step is not ExternalReferenceStep external) continue;
            if (!registriesByDomain.TryGetValue(external.SourceDomain, out var registry)) continue;

            var entity = NameMatcher.Singularize(external.Entity);
            if (!registry.IsKeyKnown(entity, external.Key))
            {
                violations.Add(new PolicyViolation("KeyRegistry",
                    $"[{scenario.Name} line {step.Line}] external reference {entity} \"{external.Key}\" from '{external.SourceDomain}' " +
                    $"is not declared in that domain's key registry — check for a typo, or ask the {external.SourceDomain} team to publish it."));
            }
        }
        return violations;
    }
}
