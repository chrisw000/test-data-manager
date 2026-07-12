using System.Text.RegularExpressions;

namespace Tdm.Core.Grammar;

/// <summary>
/// The fixed TDM verb grammar (handoff §6.1). Matching is on step text only —
/// Given/When/Then keywords are not significant, so And/But continuation works naturally.
/// </summary>
public static partial class StepGrammar
{
    [GeneratedRegex(@"^an?\s+external\s+(?<entity>\w+)\s+reference\s+""(?<key>[^""]*)""\s+from\s+(?<domain>\w+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExternalReference();

    [GeneratedRegex(@"^an?\s+(?:(?<domain>\w+)\s+)?(?<entity>\w+)\s+exists\b(?<rest>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateSingle();

    [GeneratedRegex(@"^the\s+following\s+(?:(?<domain>\w+)\s+)?(?<entity>\w+)\s+exist\b(?<rest>[^:]*):?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateTable();

    [GeneratedRegex(@"^(?<count>\d+)\s+(?:(?<domain>\w+)\s+)?(?<entity>\w+)\s+exist\b(?<rest>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateCount();

    [GeneratedRegex(@"^the\s+(?<entity>\w+)\s+""(?<key>[^""]*)""\s+is\s+updated\s+with\s+(?<rest>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Update();

    [GeneratedRegex(@"^the\s+(?<entity>\w+)\s+""(?<key>[^""]*)""\s+is\s+deleted$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeleteSingle();

    [GeneratedRegex(@"^all\s+(?<entity>\w+)(?:\s+with\s+(?<props>.+?))?\s+are\s+deleted$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeleteAll();

    [GeneratedRegex(@"^an?\s+(?<entity>\w+)\s+""(?<key>[^""]*)""\s+should\s+exist(?:\s+with\s+(?<props>.+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LoadSingle();

    [GeneratedRegex(@"^(?<count>\d+)\s+(?<entity>\w+)\s+should\s+exist(?:\s+with\s+(?<props>.+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LoadCount();

    [GeneratedRegex(@"\bfor\s+(?<entity>\w+)\s+""(?<key>[^""]*)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForClause();

    [GeneratedRegex(@"(?<name>[\w][\w .\-]*?)\s+""(?<value>[^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex Assignment();

    public static StepPlan Parse(string keyword, string text, IReadOnlyList<IReadOnlyList<string>>? dataTable, int line)
    {
        var trimmed = text.Trim();

        Match m;
        if ((m = ExternalReference().Match(trimmed)).Success)
        {
            return new ExternalReferenceStep
            {
                Text = trimmed, Keyword = keyword, Line = line,
                Entity = m.Groups["entity"].Value,
                Key = m.Groups["key"].Value,
                SourceDomain = m.Groups["domain"].Value,
            };
        }

        // Load patterns must be probed before create patterns: "2 Products should exist"
        // would otherwise satisfy the create-count pattern with entity "should".
        if ((m = LoadSingle().Match(trimmed)).Success)
        {
            return new LoadStep
            {
                Text = trimmed, Keyword = keyword, Line = line,
                Entity = m.Groups["entity"].Value,
                Key = m.Groups["key"].Value,
                Expected = ParseAssignments(m.Groups["props"].Value),
            };
        }

        if ((m = LoadCount().Match(trimmed)).Success)
        {
            return new LoadStep
            {
                Text = trimmed, Keyword = keyword, Line = line,
                Entity = m.Groups["entity"].Value,
                ExpectedCount = int.Parse(m.Groups["count"].Value),
                Expected = ParseAssignments(m.Groups["props"].Value),
            };
        }

        if ((m = CreateTable().Match(trimmed)).Success && dataTable is { Count: > 1 })
        {
            // Shared clauses on the step line — `for Account "X"` refs and `with ...` defaults —
            // apply to every row; per-row cells win over shared defaults.
            var (shared, references) = ParseRest(m.Groups["rest"].Value);
            var header = dataTable[0];
            var rows = new List<PropertyBag>();
            foreach (var row in dataTable.Skip(1))
            {
                var bag = new PropertyBag(shared);
                for (var i = 0; i < header.Count && i < row.Count; i++)
                    bag.Add(new PropertyAssignment(header[i], row[i]));
                rows.Add(bag);
            }
            return new CreateStep
            {
                Text = trimmed, Keyword = keyword, Line = line,
                Domain = NullIfEmpty(m.Groups["domain"].Value),
                Entity = m.Groups["entity"].Value,
                Rows = rows,
                Count = rows.Count,
                References = references,
            };
        }

        if ((m = CreateCount().Match(trimmed)).Success)
        {
            var (overrides, references) = ParseRest(m.Groups["rest"].Value);
            return new CreateStep
            {
                Text = trimmed, Keyword = keyword, Line = line,
                Domain = NullIfEmpty(m.Groups["domain"].Value),
                Entity = m.Groups["entity"].Value,
                Count = int.Parse(m.Groups["count"].Value),
                Overrides = overrides,
                References = references,
            };
        }

        if ((m = CreateSingle().Match(trimmed)).Success)
        {
            var (overrides, references) = ParseRest(m.Groups["rest"].Value);
            return new CreateStep
            {
                Text = trimmed, Keyword = keyword, Line = line,
                Domain = NullIfEmpty(m.Groups["domain"].Value),
                Entity = m.Groups["entity"].Value,
                Overrides = overrides,
                References = references,
            };
        }

        if ((m = Update().Match(trimmed)).Success)
        {
            var (overrides, references) = ParseRest("with " + m.Groups["rest"].Value);
            return new UpdateStep
            {
                Text = trimmed, Keyword = keyword, Line = line,
                Entity = m.Groups["entity"].Value,
                Key = m.Groups["key"].Value,
                Overrides = overrides,
                References = references,
            };
        }

        if ((m = DeleteSingle().Match(trimmed)).Success)
        {
            return new DeleteStep
            {
                Text = trimmed, Keyword = keyword, Line = line,
                Entity = m.Groups["entity"].Value,
                Key = m.Groups["key"].Value,
            };
        }

        if ((m = DeleteAll().Match(trimmed)).Success)
        {
            return new DeleteStep
            {
                Text = trimmed, Keyword = keyword, Line = line,
                Entity = m.Groups["entity"].Value,
                All = true,
                Filter = ParseAssignments(m.Groups["props"].Value),
            };
        }

        return new UnmatchedStep { Text = trimmed, Keyword = keyword, Line = line };
    }

    /// <summary>
    /// Parses the tail of a create/update step: zero or more `for Entity "key"` reference
    /// clauses (before or after the overrides) plus an optional `with prop "value" and ...` list.
    /// </summary>
    private static (PropertyBag Overrides, List<ReferenceClause> References) ParseRest(string rest)
    {
        var references = new List<ReferenceClause>();
        var withoutRefs = ForClause().Replace(rest, m2 =>
        {
            references.Add(new ReferenceClause(m2.Groups["entity"].Value, m2.Groups["key"].Value));
            return "";
        });

        var overrides = new PropertyBag();
        var withIdx = withoutRefs.IndexOf("with ", StringComparison.OrdinalIgnoreCase);
        if (withIdx >= 0)
            overrides = ParseAssignments(withoutRefs[(withIdx + 5)..]);

        return (overrides, references);
    }

    /// <summary>Parses `name "Acme Ltd" and tier "Gold"` (also comma-separated) into ordered assignments.</summary>
    public static PropertyBag ParseAssignments(string? text)
    {
        var bag = new PropertyBag();
        if (string.IsNullOrWhiteSpace(text)) return bag;

        foreach (Match m in Assignment().Matches(text))
        {
            var name = m.Groups["name"].Value.Trim().TrimStart(',').Trim();
            if (name.StartsWith("and ", StringComparison.OrdinalIgnoreCase)) name = name[4..].Trim();
            if (name.Length == 0) continue;
            bag.Add(new PropertyAssignment(name, m.Groups["value"].Value));
        }
        return bag;
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
