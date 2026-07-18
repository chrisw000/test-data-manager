using AwesomeAssertions;
using Tdm.Core.Model;
using Tdm.Core.Tests.Execution;
using Xunit;

namespace Tdm.Core.Tests.Model;

public class TdmModelBuilderTests
{
    [Fact]
    public void Build_CapturesDomainsEntitiesPropertiesAndNaturalKeys()
    {
        var crm = new FakeDomainRuntime("CRM",
            FakeDomainRuntime.Describe<Widget>("Widget", "CRM"),
            FakeDomainRuntime.Describe<Gadget>("Gadget", "CRM"));

        var model = TdmModelBuilder.Build([crm], settingsFileSha256: "cafe01");

        model.ModelVersion.Should().Be(1);
        model.SettingsFileSha256.Should().Be("cafe01");
        var domain = model.Domains.Should().ContainSingle().Subject;
        domain.Name.Should().Be("CRM");

        var widget = domain.Entities.Single(e => e.Name == "Widget");
        widget.ClrType.Should().Contain("Widget");
        widget.NaturalKey.Should().Be("Name");
        widget.Properties.Select(p => (p.Name, p.Type)).Should().BeEquivalentTo(
        [
            ("Colour", "string"), ("Id", "Guid"), ("Name", "string"), ("Size", "int"),
        ]);
    }

    [Fact]
    public void Build_IsDeterministic_SortedAndCiDiffStable()
    {
        var runtimes = new[]
        {
            new FakeDomainRuntime("Zeta", FakeDomainRuntime.Describe<Widget>("Widget", "Zeta")),
            new FakeDomainRuntime("Alpha",
                FakeDomainRuntime.Describe<Gadget>("Gadget", "Alpha"),
                FakeDomainRuntime.Describe<Widget>("Widget", "Alpha")),
        };

        var model = TdmModelBuilder.Build(runtimes, null);
        model.Domains.Select(d => d.Name).Should().ContainInOrder("Alpha", "Zeta");
        model.Domains[0].Entities.Select(e => e.Name).Should().ContainInOrder("Gadget", "Widget");

        // Byte-identical across builds — the CI drift check regenerates and diffs (W4-D2).
        // No timestamps, no +commit build metadata.
        var first = model.Serialize();
        var second = TdmModelBuilder.Build(runtimes, null).Serialize();
        second.Should().Be(first);
        first.Should().NotContain("+");
        model.TdmVersion.Should().MatchRegex(@"^\d+\.\d+\.\d+$");
    }

    [Fact]
    public void SerializeAndLoad_RoundTrips()
    {
        var crm = new FakeDomainRuntime("CRM", FakeDomainRuntime.Describe<Widget>("Widget", "CRM"));
        var model = TdmModelBuilder.Build([crm], "aa");

        var path = Path.Combine(Path.GetTempPath(), $"tdm-model-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, model.Serialize());
            var loaded = TdmModel.Load(path);
            loaded.SettingsFileSha256.Should().Be("aa");
            loaded.Domains.Single().Entities.Single().Properties.Should().HaveCount(4);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
