using AwesomeAssertions;
using Bogus;
using Tdm.Core.Generation;
using Tdm.Core.Manifest;
using Tdm.Core.Settings;
using Tdm.Core.Tests.Execution;
using Xunit;

namespace Tdm.Core.Tests.Generation;

public sealed class StatTarget
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public decimal Total { get; set; }
    public string City { get; set; } = "";
    public string Postcode { get; set; } = "";
}

public class StatisticalGeneratorTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"tdm-stat-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }

    private TdmSettings Settings(Action<EntitySettings> configure)
    {
        var entity = new EntitySettings();
        configure(entity);
        return new TdmSettings
        {
            BaseDirectory = _directory,
            Entities = { ["StatTarget"] = entity },
            Datasets = { ["ukCities"] = new DatasetSettings { Path = "uk-cities.csv" } },
        };
    }

    private static readonly Tdm.Core.Execution.EntityDescriptor Descriptor =
        FakeDomainRuntime.Describe<StatTarget>("StatTarget", "D");

    [Fact]
    public void AppliesWeightsAndDistributions_OverTheInstance()
    {
        var settings = Settings(e =>
        {
            e.Properties["Status"] = new PropertyGenerationSettings
            {
                Weights = new Dictionary<string, double> { ["Gold"] = 1 },
            };
            // Tolerant name matching, like everywhere else in TDM: "total" → Total.
            e.Properties["total"] = new PropertyGenerationSettings
            {
                Distribution = "lognormal", Mean = 120, Sigma = 1.2,
            };
        });
        var generator = new StatisticalGenerator(settings);
        var target = new StatTarget { Status = "faker-output", Total = -1 };

        var applied = generator.Apply(Descriptor, target, new Randomizer(5));

        applied.Distributions.Should().BeTrue();
        applied.Datasets.Should().BeFalse();
        target.Status.Should().Be("Gold");
        target.Total.Should().BePositive();
    }

    [Fact]
    public void DatasetColumns_ComeFromOneSampledRow_TuplesStayCorrelated()
    {
        File.WriteAllText(Path.Combine(_directory, "uk-cities.csv"), """
            city,postcode
            London,E1 6AN
            Leeds,LS1 4AP
            "York, Old","YO1 7HH"
            """);
        var settings = Settings(e =>
        {
            e.Properties["City"] = new PropertyGenerationSettings { Dataset = "ukCities", Column = "city" };
            e.Properties["Postcode"] = new PropertyGenerationSettings { Dataset = "ukCities", Column = "postcode" };
        });
        var generator = new StatisticalGenerator(settings);
        var pairs = new Dictionary<string, string>
        {
            ["London"] = "E1 6AN", ["Leeds"] = "LS1 4AP", ["York, Old"] = "YO1 7HH",
        };

        var random = new Randomizer(9);
        var seen = new HashSet<string>();
        for (var i = 0; i < 200; i++)
        {
            var target = new StatTarget();
            generator.Apply(Descriptor, target, random).Datasets.Should().BeTrue();
            // The tuple holds: postcode always belongs to the sampled city (incl. the
            // quoted "York, Old" row — commas inside quotes parse per RFC 4180).
            pairs[target.City].Should().Be(target.Postcode);
            seen.Add(target.City);
        }
        seen.Count.Should().BeGreaterThan(1, "row sampling should cover the dataset");

        // Same seed ⇒ same row sequence.
        var first = Sequence(generator, seed: 11);
        first.Should().Equal(Sequence(generator, seed: 11));

        static List<string> Sequence(StatisticalGenerator generator, int seed)
        {
            var random = new Randomizer(seed);
            return [.. Enumerable.Range(0, 50).Select(_ =>
            {
                var target = new StatTarget();
                generator.Apply(Descriptor, target, random);
                return target.City;
            })];
        }
    }

    [Fact]
    public void Misconfiguration_FailsLoudly()
    {
        var unknownProperty = new StatisticalGenerator(Settings(e =>
            e.Properties["ShoeSize"] = new PropertyGenerationSettings { Distribution = "normal", Mean = 1, Sigma = 1 }));
        FluentActions.Invoking(() => unknownProperty.Apply(Descriptor, new StatTarget(), new Randomizer(1)))
            .Should().Throw<InvalidOperationException>().WithMessage("*'ShoeSize' matches no writable property*");

        var nonNumeric = new StatisticalGenerator(Settings(e =>
            e.Properties["Name"] = new PropertyGenerationSettings { Distribution = "normal", Mean = 1, Sigma = 1 }));
        FluentActions.Invoking(() => nonNumeric.Apply(Descriptor, new StatTarget(), new Randomizer(1)))
            .Should().Throw<InvalidOperationException>().WithMessage("*numeric*use \"weights\"*");

        var unknownDataset = new StatisticalGenerator(Settings(e =>
            e.Properties["City"] = new PropertyGenerationSettings { Dataset = "nope" }));
        FluentActions.Invoking(() => unknownDataset.Apply(Descriptor, new StatTarget(), new Randomizer(1)))
            .Should().Throw<InvalidOperationException>().WithMessage("*Unknown dataset 'nope'*");
    }

    [Fact]
    public void EntitiesWithoutStatConfig_AreUntouched()
    {
        var generator = new StatisticalGenerator(new TdmSettings());
        var target = new StatTarget { Status = "as-generated" };
        generator.Apply(Descriptor, target, new Randomizer(1)).Any.Should().BeFalse();
        target.Status.Should().Be("as-generated");
    }

    [Fact]
    public void Attestation_RecordsTheNewSourceMarkers()
    {
        var manifest = new RunManifest
        {
            Scenarios =
            [
                new ScenarioManifest
                {
                    Entities =
                    [
                        new EntityManifest { FakerSource = "auto+plugin:Sku+distributions+datasets" },
                        new EntityManifest { FakerSource = "CustomerFaker+distributions" },
                    ],
                },
            ],
        };
        var attestation = AttestationBuilder.Build(manifest);
        attestation.SyntheticOnly.Should().BeTrue();
        attestation.Sources.Should().BeEquivalentTo(
            ["AutoFaker", "ConventionFaker", "DatasetPack", "Distribution", "GeneratorPlugin"]);
    }
}
