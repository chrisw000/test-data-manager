using AwesomeAssertions;
using Tdm.Core.Grammar;
using Tdm.Core.Settings;
using Xunit;

namespace Tdm.Core.Tests.Grammar;

public class GherkinPlanParserTests
{
    private static FeaturePlan Parse(string text) => new GherkinPlanParser().ParseText(text);

    [Fact]
    public void Background_StepsPrependedToEveryScenario()
    {
        var plan = Parse("""
            Feature: F
              Background:
                Given a Customer exists with name "Base"
              Scenario: One
                Given a Product exists
              Scenario: Two
                Given an Order exists
            """);
        plan.Scenarios.Should().HaveCount(2);
        plan.Scenarios.Should().AllSatisfy(s =>
            s.Steps[0].Should().BeOfType<CreateStep>().Which.Entity.Should().Be("Customer"));
        plan.Scenarios[0].Steps.Should().HaveCount(2);
    }

    [Fact]
    public void Rule_ScenariosCollected_WithRuleTagsAndBackground()
    {
        var plan = Parse("""
            Feature: F
              Background:
                Given a Customer exists
              @ruleTag
              Rule: R
                Background:
                  Given a Product exists
                Scenario: InRule
                  Given an Order exists
            """);
        var scenario = plan.Scenarios.Should().ContainSingle().Subject;
        scenario.Tags.Should().Contain("@ruleTag");
        scenario.Steps.Should().HaveCount(3); // feature bg + rule bg + own step
    }

    [Fact]
    public void ScenarioOutline_ExpandsPerExampleRow_WithSubstitution()
    {
        var plan = Parse("""
            Feature: F
              Scenario Outline: Customer <name>
                Given a Customer exists with name "<name>" and tier "<tier>"
              Examples:
                | name  | tier |
                | Acme  | Gold |
                | Beta  | Silver |
            """);
        plan.Scenarios.Should().HaveCount(2);
        plan.Scenarios[0].Name.Should().Be("Customer Acme");
        var step = plan.Scenarios[1].Steps[0].Should().BeOfType<CreateStep>().Subject;
        step.Overrides.Select(o => o.RawValue).Should().Equal("Beta", "Silver");
    }

    [Fact]
    public void ScenarioOutline_SubstitutesIntoDataTableCells()
    {
        var plan = Parse("""
            Feature: F
              Scenario Outline: O
                Given the following Products exist:
                  | Sku    | Category |
                  | <sku>  | Bulk     |
              Examples:
                | sku  |
                | S-1  |
            """);
        var scenario = plan.Scenarios.Should().ContainSingle().Subject;
        var step = scenario.Steps[0].Should().BeOfType<CreateStep>().Subject;
        step.Rows![0][0].RawValue.Should().Be("S-1");
    }

    [Fact]
    public void FeatureTags_InheritedByScenarios()
    {
        var plan = Parse("""
            @seed:42 @domain:Billing
            Feature: F
              @benchmark
              Scenario: S
                Given a Customer exists
            """);
        var scenario = plan.Scenarios.Should().ContainSingle().Subject;
        scenario.Seed.Should().Be(42);
        scenario.DomainPin.Should().Be("Billing");
        scenario.ForceBenchmark.Should().BeTrue();
    }

    [Fact]
    public void Tags_SkipAndLifecycle()
    {
        var plan = Parse("""
            Feature: F
              @skip @persistent
              Scenario: A
                Given a Customer exists
              @ephemeral
              Scenario: B
                Given a Customer exists
            """);
        plan.Scenarios[0].Skip.Should().BeTrue();
        plan.Scenarios[0].LifecycleOverride.Should().Be(LifecycleMode.Persistent);
        plan.Scenarios[1].LifecycleOverride.Should().Be(LifecycleMode.TrackedTeardown);
        Parse("Feature: F\n  Scenario: S\n    Given a Customer exists")
            .Scenarios[0].LifecycleOverride.Should().BeNull();
    }

    [Fact]
    public void NoSeedTag_SeedIsNull() =>
        Parse("Feature: F\n  Scenario: S\n    Given a Customer exists")
            .Scenarios[0].Seed.Should().BeNull();

    [Fact]
    public void StepLineNumbers_Recorded() =>
        Parse("Feature: F\n  Scenario: S\n    Given a Customer exists")
            .Scenarios[0].Steps[0].Line.Should().Be(3);
}
