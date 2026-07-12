using Tdm.Core.Conversion;
using Xunit;

namespace Tdm.Core.Tests.Conversion;

public class ValueConverterTests
{
    private enum Tier { Standard, Gold, Platinum }

    private static object? Convert(string raw, Type target)
    {
        Assert.True(ValueConverter.TryConvert(raw, target, out var value, out var error), error);
        return value;
    }

    [Fact] public void String_PassesThrough() => Assert.Equal("Acme Ltd", Convert("Acme Ltd", typeof(string)));
    [Fact] public void Int_Parses() => Assert.Equal(42, Convert("42", typeof(int)));
    [Fact] public void Decimal_ParsesInvariant() => Assert.Equal(1250.50m, Convert("1250.50", typeof(decimal)));
    [Fact] public void Bool_Parses() => Assert.Equal(true, Convert("true", typeof(bool)));
    [Fact] public void Guid_Parses() =>
        Assert.Equal(Guid.Parse("6fa1b3c4-0000-0000-0000-000000000001"), Convert("6fa1b3c4-0000-0000-0000-000000000001", typeof(Guid)));

    [Fact] public void Enum_ByName_CaseInsensitive() => Assert.Equal(Tier.Gold, Convert("gold", typeof(Tier)));
    [Fact] public void Enum_ByNumericValue() => Assert.Equal(Tier.Platinum, Convert("2", typeof(Tier)));
    [Fact] public void Enum_NameWithSpaces_Tolerated() => Assert.Equal(Tier.Gold, Convert("G old", typeof(Tier)));

    [Fact]
    public void DateTime_Iso8601_ParsesAsUtc()
    {
        var value = (DateTime)Convert("2026-07-12T10:30:00Z", typeof(DateTime))!;
        Assert.Equal(new DateTime(2026, 7, 12, 10, 30, 0, DateTimeKind.Utc), value);
    }

    [Fact] public void DateOnly_Parses() => Assert.Equal(new DateOnly(2026, 7, 12), Convert("2026-07-12", typeof(DateOnly)));

    [Fact] public void NullableInt_Parses() => Assert.Equal(7, Convert("7", typeof(int?)));
    [Fact] public void NullLiteral_ToNullable_ReturnsNull() => Assert.Null(Convert("null", typeof(int?)));
    [Fact] public void NullLiteral_ToReferenceType_ReturnsNull() => Assert.Null(Convert("null", typeof(string)));

    [Fact]
    public void NullLiteral_ToNonNullableValueType_Fails()
    {
        Assert.False(ValueConverter.TryConvert("null", typeof(int), out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("today", 0)]
    [InlineData("today+3d", 3)]
    [InlineData("today-1d", -1)]
    [InlineData("today+2w", 14)]
    public void RelativeDate_DayAndWeekTokens(string raw, int expectedDayOffset)
    {
        var value = (DateTime)Convert(raw, typeof(DateTime))!;
        Assert.Equal(DateTime.UtcNow.Date.AddDays(expectedDayOffset), value);
    }

    [Fact]
    public void RelativeDate_YearToken()
    {
        var value = (DateTime)Convert("today-1y", typeof(DateTime))!;
        Assert.Equal(DateTime.UtcNow.Date.AddYears(-1), value);
    }

    [Fact]
    public void RelativeDate_MonthToken()
    {
        var value = (DateTime)Convert("today+6m", typeof(DateTime))!;
        Assert.Equal(DateTime.UtcNow.Date.AddMonths(6), value);
    }

    [Fact]
    public void RelativeDate_Now_IsCloseToUtcNow()
    {
        var value = (DateTime)Convert("now", typeof(DateTime))!;
        Assert.True((DateTime.UtcNow - value).Duration() < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void RelativeDate_ToDateOnly_StaysDateOnly()
    {
        var value = Convert("today+3d", typeof(DateOnly));
        Assert.IsType<DateOnly>(value);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(3)), value);
    }

    // Regression: the relative-date ternary once silently promoted DateTime to DateTimeOffset.
    [Fact]
    public void RelativeDate_ToDateTime_IsExactlyDateTime()
    {
        var value = Convert("today-3d", typeof(DateTime));
        Assert.IsType<DateTime>(value);
    }

    [Fact]
    public void ConversionFailure_ReturnsFalseWithError()
    {
        Assert.False(ValueConverter.TryConvert("not-a-number", typeof(int), out _, out var error));
        Assert.Contains("not-a-number", error);
        Assert.Contains("Int32", error);
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
    public void Find_TolerantMatching(string name) =>
        Assert.Equal("OrderDate", PropertyMatcher.Find(typeof(Sample), name)?.Name);

    [Fact]
    public void Find_ReadOnlyProperty_ReturnsNull() =>
        Assert.Null(PropertyMatcher.Find(typeof(Sample), "ReadOnly"));

    [Fact]
    public void Find_Missing_ReturnsNull() =>
        Assert.Null(PropertyMatcher.Find(typeof(Sample), "Nope"));
}
