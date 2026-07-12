using Humanizer;
using Tdm.Core.Settings;

namespace Tdm.Core.Naming;

/// <summary>
/// Name normalisation and matching between Gherkin entity names ("Customer", "customers",
/// "order line") and CLR type names with convention suffixes ("CustomerEntity", "OrderLineModel").
/// </summary>
public static class NameMatcher
{
    /// <summary>Lower-case, strips spaces/underscores/hyphens: "order line" → "orderline".</summary>
    public static string Normalize(string name)
    {
        Span<char> buffer = stackalloc char[name.Length];
        var i = 0;
        foreach (var c in name)
        {
            if (c is ' ' or '_' or '-') continue;
            buffer[i++] = char.ToLowerInvariant(c);
        }
        return new string(buffer[..i]);
    }

    /// <summary>"Customers" → "Customer"; already-singular input is returned unchanged.</summary>
    public static string Singularize(string name) => name.Singularize(inputIsKnownToBePlural: false);

    /// <summary>
    /// Strips a convention class pattern from a CLR type name: pattern "{Name}Entity"
    /// turns "CustomerEntity" into "Customer". Types not matching the pattern are returned as-is.
    /// </summary>
    public static string StripPattern(string clrTypeName, string entityClassPattern)
    {
        var idx = entityClassPattern.IndexOf("{Name}", StringComparison.Ordinal);
        if (idx < 0) return clrTypeName;
        var prefix = entityClassPattern[..idx];
        var suffix = entityClassPattern[(idx + "{Name}".Length)..];

        var name = clrTypeName;
        if (prefix.Length > 0 && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            name = name[prefix.Length..];
        if (suffix.Length > 0 && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && name.Length > suffix.Length)
            name = name[..^suffix.Length];
        return name;
    }

    /// <summary>Case-insensitive, space/underscore-tolerant, singular/plural-tolerant comparison.</summary>
    public static bool Matches(string gherkinName, string logicalName)
    {
        var normalized = Normalize(gherkinName);
        var target = Normalize(logicalName);
        if (normalized == target) return true;
        return Normalize(Singularize(gherkinName)) == target;
    }

    /// <summary>Expands a convention pattern for a logical name: ("I{Name}Repository", "Customer") → "ICustomerRepository".</summary>
    public static string Expand(string pattern, string logicalName) =>
        pattern.Replace("{Name}", logicalName, StringComparison.Ordinal);
}
