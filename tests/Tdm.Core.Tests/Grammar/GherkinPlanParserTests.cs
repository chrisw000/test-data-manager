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
        Assert.Equal(2, plan.Scenarios.Count);
        Assert.All(plan.Scenarios, s =>
            Assert.Equal("Customer", Assert.IsType<CreateStep>(s.Steps[0]).Entity));
        Assert.Equal(2, plan.Scenarios[0].Steps.Count);
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
        var scenario = Assert.Single(plan.Scenarios);
        Assert.Contains("@ruleTag", scenario.Tags);
        Assert.Equal(3, scenario.Steps.Count); // feature bg + rule bg + own step
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
        Assert.Equal(2, plan.Scenarios.Count);
        Assert.Equal("Customer Acme", plan.Scenarios[0].Name);
        var step = Assert.IsType<CreateStep>(plan.Scenarios[1].Steps[0]);
        Assert.Equal(["Beta", "Silver"], step.Overrides.Select(o => o.RawValue));
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
        var step = Assert.IsType<CreateStep>(Assert.Single(plan.Scenarios).Steps[0]);
        Assert.Equal("S-1", step.Rows![0][0].RawValue);
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
        var scenario = Assert.Single(plan.Scenarios);
        Assert.Equal(42, scenario.Seed);
        Assert.Equal("Billing", scenario.DomainPin);
        Assert.True(scenario.ForceBenchmark);
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
        Assert.True(plan.Scenarios[0].Skip);
        Assert.Equal(LifecycleMode.Persistent, plan.Scenarios[0].LifecycleOverride);
        Assert.Equal(LifecycleMode.TrackedTeardown, plan.Scenarios[1].LifecycleOverride);
        Assert.Null(Parse("Feature: F\n  Scenario: S\n    Given a Customer exists").Scenarios[0].LifecycleOverride);
    }

    [Fact]
    public void NoSeedTag_SeedIsNull()
    {
        var plan = Parse("Feature: F\n  Scenario: S\n    Given a Customer exists");
        Assert.Null(plan.Scenarios[0].Seed);
    }

    [Fact]
    public void StepLineNumbers_Recorded()
    {
        var plan = Parse("Feature: F\n  Scenario: S\n    Given a Customer exists");
        Assert.Equal(3, plan.Scenarios[0].Steps[0].Line);
    }
}
