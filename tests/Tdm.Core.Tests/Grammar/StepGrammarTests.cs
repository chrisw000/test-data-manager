using Tdm.Core.Grammar;
using Xunit;

namespace Tdm.Core.Tests.Grammar;

public class StepGrammarTests
{
    private static StepPlan Parse(string text, IReadOnlyList<IReadOnlyList<string>>? table = null) =>
        StepGrammar.Parse("Given", text, table, 1);

    [Fact]
    public void CreateSingle_NoOverrides()
    {
        var step = Assert.IsType<CreateStep>(Parse("a Customer exists"));
        Assert.Equal("Customer", step.Entity);
        Assert.Null(step.Domain);
        Assert.Equal(1, step.Count);
        Assert.Empty(step.Overrides);
    }

    [Fact]
    public void CreateSingle_WithOverrides_PreservesOrder()
    {
        var step = Assert.IsType<CreateStep>(Parse("an Order exists with status \"Pending\" and total \"9.99\""));
        Assert.Equal(["status", "total"], step.Overrides.Select(o => o.Name));
        Assert.Equal(["Pending", "9.99"], step.Overrides.Select(o => o.RawValue));
    }

    [Fact]
    public void CreateSingle_DomainQualified()
    {
        var step = Assert.IsType<CreateStep>(Parse("a Billing Customer exists with name \"X\""));
        Assert.Equal("Billing", step.Domain);
        Assert.Equal("Customer", step.Entity);
    }

    [Fact]
    public void CreateSingle_MultiWordPropertyName()
    {
        var step = Assert.IsType<CreateStep>(Parse("a Customer exists with credit limit \"25000\""));
        Assert.Equal("credit limit", Assert.Single(step.Overrides).Name);
    }

    [Fact]
    public void CreateSingle_WithReference()
    {
        var step = Assert.IsType<CreateStep>(Parse("an Order exists for Customer \"Acme Ltd\" with status \"Pending\""));
        var reference = Assert.Single(step.References);
        Assert.Equal("Customer", reference.Entity);
        Assert.Equal("Acme Ltd", reference.Key);
        Assert.Equal("status", Assert.Single(step.Overrides).Name);
    }

    [Fact]
    public void CreateSingle_MultipleReferences()
    {
        var step = Assert.IsType<CreateStep>(
            Parse("an Invoice exists for Account \"A1\" for Customer \"C1\" with amount \"5\""));
        Assert.Equal(2, step.References.Count);
        Assert.Equal(["Account", "Customer"], step.References.Select(r => r.Entity));
    }

    [Fact]
    public void CreateCount_Bulk()
    {
        var step = Assert.IsType<CreateStep>(Parse("500 Products exist with category \"Widgets\""));
        Assert.Equal(500, step.Count);
        Assert.Equal("Products", step.Entity);
        Assert.Equal("category", Assert.Single(step.Overrides).Name);
    }

    [Fact]
    public void CreateTable_RowsFromDataTable()
    {
        var table = new List<IReadOnlyList<string>>
        {
            new List<string> { "Sku", "Price" },
            new List<string> { "A-1", "9.99" },
            new List<string> { "B-2", "24.50" },
        };
        var step = Assert.IsType<CreateStep>(Parse("the following Products exist:", table));
        Assert.Equal(2, step.Rows!.Count);
        Assert.Equal([new PropertyAssignment("Sku", "A-1"), new PropertyAssignment("Price", "9.99")], step.Rows[0]);
    }

    [Fact]
    public void CreateTable_SharedReferenceAndDefaults_ApplyToEveryRow()
    {
        var table = new List<IReadOnlyList<string>>
        {
            new List<string> { "InvoiceNumber" },
            new List<string> { "INV-1" },
        };
        var step = Assert.IsType<CreateStep>(
            Parse("the following Invoices exist for Account \"A1\" with currency \"GBP\":", table));
        var reference = Assert.Single(step.References);
        Assert.Equal(("Account", "A1"), (reference.Entity, reference.Key));
        // Shared defaults come first so per-row cells win when applied in order.
        Assert.Equal(["currency", "InvoiceNumber"], step.Rows![0].Select(a => a.Name));
    }

    [Fact]
    public void Update_KeyAndOverrides()
    {
        var step = Assert.IsType<UpdateStep>(Parse("the Customer \"Acme Ltd\" is updated with tier \"Platinum\""));
        Assert.Equal("Acme Ltd", step.Key);
        Assert.Equal("tier", Assert.Single(step.Overrides).Name);
    }

    [Fact]
    public void DeleteSingle()
    {
        var step = Assert.IsType<DeleteStep>(Parse("the Product \"SKU-1\" is deleted"));
        Assert.Equal("SKU-1", step.Key);
        Assert.False(step.All);
    }

    [Fact]
    public void DeleteAll_WithFilter()
    {
        var step = Assert.IsType<DeleteStep>(Parse("all Orders with status \"Draft\" are deleted"));
        Assert.True(step.All);
        Assert.Equal([new PropertyAssignment("status", "Draft")], step.Filter);
    }

    [Fact]
    public void DeleteAll_Unfiltered()
    {
        var step = Assert.IsType<DeleteStep>(Parse("all Orders are deleted"));
        Assert.True(step.All);
        Assert.Empty(step.Filter);
    }

    [Fact]
    public void LoadSingle_WithExpectedProps()
    {
        var step = Assert.IsType<LoadStep>(Parse("a Customer \"Acme Ltd\" should exist with tier \"Gold\""));
        Assert.Equal("Acme Ltd", step.Key);
        Assert.Equal([new PropertyAssignment("tier", "Gold")], step.Expected);
    }

    // Regression: this once matched the create-count pattern with entity "should".
    [Fact]
    public void LoadCount_NotMistakenForCreate()
    {
        var step = Assert.IsType<LoadStep>(Parse("3 Orders should exist with status \"Pending\""));
        Assert.Equal(3, step.ExpectedCount);
        Assert.Equal("Orders", step.Entity);
    }

    [Fact]
    public void ExternalReference()
    {
        var step = Assert.IsType<ExternalReferenceStep>(Parse("an external Customer reference \"Acme Ltd\" from CRM"));
        Assert.Equal(("Customer", "Acme Ltd", "CRM"), (step.Entity, step.Key, step.SourceDomain));
    }

    [Theory]
    [InlineData("the quick brown fox")]
    [InlineData("a Customer is happy")]
    [InlineData("nothing matches here")]
    public void Gibberish_IsUnmatched(string text) => Assert.IsType<UnmatchedStep>(Parse(text));

    [Fact]
    public void ParseAssignments_CommaSeparated()
    {
        var bag = StepGrammar.ParseAssignments("name \"A\", tier \"B\" and email \"c@d.e\"");
        Assert.Equal(["name", "tier", "email"], bag.Select(a => a.Name));
    }
}
