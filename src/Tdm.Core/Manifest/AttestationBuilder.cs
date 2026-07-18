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
            if (!string.IsNullOrEmpty(entity.FakerSource))
            {
                // "auto+plugin:Sku+distributions" — base source, then W4-D4 marker segments.
                var segments = entity.FakerSource.Split('+');
                sources.Add(string.Equals(segments[0], "auto", StringComparison.Ordinal)
                    ? "AutoFaker"
                    : "ConventionFaker");
                foreach (var segment in segments.Skip(1))
                {
                    if (segment.StartsWith("plugin:", StringComparison.Ordinal)) sources.Add("GeneratorPlugin");
                    else if (segment == "distributions") sources.Add("Distribution");
                    else if (segment == "datasets") sources.Add("DatasetPack");
                }
            }

            if (entity.OverridesApplied.Count > 0)
                sources.Add("Override");

            if (string.Equals(entity.IdStrategy, "Deterministic", StringComparison.Ordinal))
                sources.Add("IdentityContract");
        }

        // All generator sources remain synthetic by construction: distributions and dataset
        // tuples are config-declared shapes, never production rows (the §2.6 spike keeps it so).
        return new AttestationInfo { SyntheticOnly = true, Sources = [.. sources] };
    }
}
