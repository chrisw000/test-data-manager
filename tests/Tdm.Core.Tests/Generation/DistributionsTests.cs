using AwesomeAssertions;
using Bogus;
using Tdm.Core.Generation;
using Tdm.Core.Settings;
using Xunit;

namespace Tdm.Core.Tests.Generation;

public class DistributionsTests
{
    [Fact]
    public void Weighted_10kDraws_LandWithin2PercentOfConfiguredWeights()
    {
        var weights = new Dictionary<string, double> { ["Pending"] = 0.6, ["Shipped"] = 0.3, ["Cancelled"] = 0.1 };
        var random = new Randomizer(42);

        var counts = new Dictionary<string, int> { ["Pending"] = 0, ["Shipped"] = 0, ["Cancelled"] = 0 };
        for (var i = 0; i < 10_000; i++)
            counts[Distributions.SampleWeighted(weights, random)]++;

        (counts["Pending"] / 10_000d).Should().BeApproximately(0.6, 0.02);
        (counts["Shipped"] / 10_000d).Should().BeApproximately(0.3, 0.02);
        (counts["Cancelled"] / 10_000d).Should().BeApproximately(0.1, 0.02);
    }

    [Fact]
    public void Weighted_SameSeed_SameSequence_EvenWithReorderedConfig()
    {
        var a = new Dictionary<string, double> { ["A"] = 0.5, ["B"] = 0.3, ["C"] = 0.2 };
        var reordered = new Dictionary<string, double> { ["C"] = 0.2, ["A"] = 0.5, ["B"] = 0.3 };

        var first = Draw(a, seed: 7, count: 200);
        first.Should().Equal(Draw(a, seed: 7, count: 200));
        // Keys are ordinal-sorted at sample time — config file order must not change draws.
        first.Should().Equal(Draw(reordered, seed: 7, count: 200));

        static List<string> Draw(Dictionary<string, double> weights, int seed, int count)
        {
            var random = new Randomizer(seed);
            return [.. Enumerable.Range(0, count).Select(_ => Distributions.SampleWeighted(weights, random))];
        }
    }

    [Fact]
    public void Normal_MatchesConfiguredMoments()
    {
        var config = new PropertyGenerationSettings { Distribution = "normal", Mean = 100, Sigma = 10 };
        var random = new Randomizer(1);
        var samples = Enumerable.Range(0, 10_000).Select(_ => Distributions.Sample(config, random)).ToList();

        samples.Average().Should().BeApproximately(100, 1);
        Math.Sqrt(samples.Average(s => Math.Pow(s - 100, 2))).Should().BeApproximately(10, 1);
    }

    [Fact]
    public void Lognormal_MeanIsTheMedian_AllPositive()
    {
        // "mean": 120 reads as the scale: half of draws below 120, long right tail.
        var config = new PropertyGenerationSettings { Distribution = "lognormal", Mean = 120, Sigma = 1.2 };
        var random = new Randomizer(1);
        var samples = Enumerable.Range(0, 10_000).Select(_ => Distributions.Sample(config, random)).OrderBy(s => s).ToList();

        samples.Should().OnlyContain(s => s > 0);
        samples[5_000].Should().BeApproximately(120, 12); // median within 10%
        samples.Average().Should().BeGreaterThan(samples[5_000]); // right skew
    }

    [Fact]
    public void Uniform_StaysInBounds_Exponential_MatchesMean()
    {
        var random = new Randomizer(1);
        var uniform = new PropertyGenerationSettings { Distribution = "uniform", Min = 5, Max = 10 };
        Enumerable.Range(0, 1_000).Select(_ => Distributions.Sample(uniform, random))
            .Should().OnlyContain(s => s >= 5 && s <= 10);

        var exponential = new PropertyGenerationSettings { Distribution = "exponential", Mean = 40 };
        var samples = Enumerable.Range(0, 10_000).Select(_ => Distributions.Sample(exponential, random)).ToList();
        samples.Should().OnlyContain(s => s >= 0);
        samples.Average().Should().BeApproximately(40, 4);
    }

    [Fact]
    public void MinMax_ClampNonUniformDistributions()
    {
        var config = new PropertyGenerationSettings { Distribution = "normal", Mean = 0, Sigma = 100, Min = 0, Max = 50 };
        var random = new Randomizer(1);
        Enumerable.Range(0, 1_000).Select(_ => Distributions.Sample(config, random))
            .Should().OnlyContain(s => s >= 0 && s <= 50);
    }

    [Fact]
    public void Misconfiguration_FailsWithActionableMessages()
    {
        var random = new Randomizer(1);
        FluentActions.Invoking(() => Distributions.Sample(
                new PropertyGenerationSettings { Distribution = "pareto" }, random))
            .Should().Throw<InvalidOperationException>().WithMessage("*Unknown distribution 'pareto'*normal, lognormal, uniform, exponential*");
        FluentActions.Invoking(() => Distributions.Sample(
                new PropertyGenerationSettings { Distribution = "normal", Mean = 5 }, random))
            .Should().Throw<InvalidOperationException>().WithMessage("*requires 'sigma'*");
        FluentActions.Invoking(() => Distributions.SampleWeighted(new Dictionary<string, double>(), random))
            .Should().Throw<InvalidOperationException>().WithMessage("*at least one*");
        FluentActions.Invoking(() => Distributions.SampleWeighted(
                new Dictionary<string, double> { ["A"] = 0 }, random))
            .Should().Throw<InvalidOperationException>().WithMessage("*positive*");
    }
}
