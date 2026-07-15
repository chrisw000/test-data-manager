using AwesomeAssertions;
using Tdm.Core.Grammar;
using Tdm.Core.Registry;
using Xunit;

namespace Tdm.Policy.Tests;

public class KeyRegistryCheckerTests
{
    private static SeedingPlan Plan(string featureText) =>
        new() { Features = { new GherkinPlanParser().ParseText(featureText) } };

    private const string ExternalRefFeature = """
        Feature: F
          Scenario: S
            Given an external Customer reference "Acme Ltd" from Orders
        """;

    private static KeyRegistryDocument OrdersRegistry(List<string>? keys = null, string? pattern = null) => new()
    {
        Domain = "Orders",
        Entities = new Dictionary<string, EntityKeyRegistry>(StringComparer.OrdinalIgnoreCase)
        {
            ["Customer"] = new EntityKeyRegistry { NaturalKey = "Name", Keys = keys ?? [], KeyPattern = pattern },
        },
    };

    [Fact]
    public void KnownKey_NoViolation()
    {
        var registries = new Dictionary<string, KeyRegistryDocument> { ["Orders"] = OrdersRegistry(["Acme Ltd"]) };
        KeyRegistryChecker.Check(Plan(ExternalRefFeature), registries).Should().BeEmpty();
    }

    [Fact]
    public void UnknownKey_Violation()
    {
        var registries = new Dictionary<string, KeyRegistryDocument> { ["Orders"] = OrdersRegistry(["Globex Corp"]) };
        var violations = KeyRegistryChecker.Check(Plan(ExternalRefFeature), registries);
        violations.Should().ContainSingle().Which.Rule.Should().Be("KeyRegistry");
    }

    [Fact]
    public void KeyPatternMatch_NoViolation()
    {
        var registries = new Dictionary<string, KeyRegistryDocument> { ["Orders"] = OrdersRegistry(pattern: "^Acme.*$") };
        KeyRegistryChecker.Check(Plan(ExternalRefFeature), registries).Should().BeEmpty();
    }

    [Fact]
    public void UngovernedEntity_NoViolation()
    {
        var registry = new KeyRegistryDocument { Domain = "Orders" }; // no "Customer" entry at all
        var registries = new Dictionary<string, KeyRegistryDocument> { ["Orders"] = registry };
        KeyRegistryChecker.Check(Plan(ExternalRefFeature), registries).Should().BeEmpty();
    }

    [Fact]
    public void NoRegistryForDomain_NoViolation() =>
        KeyRegistryChecker.Check(Plan(ExternalRefFeature), new Dictionary<string, KeyRegistryDocument>()).Should().BeEmpty();

    [Fact]
    public void NonExternalReferenceSteps_Ignored()
    {
        var registries = new Dictionary<string, KeyRegistryDocument> { ["Orders"] = OrdersRegistry(["Globex Corp"]) };
        var plan = Plan("Feature: F\n  Scenario: S\n    Given a Customer exists with name \"Anything\"");
        KeyRegistryChecker.Check(plan, registries).Should().BeEmpty();
    }
}
