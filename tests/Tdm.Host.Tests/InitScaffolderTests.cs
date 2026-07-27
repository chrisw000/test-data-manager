using AwesomeAssertions;
using Tdm.Host;
using Xunit;

namespace Tdm.Host.Tests;

/// <summary>
/// W5-P6: `tdm init --agents` scaffolds the embedded agent-kit with the domain name
/// substituted for the templates' placeholders. These tests assert content and
/// substitution (the acceptance bar for the flag).
/// </summary>
public sealed class InitScaffolderTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "tdm-init-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void ScaffoldAgentKit_writes_AGENTS_and_all_four_skills()
    {
        var written = InitScaffolder.ScaffoldAgentKit(_dir, "Orders");

        File.Exists(Path.Combine(_dir, "AGENTS.md")).Should().BeTrue();
        File.Exists(Path.Combine(_dir, "VERSION")).Should().BeTrue();
        foreach (var skill in new[]
                 {
                     "tdm-feature-author", "tdm-run-triage",
                     "tdm-perf-analyst", "tdm-domain-onboarding",
                 })
        {
            var path = Path.Combine(_dir, "skills", skill, "SKILL.md");
            File.Exists(path).Should().BeTrue($"skill '{skill}' should be scaffolded");
            // Each skill carries YAML front matter naming itself — the convention runners read.
            var text = File.ReadAllText(path);
            text.Should().StartWith("---");
            text.Should().Contain($"name: {skill}");
        }

        written.Should().Contain(p => p.EndsWith("AGENTS.md"));
        written.Should().HaveCountGreaterThanOrEqualTo(6); // AGENTS + VERSION + 4 skills
    }

    [Fact]
    public void ScaffoldAgentKit_substitutes_the_domain_name_for_the_placeholder()
    {
        InitScaffolder.ScaffoldAgentKit(_dir, "Fulfilment");

        var agents = File.ReadAllText(Path.Combine(_dir, "AGENTS.md"));
        agents.Should().Contain("Fulfilment");
        agents.Should().NotContain(InitScaffolder.DomainPlaceholder);
        agents.Should().NotContain(InitScaffolder.DomainPlaceholder.ToLowerInvariant());
    }

    [Fact]
    public void ScaffoldAgentKit_does_not_ship_the_maintenance_readme()
    {
        InitScaffolder.ScaffoldAgentKit(_dir, "Orders");

        // agent-kit/README.md documents maintaining the source; it is excluded from the
        // embedded resources, so it must not land in a consuming repo.
        File.Exists(Path.Combine(_dir, "README.md")).Should().BeFalse();
    }

    [Fact]
    public void Substitute_replaces_both_casings()
    {
        var result = InitScaffolder.Substitute(
            "The YourDomain domain writes yourdomain.db", "Orders");

        result.Should().Be("The Orders domain writes orders.db");
    }

    [Fact]
    public void ScaffoldAgentKit_never_overwrites_existing_files()
    {
        Directory.CreateDirectory(_dir);
        var agentsPath = Path.Combine(_dir, "AGENTS.md");
        File.WriteAllText(agentsPath, "custom local content");

        InitScaffolder.ScaffoldAgentKit(_dir, "Orders");

        File.ReadAllText(agentsPath).Should().Be("custom local content");
    }
}
