using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Tdm.Core.Conversion;

/// <summary>
/// Conversion pipeline for raw Gherkin string values (handoff §5.4):
/// direct assign → relative date tokens → enum parse → Guid → date/time (ISO 8601) →
/// TypeConverter → Convert.ChangeType → nullable unwrap.
/// </summary>
public static partial class ValueConverter
{
    [GeneratedRegex(@"^(?<base>today|now)\s*(?:(?<sign>[+-])\s*(?<n>\d+)\s*(?<unit>[dwmy]))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RelativeDatePattern();

    public static bool TryConvert(string raw, Type targetType, out object? value, out string? error)
    {
        error = null;
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        // Explicit null for nullable targets.
        if (string.Equals(raw, "null", StringComparison.OrdinalIgnoreCase) &&
            (!targetType.IsValueType || Nullable.GetUnderlyingType(targetType) is not null))
        {
            value = null;
            return true;
        }

        try
        {
            if (underlying == typeof(string)) { value = raw; return true; }

            if (underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset) || underlying == typeof(DateOnly))
            {
                if (TryRelativeDate(raw, out var dt))
                {
                    if (underlying == typeof(DateOnly)) value = DateOnly.FromDateTime(dt);
                    else if (underlying == typeof(DateTimeOffset)) value = new DateTimeOffset(dt, TimeSpan.Zero);
                    else value = dt;
                    return true;
                }
                if (underlying == typeof(DateTime))
                {
                    value = DateTime.Parse(raw, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                    return true;
                }
                if (underlying == typeof(DateTimeOffset))
                {
                    value = DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture);
                    return true;
                }
                value = DateOnly.Parse(raw, CultureInfo.InvariantCulture);
                return true;
            }

            if (underlying.IsEnum)
            {
                value = Enum.Parse(underlying, raw.Replace(" ", "", StringComparison.Ordinal), ignoreCase: true);
                return true;
            }

            if (underlying == typeof(Guid)) { value = Guid.Parse(raw); return true; }
            if (underlying == typeof(TimeOnly)) { value = TimeOnly.Parse(raw, CultureInfo.InvariantCulture); return true; }
            if (underlying == typeof(TimeSpan)) { value = TimeSpan.Parse(raw, CultureInfo.InvariantCulture); return true; }
            if (underlying == typeof(bool)) { value = bool.Parse(raw); return true; }

            var converter = TypeDescriptor.GetConverter(underlying);
            if (converter.CanConvertFrom(typeof(string)))
            {
                value = converter.ConvertFromInvariantString(raw);
                return true;
            }

            value = Convert.ChangeType(raw, underlying, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception ex)
        {
            value = null;
            error = $"Cannot convert \"{raw}\" to {targetType.Name}: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>Evergreen relative tokens: today, now, today+3d, today-1y (units d/w/m/y). UTC-based.</summary>
    public static bool TryRelativeDate(string raw, out DateTime result)
    {
        result = default;
        var match = RelativeDatePattern().Match(raw.Trim());
        if (!match.Success) return false;

        var now = DateTime.UtcNow;
        result = match.Groups["base"].Value.Equals("today", StringComparison.OrdinalIgnoreCase) ? now.Date : now;

        if (match.Groups["sign"].Success)
        {
            var n = int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
            if (match.Groups["sign"].Value == "-") n = -n;
            result = char.ToLowerInvariant(match.Groups["unit"].Value[0]) switch
            {
                'd' => result.AddDays(n),
                'w' => result.AddDays(7 * n),
                'm' => result.AddMonths(n),
                'y' => result.AddYears(n),
                _ => result,
            };
        }
        return true;
    }
}

/// <summary>Case-insensitive, underscore/space-tolerant property lookup ("order date" → OrderDate).</summary>
public static class PropertyMatcher
{
    public static PropertyInfo? Find(Type type, string gherkinName)
    {
        var wanted = Naming.NameMatcher.Normalize(gherkinName);
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (Naming.NameMatcher.Normalize(prop.Name) == wanted && prop.CanWrite)
                return prop;
        }
        return null;
    }
}
