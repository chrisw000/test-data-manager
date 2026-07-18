using System.Text.RegularExpressions;
using Tdm.Core.Grammar;
using Tdm.Core.Model;
using Tdm.Core.Naming;

namespace Tdm.Lsp;

/// <summary>Kind uses LSP CompletionItemKind numbers (7 class/entity, 10 property, 9 module/domain, 14 keyword/tag).</summary>
public sealed record CompletionItem(string Label, int Kind, string? Detail, string? InsertText = null);

/// <summary>
/// Context-aware completion (W4-D2): entity names after <c>a/an/the following/N/for</c>,
/// property names inside <c>with</c> clauses (resolved against the step's entity), and the
/// tag vocabulary after <c>@</c>. Pure over (text, position, model) — testable without a client.
/// </summary>
public static partial class CompletionProvider
{
    private const int EntityKind = 7;
    private const int DomainKind = 9;
    private const int PropertyKind = 10;
    private const int TagKind = 14;

    [GeneratedRegex(@"(?:\ban?\s+|\bthe\s+following\s+|\bexternal\s+|(?<=^|\s)\d+\s+|\bfor\s+|\ball\s+|\bthe\s+)(?<partial>\w*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EntityPosition();

    [GeneratedRegex(@"(?:\bwith\s+|\band\s+|,\s*)(?<partial>[\w .\-]*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PropertyPosition();

    [GeneratedRegex(@"^\s*(?:Given|When|Then|And|But|\*)\s+(?<step>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StepLine();

    [GeneratedRegex(@"^(?:an?\s+|the\s+following\s+|the\s+|all\s+|\d+\s+)(?<entity>\w+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingEntity();

    private static readonly (string Tag, string Detail)[] TagVocabulary =
    [
        ("@seed:", "Pins the deterministic generation seed, e.g. @seed:42"),
        ("@domain:", "Pins entity resolution to one domain"),
        ("@benchmark", "Forces benchmark stats for this scenario"),
        ("@skip", "Parsed but not executed (reported as skipped)"),
        ("@persistent", "Rows survive the run (overrides run lifecycle)"),
        ("@ephemeral", "Tracked teardown at scenario end"),
    ];

    public static List<CompletionItem> Complete(string text, int line, int character, TdmModel? model)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        if (line < 0 || line >= lines.Length) return [];
        var lineText = lines[line];
        var prefix = lineText[..Math.Min(character, lineText.Length)];
        var trimmed = prefix.TrimStart();

        // Tag lines: the vocabulary, plus domain names after @domain:.
        if (trimmed.StartsWith('@'))
        {
            var lastToken = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "@";
            if (lastToken.StartsWith("@domain:", StringComparison.OrdinalIgnoreCase) && model is not null)
            {
                return [.. model.Domains.Select(d =>
                    new CompletionItem($"@domain:{d.Name}", TagKind, $"{d.Entities.Count} entities", $"@domain:{d.Name}"))];
            }
            return [.. TagVocabulary.Select(t => new CompletionItem(t.Tag, TagKind, t.Detail))];
        }

        if (model is null || !StepLine().IsMatch(prefix)) return [];

        // Property position wins over entity position: "an Order exists with sta|" is a
        // property, even though "with sta" also ends in a bare word.
        var propertyMatch = PropertyPosition().Match(prefix);
        if (propertyMatch.Success && prefix.Contains(" with ", StringComparison.OrdinalIgnoreCase))
        {
            var entity = ResolveStepEntity(lineText, model);
            if (entity is not null)
            {
                return [.. entity.Properties
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new CompletionItem(p.Name, PropertyKind, $"{p.Type} on {entity.Name}"))];
            }
        }

        if (EntityPosition().IsMatch(prefix))
        {
            var items = model.AllEntities()
                .Select(pair => new CompletionItem(pair.Entity.Name, EntityKind,
                    $"{pair.Domain.Name} · {pair.Entity.ClrType}"))
                .DistinctBy(i => (i.Label, i.Detail))
                .OrderBy(i => i.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();
            // Domain qualifiers complete too: "a Billing |Customer exists".
            items.AddRange(model.Domains.Select(d =>
                new CompletionItem(d.Name, DomainKind, $"domain · {d.Entities.Count} entities")));
            return items;
        }

        return [];
    }

    /// <summary>The entity the current step line talks about, via the real grammar — falls
    /// back to null when the line doesn't parse or the entity isn't in the model.</summary>
    private static TdmModelEntity? ResolveStepEntity(string lineText, TdmModel model)
    {
        var stepMatch = StepLine().Match(lineText);
        if (!stepMatch.Success) return null;
        var stepText = stepMatch.Groups["step"].Value.Trim();
        // Synthetic table so `the following <Entities> exist ... with |` resolves its entity.
        var plan = StepGrammar.Parse("Given", stepText, [["_"], ["_"]], line: 1);
        var (entityName, domainPin) = plan switch
        {
            CreateStep create => (create.Entity, create.Domain),
            UpdateStep update => (update.Entity, null),
            DeleteStep delete => (delete.Entity, null),
            LoadStep load => (load.Entity, null),
            // Mid-edit lines often don't parse yet ("the Customer "X" is updated with ") —
            // fall back to the leading entity token so property completion still fires.
            _ => (LeadingEntity().Match(stepText) is { Success: true } m ? m.Groups["entity"].Value : null, null),
        };
        if (entityName is null) return null;

        return model.Domains
            .Where(d => domainPin is null || string.Equals(d.Name, domainPin, StringComparison.OrdinalIgnoreCase))
            .SelectMany(d => d.Entities)
            .FirstOrDefault(e => NameMatcher.Matches(entityName, e.Name));
    }
}
