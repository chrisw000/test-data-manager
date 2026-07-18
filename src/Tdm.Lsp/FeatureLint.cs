using Tdm.Core.Grammar;
using Tdm.Core.Model;
using Tdm.Core.Naming;

namespace Tdm.Lsp;

/// <summary>One squiggle: zero-based line/character range, LSP severity (1 error, 2 warning).</summary>
public sealed record LintDiagnostic(int Line, int StartChar, int EndChar, int Severity, string Message);

/// <summary>
/// Feature-file diagnostics (W4-D2): each step line runs through the *actual*
/// <see cref="StepGrammar"/> — the parser cannot drift from the engine — and entities,
/// properties and reference targets are validated against the exported model with the same
/// <see cref="NameMatcher"/> tolerance the engine resolves with. With no model, only
/// grammar (unmatched-step) diagnostics are produced.
/// </summary>
public static class FeatureLint
{
    private const int Error = 1;
    private const int Warning = 2;

    private static readonly string[] StepKeywords = ["Given", "When", "Then", "And", "But", "*"];

    public static List<LintDiagnostic> Analyze(string text, TdmModel? model)
    {
        var diagnostics = new List<LintDiagnostic>();
        var lines = text.ReplaceLineEndings("\n").Split('\n');

        var pendingTags = new List<string>();
        var featureTags = new List<string>();
        var scenarioTags = new List<string>();
        var seenFeature = false;
        var inDocString = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("\"\"\"") || trimmed.StartsWith("```"))
            {
                inDocString = !inDocString;
                continue;
            }
            if (inDocString || trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith('|'))
                continue;

            if (trimmed.StartsWith('@'))
            {
                var tags = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                pendingTags.AddRange(tags);
                CheckDomainTags(diagnostics, model, tags, i, line);
                continue;
            }

            if (trimmed.StartsWith("Feature:", StringComparison.OrdinalIgnoreCase))
            {
                seenFeature = true;
                featureTags = [.. pendingTags];
                pendingTags.Clear();
                continue;
            }
            if (trimmed.StartsWith("Scenario", StringComparison.OrdinalIgnoreCase)) // Scenario: / Scenario Outline:
            {
                scenarioTags = [.. featureTags, .. pendingTags];
                pendingTags.Clear();
                continue;
            }
            if (trimmed.StartsWith("Background", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Rule", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Examples", StringComparison.OrdinalIgnoreCase))
            {
                pendingTags.Clear();
                continue;
            }

            if (!seenFeature) continue;
            var keyword = StepKeywords.FirstOrDefault(k =>
                trimmed.StartsWith(k + " ", StringComparison.Ordinal) || trimmed.StartsWith(k + "\t", StringComparison.Ordinal));
            if (keyword is null) continue;

            var stepText = trimmed[keyword.Length..].Trim();
            if (stepText.Length == 0) continue;
            // Scenario Outline placeholders are substituted per Examples row at run time —
            // the literal text is unvalidatable here.
            if (stepText.Contains('<') && stepText.Contains('>')) continue;

            var table = CollectDataTable(lines, i);
            var plan = StepGrammar.Parse(keyword, stepText, table, i + 1);
            AnalyzeStep(diagnostics, model, plan, lines, i, line, table,
                scenarioTags.Count > 0 ? scenarioTags : featureTags);
        }
        return diagnostics;
    }

    /// <summary>Rows of the `|`-table immediately following the step, exactly as the Gherkin
    /// parser would hand them to <see cref="StepGrammar"/> — table steps parse for real.</summary>
    private static IReadOnlyList<IReadOnlyList<string>>? CollectDataTable(string[] lines, int stepLine)
    {
        List<IReadOnlyList<string>>? rows = null;
        for (var i = stepLine + 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            if (!trimmed.StartsWith('|')) break;
            var cells = trimmed.Trim('|').Split('|').Select(c => c.Trim()).ToList();
            (rows ??= []).Add(cells);
        }
        return rows;
    }

    private static void AnalyzeStep(List<LintDiagnostic> diagnostics, TdmModel? model, StepPlan plan,
        string[] lines, int lineIndex, string line, IReadOnlyList<IReadOnlyList<string>>? table,
        List<string> activeTags)
    {
        if (plan is UnmatchedStep)
        {
            diagnostics.Add(Range(lineIndex, line, plan.Text, Warning,
                "Step matches no TDM grammar rule. Verbs: exists / exist (create), is updated with, " +
                "is deleted / are deleted, should exist (verify), an external <Entity> reference \"<key>\" from <Domain>. " +
                "Try `tdm explain \"<step>\"`."));
            return;
        }
        if (model is null) return; // grammar-only mode — no entity model to validate against

        var tagDomainPin = activeTags
            .FirstOrDefault(t => t.StartsWith("@domain:", StringComparison.OrdinalIgnoreCase))?["@domain:".Length..];

        switch (plan)
        {
            case ExternalReferenceStep external:
            {
                // The owning domain often belongs to another team and isn't in this
                // workspace's model — that's the point of the identity contract. Only
                // validate the entity when the domain *is* locally modelled.
                var domain = model.Domains.FirstOrDefault(d =>
                    string.Equals(d.Name, external.SourceDomain, StringComparison.OrdinalIgnoreCase));
                if (domain is not null && FindEntity(domain, external.Entity) is null)
                {
                    diagnostics.Add(Range(lineIndex, line, external.Entity, Warning,
                        $"Domain '{domain.Name}' has no entity matching '{external.Entity}'. " +
                        KnownEntities(model, domain.Name)));
                }
                return;
            }

            case CreateStep create:
                CheckEntity(diagnostics, model, create.Entity, create.Domain ?? tagDomainPin, lineIndex, line,
                    out var createEntity);
                CheckProperties(diagnostics, createEntity, create.Overrides, lineIndex, line);
                if (create.Rows is not null && table is { Count: > 1 })
                    CheckTableHeader(diagnostics, createEntity, table[0], lines, lineIndex);
                CheckReferences(diagnostics, model, create.References, lineIndex, line);
                return;

            case UpdateStep update:
                CheckEntity(diagnostics, model, update.Entity, tagDomainPin, lineIndex, line, out var updateEntity);
                CheckProperties(diagnostics, updateEntity, update.Overrides, lineIndex, line);
                CheckReferences(diagnostics, model, update.References, lineIndex, line);
                return;

            case DeleteStep delete:
                CheckEntity(diagnostics, model, delete.Entity, tagDomainPin, lineIndex, line, out var deleteEntity);
                CheckProperties(diagnostics, deleteEntity, delete.Filter, lineIndex, line);
                return;

            case LoadStep load:
                CheckEntity(diagnostics, model, load.Entity, tagDomainPin, lineIndex, line, out var loadEntity);
                CheckProperties(diagnostics, loadEntity, load.Expected, lineIndex, line);
                return;
        }
    }

    private static void CheckEntity(List<LintDiagnostic> diagnostics, TdmModel model, string entityName,
        string? domainPin, int lineIndex, string line, out TdmModelEntity? resolved)
    {
        resolved = null;
        if (domainPin is not null &&
            !model.Domains.Any(d => string.Equals(d.Name, domainPin, StringComparison.OrdinalIgnoreCase)))
        {
            diagnostics.Add(Range(lineIndex, line, domainPin, Error,
                $"Unknown domain '{domainPin}'. Configured domains: {string.Join(", ", model.Domains.Select(d => d.Name))}."));
            return;
        }

        var matches = model.Domains
            .Where(d => domainPin is null || string.Equals(d.Name, domainPin, StringComparison.OrdinalIgnoreCase))
            .Select(d => (Domain: d, Entity: FindEntity(d, entityName)))
            .Where(m => m.Entity is not null)
            .ToList();

        switch (matches.Count)
        {
            case 0:
                diagnostics.Add(Range(lineIndex, line, entityName, Error,
                    $"Unknown entity '{entityName}'" + (domainPin is null ? "" : $" in domain '{domainPin}'") +
                    $". {KnownEntities(model, domainPin)}"));
                return;
            case > 1:
                diagnostics.Add(Range(lineIndex, line, entityName, Warning,
                    $"'{entityName}' exists in domains {string.Join(", ", matches.Select(m => m.Domain.Name))} — " +
                    $"qualify the step (e.g. \"a {matches[0].Domain.Name} {entityName} ...\") or add an @domain: tag."));
                resolved = matches[0].Entity;
                return;
            default:
                resolved = matches[0].Entity;
                return;
        }
    }

    private static void CheckProperties(List<LintDiagnostic> diagnostics, TdmModelEntity? entity,
        IEnumerable<PropertyAssignment> assignments, int lineIndex, string line)
    {
        if (entity is null) return;
        foreach (var assignment in assignments)
        {
            if (entity.Properties.Any(p => NameMatcher.Matches(assignment.Name, p.Name))) continue;
            diagnostics.Add(Range(lineIndex, line, assignment.Name, Warning,
                $"Entity '{entity.Name}' has no property matching '{assignment.Name}'. " +
                $"Properties: {string.Join(", ", entity.Properties.Select(p => p.Name))}."));
        }
    }

    /// <summary>DataTable column headers are property names — squiggle unknown ones on the
    /// header line itself.</summary>
    private static void CheckTableHeader(List<LintDiagnostic> diagnostics, TdmModelEntity? entity,
        IReadOnlyList<string> header, string[] lines, int stepLine)
    {
        if (entity is null) return;
        var headerLineIndex = -1;
        for (var i = stepLine + 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            if (trimmed.StartsWith('|')) headerLineIndex = i;
            break;
        }
        foreach (var column in header)
        {
            if (column.Length == 0 ||
                entity.Properties.Any(p => NameMatcher.Matches(column, p.Name))) continue;
            if (headerLineIndex >= 0)
            {
                diagnostics.Add(Range(headerLineIndex, lines[headerLineIndex], column, Warning,
                    $"Entity '{entity.Name}' has no property matching column '{column}'. " +
                    $"Properties: {string.Join(", ", entity.Properties.Select(p => p.Name))}."));
            }
        }
    }

    private static void CheckReferences(List<LintDiagnostic> diagnostics, TdmModel model,
        IEnumerable<ReferenceClause> references, int lineIndex, string line)
    {
        foreach (var reference in references)
        {
            if (model.AllEntities().Any(e => NameMatcher.Matches(reference.Entity, e.Entity.Name))) continue;
            diagnostics.Add(Range(lineIndex, line, reference.Entity, Error,
                $"Reference target '{reference.Entity}' matches no entity in any configured domain. " +
                KnownEntities(model, domainName: null)));
        }
    }

    private static void CheckDomainTags(List<LintDiagnostic> diagnostics, TdmModel? model,
        string[] tags, int lineIndex, string line)
    {
        if (model is null) return;
        foreach (var tag in tags)
        {
            if (!tag.StartsWith("@domain:", StringComparison.OrdinalIgnoreCase)) continue;
            var domain = tag["@domain:".Length..];
            if (domain.Length == 0 ||
                model.Domains.Any(d => string.Equals(d.Name, domain, StringComparison.OrdinalIgnoreCase))) continue;
            diagnostics.Add(Range(lineIndex, line, domain, Warning,
                $"Unknown domain '{domain}'. Configured domains: {string.Join(", ", model.Domains.Select(d => d.Name))}."));
        }
    }

    private static TdmModelEntity? FindEntity(TdmModelDomain domain, string gherkinName) =>
        domain.Entities.FirstOrDefault(e => NameMatcher.Matches(gherkinName, e.Name));

    private static string KnownEntities(TdmModel model, string? domainName)
    {
        var names = model.Domains
            .Where(d => domainName is null || string.Equals(d.Name, domainName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(d => d.Entities.Select(e => e.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return names.Count == 0
            ? "The model has no entities — regenerate with `tdm export-model`."
            : $"Known entities: {string.Join(", ", names.Take(12))}{(names.Count > 12 ? ", …" : "")}.";
    }

    /// <summary>Anchors the squiggle on the first occurrence of <paramref name="token"/> in
    /// the line (falling back to the whole trimmed text).</summary>
    private static LintDiagnostic Range(int lineIndex, string line, string token, int severity, string message)
    {
        var start = line.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (start < 0 || token.Length == 0)
        {
            start = line.Length - line.TrimStart().Length;
            return new LintDiagnostic(lineIndex, start, Math.Max(start + 1, line.TrimEnd().Length), severity, message);
        }
        return new LintDiagnostic(lineIndex, start, start + token.Length, severity, message);
    }
}
