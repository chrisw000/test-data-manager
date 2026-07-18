using AwesomeAssertions;
using Xunit;

namespace Tdm.Lsp.Tests;

public class CompletionProviderTests
{
    private static List<CompletionItem> Complete(string lineText) =>
        CompletionProvider.Complete(lineText, 0, lineText.Length, TestModels.OrdersAndBilling());

    [Fact]
    public void EntityNames_AfterArticle()
    {
        var items = Complete("    Given a ");
        items.Select(i => i.Label).Should().Contain(["Customer", "Order", "Invoice"]);
        items.Should().Contain(i => i.Label == "Customer" && i.Detail!.Contains("Orders"));
        // Domain qualifiers complete too, for "a Billing Customer ..." steps.
        items.Select(i => i.Label).Should().Contain(["Orders", "Billing"]);
    }

    [Fact]
    public void EntityNames_AfterCount_AfterFollowing_AndAfterFor()
    {
        Complete("    Given 500 ").Select(i => i.Label).Should().Contain("Order");
        Complete("    Given the following ").Select(i => i.Label).Should().Contain("Invoice");
        Complete("    Given an Order exists for ").Select(i => i.Label).Should().Contain("Customer");
    }

    [Fact]
    public void Properties_InsideWithClause_ComeFromTheStepEntity()
    {
        var items = Complete("    Given an Order exists with ");
        items.Select(i => i.Label).Should().BeEquivalentTo(["OrderNumber", "Status"]);
        items.Should().OnlyContain(i => i.Detail!.Contains("on Order"));
    }

    [Fact]
    public void Properties_AfterAnd_AndForPartialWords()
    {
        Complete("    Given a Customer exists with name \"X\" and ")
            .Select(i => i.Label).Should().Contain("Tier");
        Complete("    Given a Customer exists with cre")
            .Select(i => i.Label).Should().Contain("CreditLimit");
    }

    [Fact]
    public void Properties_ForUpdateStep_EvenWhileIncomplete()
    {
        Complete("    When the Customer \"Acme Ltd\" is updated with ")
            .Select(i => i.Label).Should().Contain("Tier");
    }

    [Fact]
    public void TagVocabulary_AfterAt()
    {
        var items = CompletionProvider.Complete("@", 0, 1, TestModels.OrdersAndBilling());
        items.Select(i => i.Label).Should().Contain(["@seed:", "@domain:", "@skip", "@benchmark"]);
    }

    [Fact]
    public void DomainNames_AfterDomainTag()
    {
        var items = CompletionProvider.Complete("@domain:", 0, 8, TestModels.OrdersAndBilling());
        items.Select(i => i.Label).Should().BeEquivalentTo(["@domain:Orders", "@domain:Billing"]);
    }

    [Fact]
    public void NonStepLines_AndMissingModel_YieldNothing()
    {
        Complete("Feature: Orders").Should().BeEmpty();
        CompletionProvider.Complete("    Given a ", 0, 12, model: null).Should().BeEmpty();
    }
}
