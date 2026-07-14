using AwesomeAssertions;
using Tdm.Core.Settings;
using Xunit;

namespace Tdm.Core.Tests.Settings;

public class TdmSettingsTests
{
    [Fact]
    public void Load_JsonWithCommentsAndTrailingCommas()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tdm-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {
              // a comment
              "run": { "name": "t", "failurePolicy": "FailRun", "defaultSeed": 9, },
              "domains": [ { "name": "Orders", "provider": "Sqlite", "persistence": "DbContextOnly", } ],
              "entities": { "Product": { "naturalKey": "Sku" } }
            }
            """);
        try
        {
            var settings = TdmSettings.Load(path);
            settings.Run.Name.Should().Be("t");
            settings.Run.FailurePolicy.Should().Be(FailurePolicy.FailRun);
            settings.Run.DefaultSeed.Should().Be(9);
            settings.Domains.Should().ContainSingle().Which.Persistence.Should().Be(PersistenceMode.DbContextOnly);
            settings.EntityFor("Product").NaturalKey.Should().Be("Sku");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ApplyDefaults_MergesBuiltInProfiles_WithoutOverwritingConfigured()
    {
        var settings = new TdmSettings();
        settings.ConventionProfiles["modern"] = new ConventionProfile { EntityClassPattern = "{Name}Custom" };
        settings.ApplyDefaults();
        settings.ConventionProfiles["modern"].EntityClassPattern.Should().Be("{Name}Custom");
        settings.ConventionProfiles["legacy"].EntityClassPattern.Should().Be("{Name}Model");
    }

    [Fact]
    public void ProfileFor_UnknownProfile_ThrowsWithKnownProfiles()
    {
        var settings = new TdmSettings();
        settings.ApplyDefaults();
        FluentActions.Invoking(() =>
                settings.ProfileFor(new DomainSettings { Name = "X", ConventionProfile = "nope" }))
            .Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("nope").And.Contain("modern");
    }

    [Fact]
    public void EntityFor_Unconfigured_ReturnsDefault() =>
        new TdmSettings().EntityFor("Anything").Should().BeSameAs(EntitySettings.Default);

    [Fact]
    public void ResolveConnectionString_InlineWins() =>
        new DomainSettings { Name = "D", ConnectionString = "inline", ConnectionStringName = "IGNORED" }
            .ResolveConnectionString().Should().Be("inline");

    [Fact]
    public void ResolveConnectionString_FromEnvironment()
    {
        Environment.SetEnvironmentVariable("TDM_CONNECTIONSTRINGS__TESTDB", "Data Source=env.db");
        try
        {
            new DomainSettings { Name = "D", ConnectionStringName = "TestDb" }
                .ResolveConnectionString().Should().Be("Data Source=env.db");
        }
        finally { Environment.SetEnvironmentVariable("TDM_CONNECTIONSTRINGS__TESTDB", null); }
    }

    [Fact]
    public void ResolveConnectionString_MissingEverywhere_Throws() =>
        FluentActions.Invoking(() =>
                new DomainSettings { Name = "D", ConnectionStringName = "NoSuchName" }.ResolveConnectionString())
            .Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("NoSuchName");

    [Fact]
    public void FindDomain_CaseInsensitive()
    {
        var settings = new TdmSettings { Domains = [new DomainSettings { Name = "Orders" }] };
        settings.FindDomain("orders").Should().NotBeNull();
        settings.FindDomain("Billing").Should().BeNull();
    }
}
