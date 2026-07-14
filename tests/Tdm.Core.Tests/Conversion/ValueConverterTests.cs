using AwesomeAssertions;
using Tdm.Core.Conversion;
using Xunit;

namespace Tdm.Core.Tests.Conversion;

public class ValueConverterTests
{
    private enum Tier { Standard, Gold, Platinum }

    private static object? Convert(string raw, Type target)
    {
        var succeeded = ValueConverter.TryConvert(raw, target, out var value, out var error);
        succeeded.Should().BeTrue("conversion should succeed but failed with: {0}", error);
        return value;
    }

    [Fact] public void String_PassesThrough() => Convert("Acme Ltd", typeof(string)).Should().Be("Acme Ltd");
    [Fact] public void Int_Parses() => Convert("42", typeof(int)).Should().Be(42);
    [Fact] public void Decimal_ParsesInvariant() => Convert("1250.50", typeof(decimal)).Should().Be(1250.50m);
    [Fact] public void Bool_Parses() => Convert("true", typeof(bool)).Should().Be(true);
    [Fact] public void Guid_Parses() =>
        Convert("6fa1b3c4-0000-0000-0000-000000000001", typeof(Guid))
            .Should().Be(Guid.Parse("6fa1b3c4-0000-0000-0000-000000000001"));

    [Fact] public void Enum_ByName_CaseInsensitive() => Convert("gold", typeof(Tier)).Should().Be(Tier.Gold);
    [Fact] public void Enum_ByNumericValue() => Convert("2", typeof(Tier)).Should().Be(Tier.Platinum);
    [Fact] public void Enum_NameWithSpaces_Tolerated() => Convert("G old", typeof(Tier)).Should().Be(Tier.Gold);

    [Fact]
    public void DateTime_Iso8601_ParsesAsUtc() =>
        Convert("2026-07-12T10:30:00Z", typeof(DateTime))
            .Should().Be(new DateTime(2026, 7, 12, 10, 30, 0, DateTimeKind.Utc));

    [Fact] public void DateOnly_Parses() => Convert("2026-07-12", typeof(DateOnly)).Should().Be(new DateOnly(2026, 7, 12));

    [Fact] public void NullableInt_Parses() => Convert("7", typeof(int?)).Should().Be(7);
    [Fact] public void NullLiteral_ToNullable_ReturnsNull() => Convert("null", typeof(int?)).Should().BeNull();
    [Fact] public void NullLiteral_ToReferenceType_ReturnsNull() => Convert("null", typeof(string)).Should().BeNull();

    [Fact]
    public void NullLiteral_ToNonNullableValueType_Fails()
    {
        ValueConverter.TryConvert("null", typeof(int), out _, out var error).Should().BeFalse();
        error.Should().NotBeNull();
    }

    [Theory]
    [InlineData("today", 0)]
    [InlineData("today+3d", 3)]
    [InlineData("today-1d", -1)]
    [InlineData("today+2w", 14)]
    public void RelativeDate_DayAndWeekTokens(string raw, int expectedDayOffset) =>
        Convert(raw, typeof(DateTime)).Should().Be(DateTime.UtcNow.Date.AddDays(expectedDayOffset));

    [Fact]
    public void RelativeDate_YearToken() =>
        Convert("today-1y", typeof(DateTime)).Should().Be(DateTime.UtcNow.Date.AddYears(-1));

    [Fact]
    public void RelativeDate_MonthToken() =>
        Convert("today+6m", typeof(DateTime)).Should().Be(DateTime.UtcNow.Date.AddMonths(6));

    [Fact]
    public void RelativeDate_Now_IsCloseToUtcNow()
    {
        var value = (DateTime)Convert("now", typeof(DateTime))!;
        value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void RelativeDate_ToDateOnly_StaysDateOnly()
    {
        var value = Convert("today+3d", typeof(DateOnly));
        value.Should().BeOfType<DateOnly>();
        value.Should().Be(DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(3)));
    }

    // Regression: the relative-date ternary once silently promoted DateTime to DateTimeOffset.
    [Fact]
    public void RelativeDate_ToDateTime_IsExactlyDateTime() =>
        Convert("today-3d", typeof(DateTime)).Should().BeOfType<DateTime>();

    [Fact]
    public void ConversionFailure_ReturnsFalseWithError()
    {
        ValueConverter.TryConvert("not-a-number", typeof(int), out _, out var error).Should().BeFalse();
        error.Should().Contain("not-a-number").And.Contain("Int32");
    }
}

public class PropertyMatcherTests
{
    private sealed class Sample
    {
        public DateTime OrderDate { get; set; }
        public string? CustomerName { get; set; }
        public int ReadOnly { get; }
    }

    [Theory]
    [InlineData("OrderDate")]
    [InlineData("order date")]
    [InlineData("order_date")]
    [InlineData("ORDERDATE")]
    public void Find_TolerantMatching(string name)
    {
        var property = PropertyMatcher.Find(typeof(Sample), name);
        property.Should().NotBeNull();
        property!.Name.Should().Be("OrderDate");
    }

    [Fact]
    public void Find_ReadOnlyProperty_ReturnsNull() =>
        PropertyMatcher.Find(typeof(Sample), "ReadOnly").Should().BeNull();

    [Fact]
    public void Find_Missing_ReturnsNull() =>
        PropertyMatcher.Find(typeof(Sample), "Nope").Should().BeNull();
}
