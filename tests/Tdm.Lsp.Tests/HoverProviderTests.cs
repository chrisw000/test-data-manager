using AwesomeAssertions;
using Xunit;

namespace Tdm.Lsp.Tests;

public class HoverProviderTests
{
    private static string? Hover(string lineText) =>
        HoverProvider.Hover(lineText, TestModels.OrdersAndBilling());

    [Fact]
    public void CreateStep_ShowsVerbDoc_AndEntityFacts()
    {
        var markdown = Hover("    Given a Customer exists with name \"Acme Ltd\"");
        markdown.Should().Contain("**Create**").And.Contain("```gherkin");
        markdown.Should().Contain("Orders.Customer").And.Contain("natural key: `Name`");
    }

    [Fact]
    public void CountBulk_TableBulk_Update_Delete_Verify_AllHaveDocs()
    {
        Hover("Given 500 Orders exist").Should().Contain("count bulk");
        Hover("Given the following Invoices exist:").Should().Contain("DataTable bulk");
        Hover("When the Customer \"X\" is updated with tier \"Gold\"").Should().Contain("**Update**");
        Hover("When the Order \"O-1\" is deleted").Should().Contain("**Delete**");
        Hover("When all Invoices with status \"Draft\" are deleted").Should().Contain("filtered/all");
        Hover("Then an Order \"O-1\" should exist").Should().Contain("**Verify**");
        Hover("Then 2 Orders should exist").Should().Contain("Verify (count)");
    }

    [Fact]
    public void ExternalReference_ExplainsTheIdentityContract_WithThisStepsTriple()
    {
        var markdown = Hover("Given an external Customer reference \"Acme Ltd\" from Orders");
        markdown.Should().Contain("identity contract").And.Contain("UUIDv5");
        markdown.Should().Contain("Orders|Customer|Acme Ltd");
    }

    [Fact]
    public void UnmatchedStep_ListsTheVerbs()
    {
        Hover("Given the moon is full").Should().Contain("No TDM grammar rule")
            .And.Contain("tdm explain");
    }

    [Fact]
    public void NonStepLines_ReturnNull()
    {
        Hover("Feature: Orders").Should().BeNull();
        Hover("  | InvoiceNumber | Amount |").Should().BeNull();
        Hover("").Should().BeNull();
    }
}
