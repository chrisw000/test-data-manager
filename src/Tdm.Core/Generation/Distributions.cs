using Bogus;
using Tdm.Core.Settings;

namespace Tdm.Core.Generation;

/// <summary>
/// Config-declared distribution sampling (W4-D4). Every draw comes from the supplied
/// per-scenario <see cref="Randomizer"/>, so determinism (v1 D8) is untouched: same seed,
/// same draws — the new capability rides the existing guarantee (W4-D5).
/// </summary>
public static class Distributions
{
    public static double Sample(PropertyGenerationSettings config, Randomizer random)
    {
        var value = config.Distribution?.ToLowerInvariant() switch
        {
            "normal" => Normal(random,
                Require(config.Mean, "normal", "mean"),
                Require(config.Sigma, "normal", "sigma")),
            // mean is the *median* (scale): exp(μ). "mean": 120, "sigma": 1.2 ⇒ half of all
            // draws below 120, long right tail — the natural way to read the config.
            "lognormal" => Math.Exp(Normal(random,
                Math.Log(RequirePositive(config.Mean, "lognormal", "mean")),
                Require(config.Sigma, "lognormal", "sigma"))),
            "uniform" => random.Double(
                Require(config.Min, "uniform", "min"),
                Require(config.Max, "uniform", "max")),
            "exponential" => -Math.Log(1 - random.Double()) * RequirePositive(config.Mean, "exponential", "mean"),
            _ => throw new InvalidOperationException(
                $"Unknown distribution '{config.Distribution}'. Supported: normal, lognormal, uniform, exponential."),
        };

        if (config.Min is { } min && value < min) value = min;
        if (config.Max is { } max && value > max) value = max;
        return value;
    }

    /// <summary>Weighted categorical draw. Keys are iterated ordinal-sorted so the draw
    /// sequence is stable even if the config file reorders entries.</summary>
    public static string SampleWeighted(IReadOnlyDictionary<string, double> weights, Randomizer random)
    {
        if (weights.Count == 0)
            throw new InvalidOperationException("weights must contain at least one value.");
        var ordered = weights.OrderBy(w => w.Key, StringComparer.Ordinal).ToList();
        var total = ordered.Sum(w => w.Value);
        if (total <= 0)
            throw new InvalidOperationException("weights must sum to a positive number.");

        var roll = random.Double() * total;
        var cumulative = 0d;
        foreach (var (value, weight) in ordered)
        {
            cumulative += weight;
            if (roll < cumulative) return value;
        }
        return ordered[^1].Key; // floating-point edge: roll == total
    }

    /// <summary>Box–Muller over two uniform draws — a fixed draw count per sample, so the
    /// Randomizer sequence stays aligned across runs.</summary>
    private static double Normal(Randomizer random, double mean, double sigma)
    {
        var u1 = 1 - random.Double(); // (0,1] — log(0) guarded
        var u2 = random.Double();
        return mean + sigma * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    private static double Require(double? value, string distribution, string name) =>
        value ?? throw new InvalidOperationException($"Distribution '{distribution}' requires '{name}'.");

    private static double RequirePositive(double? value, string distribution, string name) =>
        value is > 0
            ? value.Value
            : throw new InvalidOperationException($"Distribution '{distribution}' requires a positive '{name}'.");
}
