using AwesomeAssertions;
using Tdm.Core.Model;
using Xunit;

namespace Tdm.Lsp.Tests;

public class FeatureLintTests
{
    private static List<LintDiagnostic> Analyze(string feature, TdmModel? model = null) =>
        FeatureLint.Analyze(feature, model ?? TestModels.OrdersAndBilling());

    [Fact]
    public void CleanFeature_NoDiagnostics()
    {
        var diagnostics = Analyze("""
            Feature: Orders
              Scenario: S
                Given a Customer exists with name "Acme Ltd" and tier "Gold"
                And an Order exists for Customer "Acme Ltd" with status "Pending"
                Then an Order "ORD-1" should exist with status "Pending"
                When the Customer "Acme Ltd" is updated with credit limit "9000"
                When all Orders with status "Draft" are deleted
            """);
        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void UnmatchedStep_Warns()
    {
        var diagnostics = Analyze("""
            Feature: F
              Scenario: S
                Given the moon is full
            """);
        var diagnostic = diagnostics.Should().ContainSingle().Subject;
        diagnostic.Severity.Should().Be(2);
        diagnostic.Line.Should().Be(2);
        diagnostic.Message.Should().Contain("no TDM grammar rule");
    }

    [Fact]
    public void UnknownEntity_Errors_AnchoredOnTheToken()
    {
        var diagnostics = Analyze("""
            Feature: F
              Scenario: S
                Given a Widget exists
            """);
        var diagnostic = diagnostics.Should().ContainSingle().Subject;
        diagnostic.Severity.Should().Be(1);
        diagnostic.Message.Should().Contain("Unknown entity 'Widget'").And.Contain("Customer");
        var line = "    Given a Widget exists";
        diagnostic.StartChar.Should().Be(line.IndexOf("Widget", StringComparison.Ordinal));
        diagnostic.EndChar.Should().Be(diagnostic.StartChar + "Widget".Length);
    }

    [Fact]
    public void EntityMatching_IsCaseAndPluralTolerant()
    {
        Analyze("""
            Feature: F
              Scenario: S
                Given 3 customers exist
                Then 3 Customers should exist
            """).Should().BeEmpty();
    }

    [Fact]
    public void UnknownProperty_Warns_ListingRealProperties()
    {
        var diagnostics = Analyze("""
            Feature: F
              Scenario: S
                Given a Customer exists with shoe size "44"
            """);
        var diagnostic = diagnostics.Should().ContainSingle().Subject;
        diagnostic.Severity.Should().Be(2);
        diagnostic.Message.Should().Contain("no property matching 'shoe size'")
            .And.Contain("Name, Tier, CreditLimit");
    }

    [Fact]
    public void PropertyMatching_IsSpaceAndCaseTolerant()
    {
        Analyze("""
            Feature: F
              Scenario: S
                Given a Customer exists with credit limit "9000"
            """).Should().BeEmpty();
    }

    [Fact]
    public void DomainPinnedStep_ValidatesWithinThatDomain()
    {
        var diagnostics = Analyze("""
            Feature: F
              Scenario: S
                Given a Billing Customer exists
            """);
        diagnostics.Should().ContainSingle().Which.Message
            .Should().Contain("Unknown entity 'Customer' in domain 'Billing'");
    }

    [Fact]
    public void DomainTag_PinsResolution_AndUnknownDomainTagWarns()
    {
        var diagnostics = Analyze("""
            Feature: F
              @domain:Billing
              Scenario: S
                Given a Customer exists
            """);
        diagnostics.Should().ContainSingle().Which.Message.Should().Contain("in domain 'Billing'");

        var tagDiagnostics = Analyze("""
            @domain:Nope
            Feature: F
              Scenario: S
                Given a Customer exists
            """);
        tagDiagnostics.Should().Contain(d => d.Message.Contains("Unknown domain 'Nope'") && d.Line == 0);
    }

    [Fact]
    public void AmbiguousEntity_Warns_WhenTwoDomainsMatch()
    {
        var model = TestModels.OrdersAndBilling();
        model.Domains[1].Entities.Add(new TdmModelEntity { Name = "Customer", ClrType = "Billing.CustomerModel" });

        var diagnostics = Analyze("""
            Feature: F
              Scenario: S
                Given a Customer exists
            """, model);
        diagnostics.Should().ContainSingle().Which.Message.Should().Contain("Orders, Billing");
    }

    [Fact]
    public void DataTableStep_ParsesWithItsTable_AndValidatesHeaderColumns()
    {
        var diagnostics = Analyze("""
            Feature: F
              Scenario: S
                Given the following Invoices exist:
                  | InvoiceNumber | Amout | Status |
                  | INV-1         | 10.00 | Draft  |
            """);
        var diagnostic = diagnostics.Should().ContainSingle().Subject;
        diagnostic.Message.Should().Contain("no property matching column 'Amout'");
        diagnostic.Line.Should().Be(3); // squiggle on the header line, not the step
    }

    [Fact]
    public void ScenarioOutlinePlaceholders_AreSkipped()
    {
        Analyze("""
            Feature: F
              Scenario Outline: S
                Given a <entity> exists with tier "<tier>"
                Examples:
                  | entity | tier |
                  | Widget | Gold |
            """).Should().BeEmpty();
    }

    [Fact]
    public void ExternalReference_UnknownDomain_IsAccepted_KnownDomainUnknownEntity_Warns()
    {
        // The owning domain may belong to another team — not locally modelled, not an error.
        Analyze("""
            Feature: F
              Scenario: S
                Given an external Customer reference "Acme Ltd" from CRM
            """).Should().BeEmpty();

        var diagnostics = Analyze("""
            Feature: F
              Scenario: S
                Given an external Widget reference "W1" from Billing
            """);
        diagnostics.Should().ContainSingle().Which.Message
            .Should().Contain("Domain 'Billing' has no entity matching 'Widget'");
    }

    [Fact]
    public void UnknownReferenceTarget_Errors()
    {
        var diagnostics = Analyze("""
            Feature: F
              Scenario: S
                Given an Order exists for Wombat "W1"
            """);
        diagnostics.Should().ContainSingle().Which.Message
            .Should().Contain("Reference target 'Wombat' matches no entity");
    }

    [Fact]
    public void WithoutModel_OnlyGrammarDiagnosticsAreProduced()
    {
        var diagnostics = FeatureLint.Analyze("""
            Feature: F
              Scenario: S
                Given a Widget exists
                And the moon is full
            """, model: null);
        diagnostics.Should().ContainSingle().Which.Message.Should().Contain("no TDM grammar rule");
    }
}
