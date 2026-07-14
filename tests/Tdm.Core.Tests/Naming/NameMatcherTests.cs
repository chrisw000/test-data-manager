using AwesomeAssertions;
using Tdm.Core.Naming;
using Xunit;

namespace Tdm.Core.Tests.Naming;

public class NameMatcherTests
{
    // ── Normalize ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Order Line", "orderline")]
    [InlineData("order_line", "orderline")]
    [InlineData("order-line", "orderline")]
    [InlineData("OrderLine", "orderline")]
    [InlineData("ORDERLINE", "orderline")]
    [InlineData("Customer", "customer")]
    [InlineData("  Spaces  ", "spaces")]
    public void Normalize_StripsAndLowers(string input, string expected) =>
        NameMatcher.Normalize(input).Should().Be(expected);

    // ── Singularize ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Customers", "Customer")]
    [InlineData("Products", "Product")]
    [InlineData("Orders", "Order")]
    [InlineData("Customer", "Customer")]   // already singular — unchanged
    public void Singularize_PluralsAndSingulars(string input, string expected) =>
        NameMatcher.Singularize(input).Should().Be(expected);

    // ── StripPattern ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("CustomerEntity", "{Name}Entity", "Customer")]
    [InlineData("ProductEntity", "{Name}Entity", "Product")]
    [InlineData("OrderEntity", "{Name}Entity", "Order")]
    [InlineData("CustomerModel", "{Name}Model", "Customer")]
    [InlineData("InvoiceModel", "{Name}Model", "Invoice")]
    [InlineData("Customer", "{Name}", "Customer")]
    [InlineData("SomeClass", "{Name}Entity", "SomeClass")]   // pattern doesn't match — return as-is
    public void StripPattern_VariousPatterns(string clrTypeName, string pattern, string expected) =>
        NameMatcher.StripPattern(clrTypeName, pattern).Should().Be(expected);

    [Fact]
    public void StripPattern_NoPlaceholder_ReturnsInputUnchanged() =>
        NameMatcher.StripPattern("CustomerEntity", "NoPlaceholderHere").Should().Be("CustomerEntity");

    // ── Matches ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Customer", "Customer", true)]
    [InlineData("CUSTOMER", "Customer", true)]
    [InlineData("customer", "Customer", true)]
    [InlineData("Customers", "Customer", true)]    // plural tolerance
    [InlineData("customers", "Customer", true)]   // plural + lower
    [InlineData("order_line", "orderline", true)]  // underscore stripped
    [InlineData("Order Line", "orderline", true)]  // space stripped
    [InlineData("Invoice", "Customer", false)]
    public void Matches_PluralCaseSpaceTolerant(string gherkin, string logical, bool expected) =>
        NameMatcher.Matches(gherkin, logical).Should().Be(expected);

    // ── Expand ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("I{Name}Repository", "Customer", "ICustomerRepository")]
    [InlineData("{Name}Faker", "Product", "ProductFaker")]
    [InlineData("{Name}Entity", "Order", "OrderEntity")]
    [InlineData("{Name}", "Widget", "Widget")]
    public void Expand_ReplacesPlaceholder(string pattern, string logicalName, string expected) =>
        NameMatcher.Expand(pattern, logicalName).Should().Be(expected);
}
