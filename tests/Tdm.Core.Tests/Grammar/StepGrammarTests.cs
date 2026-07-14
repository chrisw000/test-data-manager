using AwesomeAssertions;
using Tdm.Core.Grammar;
using Xunit;

namespace Tdm.Core.Tests.Grammar;

public class StepGrammarTests
{
    private static StepPlan Parse(string text, IReadOnlyList<IReadOnlyList<string>>? table = null) =>
        StepGrammar.Parse("Given", text, table, 1);

    private static T ParseAs<T>(string text, IReadOnlyList<IReadOnlyList<string>>? table = null) where T : StepPlan =>
        Parse(text, table).Should().BeOfType<T>().Subject;

    [Fact]
    public void CreateSingle_NoOverrides()
    {
        var step = ParseAs<CreateStep>("a Customer exists");
        step.Entity.Should().Be("Customer");
        step.Domain.Should().BeNull();
        step.Count.Should().Be(1);
        step.Overrides.Should().BeEmpty();
    }

    [Fact]
    public void CreateSingle_WithOverrides_PreservesOrder()
    {
        var step = ParseAs<CreateStep>("an Order exists with status \"Pending\" and total \"9.99\"");
        step.Overrides.Select(o => o.Name).Should().Equal("status", "total");
        step.Overrides.Select(o => o.RawValue).Should().Equal("Pending", "9.99");
    }

    [Fact]
    public void CreateSingle_DomainQualified()
    {
        var step = ParseAs<CreateStep>("a Billing Customer exists with name \"X\"");
        step.Domain.Should().Be("Billing");
        step.Entity.Should().Be("Customer");
    }

    [Fact]
    public void CreateSingle_MultiWordPropertyName()
    {
        var step = ParseAs<CreateStep>("a Customer exists with credit limit \"25000\"");
        step.Overrides.Should().ContainSingle().Which.Name.Should().Be("credit limit");
    }

    [Fact]
    public void CreateSingle_WithReference()
    {
        var step = ParseAs<CreateStep>("an Order exists for Customer \"Acme Ltd\" with status \"Pending\"");
        var reference = step.References.Should().ContainSingle().Subject;
        reference.Entity.Should().Be("Customer");
        reference.Key.Should().Be("Acme Ltd");
        step.Overrides.Should().ContainSingle().Which.Name.Should().Be("status");
    }

    [Fact]
    public void CreateSingle_MultipleReferences()
    {
        var step = ParseAs<CreateStep>("an Invoice exists for Account \"A1\" for Customer \"C1\" with amount \"5\"");
        step.References.Should().HaveCount(2);
        step.References.Select(r => r.Entity).Should().Equal("Account", "Customer");
    }

    [Fact]
    public void CreateCount_Bulk()
    {
        var step = ParseAs<CreateStep>("500 Products exist with category \"Widgets\"");
        step.Count.Should().Be(500);
        step.Entity.Should().Be("Products");
        step.Overrides.Should().ContainSingle().Which.Name.Should().Be("category");
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
        var step = ParseAs<CreateStep>("the following Products exist:", table);
        step.Rows.Should().HaveCount(2);
        step.Rows![0].Should().Equal(new PropertyAssignment("Sku", "A-1"), new PropertyAssignment("Price", "9.99"));
    }

    [Fact]
    public void CreateTable_SharedReferenceAndDefaults_ApplyToEveryRow()
    {
        var table = new List<IReadOnlyList<string>>
        {
            new List<string> { "InvoiceNumber" },
            new List<string> { "INV-1" },
        };
        var step = ParseAs<CreateStep>("the following Invoices exist for Account \"A1\" with currency \"GBP\":", table);
        var reference = step.References.Should().ContainSingle().Subject;
        (reference.Entity, reference.Key).Should().Be(("Account", "A1"));
        // Shared defaults come first so per-row cells win when applied in order.
        step.Rows![0].Select(a => a.Name).Should().Equal("currency", "InvoiceNumber");
    }

    [Fact]
    public void Update_KeyAndOverrides()
    {
        var step = ParseAs<UpdateStep>("the Customer \"Acme Ltd\" is updated with tier \"Platinum\"");
        step.Key.Should().Be("Acme Ltd");
        step.Overrides.Should().ContainSingle().Which.Name.Should().Be("tier");
    }

    [Fact]
    public void DeleteSingle()
    {
        var step = ParseAs<DeleteStep>("the Product \"SKU-1\" is deleted");
        step.Key.Should().Be("SKU-1");
        step.All.Should().BeFalse();
    }

    [Fact]
    public void DeleteAll_WithFilter()
    {
        var step = ParseAs<DeleteStep>("all Orders with status \"Draft\" are deleted");
        step.All.Should().BeTrue();
        step.Filter.Should().Equal(new PropertyAssignment("status", "Draft"));
    }

    [Fact]
    public void DeleteAll_Unfiltered()
    {
        var step = ParseAs<DeleteStep>("all Orders are deleted");
        step.All.Should().BeTrue();
        step.Filter.Should().BeEmpty();
    }

    [Fact]
    public void LoadSingle_WithExpectedProps()
    {
        var step = ParseAs<LoadStep>("a Customer \"Acme Ltd\" should exist with tier \"Gold\"");
        step.Key.Should().Be("Acme Ltd");
        step.Expected.Should().Equal(new PropertyAssignment("tier", "Gold"));
    }

    // Regression: this once matched the create-count pattern with entity "should".
    [Fact]
    public void LoadCount_NotMistakenForCreate()
    {
        var step = ParseAs<LoadStep>("3 Orders should exist with status \"Pending\"");
        step.ExpectedCount.Should().Be(3);
        step.Entity.Should().Be("Orders");
    }

    [Fact]
    public void ExternalReference()
    {
        var step = ParseAs<ExternalReferenceStep>("an external Customer reference \"Acme Ltd\" from CRM");
        (step.Entity, step.Key, step.SourceDomain).Should().Be(("Customer", "Acme Ltd", "CRM"));
    }

    [Theory]
    [InlineData("the quick brown fox")]
    [InlineData("a Customer is happy")]
    [InlineData("nothing matches here")]
    public void Gibberish_IsUnmatched(string text) => Parse(text).Should().BeOfType<UnmatchedStep>();

    [Fact]
    public void ParseAssignments_CommaSeparated()
    {
        var bag = StepGrammar.ParseAssignments("name \"A\", tier \"B\" and email \"c@d.e\"");
        bag.Select(a => a.Name).Should().Equal("name", "tier", "email");
    }
}
