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
            Assert.Equal("t", settings.Run.Name);
            Assert.Equal(FailurePolicy.FailRun, settings.Run.FailurePolicy);
            Assert.Equal(9, settings.Run.DefaultSeed);
            Assert.Equal(PersistenceMode.DbContextOnly, Assert.Single(settings.Domains).Persistence);
            Assert.Equal("Sku", settings.EntityFor("Product").NaturalKey);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ApplyDefaults_MergesBuiltInProfiles_WithoutOverwritingConfigured()
    {
        var settings = new TdmSettings();
        settings.ConventionProfiles["modern"] = new ConventionProfile { EntityClassPattern = "{Name}Custom" };
        settings.ApplyDefaults();
        Assert.Equal("{Name}Custom", settings.ConventionProfiles["modern"].EntityClassPattern);
        Assert.Equal("{Name}Model", settings.ConventionProfiles["legacy"].EntityClassPattern);
    }

    [Fact]
    public void ProfileFor_UnknownProfile_ThrowsWithKnownProfiles()
    {
        var settings = new TdmSettings();
        settings.ApplyDefaults();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            settings.ProfileFor(new DomainSettings { Name = "X", ConventionProfile = "nope" }));
        Assert.Contains("nope", ex.Message);
        Assert.Contains("modern", ex.Message);
    }

    [Fact]
    public void EntityFor_Unconfigured_ReturnsDefault()
    {
        var settings = new TdmSettings();
        Assert.Same(EntitySettings.Default, settings.EntityFor("Anything"));
    }

    [Fact]
    public void ResolveConnectionString_InlineWins()
    {
        var domain = new DomainSettings { Name = "D", ConnectionString = "inline", ConnectionStringName = "IGNORED" };
        Assert.Equal("inline", domain.ResolveConnectionString());
    }

    [Fact]
    public void ResolveConnectionString_FromEnvironment()
    {
        Environment.SetEnvironmentVariable("TDM_CONNECTIONSTRINGS__TESTDB", "Data Source=env.db");
        try
        {
            var domain = new DomainSettings { Name = "D", ConnectionStringName = "TestDb" };
            Assert.Equal("Data Source=env.db", domain.ResolveConnectionString());
        }
        finally { Environment.SetEnvironmentVariable("TDM_CONNECTIONSTRINGS__TESTDB", null); }
    }

    [Fact]
    public void ResolveConnectionString_MissingEverywhere_Throws()
    {
        var domain = new DomainSettings { Name = "D", ConnectionStringName = "NoSuchName" };
        var ex = Assert.Throws<InvalidOperationException>(() => domain.ResolveConnectionString());
        Assert.Contains("NoSuchName", ex.Message);
    }

    [Fact]
    public void FindDomain_CaseInsensitive()
    {
        var settings = new TdmSettings { Domains = [new DomainSettings { Name = "Orders" }] };
        Assert.NotNull(settings.FindDomain("orders"));
        Assert.Null(settings.FindDomain("Billing"));
    }
}
