namespace Tdm.Core.Manifest;

/// <summary>
/// Classifies the generator sources used across a completed run (W2-D1). Pure function over
/// an already-built <see cref="RunManifest"/> — no engine coupling, easy to test in isolation.
/// </summary>
public static class AttestationBuilder
{
    public static AttestationInfo Build(RunManifest manifest)
    {
        var sources = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entity in manifest.Scenarios.SelectMany(s => s.Entities))
        {
            if (string.Equals(entity.FakerSource, "auto", StringComparison.Ordinal))
                sources.Add("AutoFaker");
            else if (!string.IsNullOrEmpty(entity.FakerSource))
                sources.Add("ConventionFaker");

            if (entity.OverridesApplied.Count > 0)
                sources.Add("Override");

            if (string.Equals(entity.IdStrategy, "Deterministic", StringComparison.Ordinal))
                sources.Add("IdentityContract");
        }

        // All v1 generator sources are synthetic by construction — falsifiable once Wave 4
        // explores production-data subsetting.
        return new AttestationInfo { SyntheticOnly = true, Sources = [.. sources] };
    }
}
