using AwesomeAssertions;
using Tdm.Core.Manifest;
using Xunit;

namespace Tdm.Core.Tests.Manifest;

public class AttestationBuilderTests
{
    private static RunManifest ManifestWithEntities(params EntityManifest[] entities) => new()
    {
        Scenarios = [new ScenarioManifest { Feature = "F", Scenario = "S", Entities = [.. entities] }],
    };

    [Fact]
    public void Empty_Manifest_NoSources_StillSyntheticOnly()
    {
        var attestation = AttestationBuilder.Build(new RunManifest());
        attestation.SyntheticOnly.Should().BeTrue();
        attestation.Sources.Should().BeEmpty();
    }

    [Fact]
    public void AutoFaker_Classified()
    {
        var attestation = AttestationBuilder.Build(ManifestWithEntities(
            new EntityManifest { FakerSource = "auto" }));
        attestation.Sources.Should().Equal("AutoFaker");
    }

    [Fact]
    public void ConventionFaker_Classified()
    {
        var attestation = AttestationBuilder.Build(ManifestWithEntities(
            new EntityManifest { FakerSource = "CustomerFaker" }));
        attestation.Sources.Should().Equal("ConventionFaker");
    }

    [Fact]
    public void Overrides_Classified()
    {
        var attestation = AttestationBuilder.Build(ManifestWithEntities(
            new EntityManifest { FakerSource = "auto", OverridesApplied = ["Name"] }));
        attestation.Sources.Should().Equal("AutoFaker", "Override");
    }

    [Fact]
    public void DeterministicIdStrategy_ClassifiedAsIdentityContract()
    {
        var attestation = AttestationBuilder.Build(ManifestWithEntities(
            new EntityManifest { FakerSource = "auto", IdStrategy = "Deterministic" }));
        attestation.Sources.Should().Equal("AutoFaker", "IdentityContract");
    }

    [Fact]
    public void DbGeneratedIdStrategy_NotClassifiedAsIdentityContract()
    {
        var attestation = AttestationBuilder.Build(ManifestWithEntities(
            new EntityManifest { FakerSource = "auto", IdStrategy = "DbGenerated" }));
        attestation.Sources.Should().Equal("AutoFaker");
    }

    [Fact]
    public void AllFourSources_AcrossMultipleEntities_DeduplicatedAndSorted()
    {
        var attestation = AttestationBuilder.Build(ManifestWithEntities(
            new EntityManifest { FakerSource = "auto", IdStrategy = "Deterministic" },
            new EntityManifest { FakerSource = "CustomerFaker", OverridesApplied = ["Tier"] },
            new EntityManifest { FakerSource = "auto" })); // repeats AutoFaker — must not duplicate

        attestation.Sources.Should().Equal("AutoFaker", "ConventionFaker", "IdentityContract", "Override");
        attestation.SyntheticOnly.Should().BeTrue();
    }

    [Fact]
    public void NoFakerSource_NotClassified() =>
        AttestationBuilder.Build(ManifestWithEntities(new EntityManifest())).Sources.Should().BeEmpty();
}
