using System.Text;
using System.Text.RegularExpressions;
using Tdm.Core.Grammar;
using Tdm.Core.Model;
using Tdm.Core.Naming;

namespace Tdm.Lsp;

/// <summary>
/// Verb documentation on hover (W4-D2), sourced from the Wave 1 grammar reference — the
/// matched rule's shape, a real example, and the resolved entity's model facts. Pure over
/// (line text, model).
/// </summary>
public static partial class HoverProvider
{
    [GeneratedRegex(@"^\s*(?:Given|When|Then|And|But|\*)\s+(?<step>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StepLine();

    /// <summary>Markdown for the step under the cursor, or null off step lines.</summary>
    public static string? Hover(string lineText, TdmModel? model)
    {
        var match = StepLine().Match(lineText);
        if (!match.Success) return null;
        var stepText = match.Groups["step"].Value.Trim();
        if (stepText.Length == 0) return null;

        // A synthetic two-row table lets `the following <Entities> exist:` steps take the
        // DataTable-bulk grammar branch — the real table lives on the lines below, which a
        // single-line hover cannot see. Other verbs ignore the table entirely.
        var plan = StepGrammar.Parse("Given", stepText, [["_"], ["_"]], line: 1);
        var markdown = new StringBuilder();

        switch (plan)
        {
            case ExternalReferenceStep external:
                markdown.AppendLine("**External reference** — `an external <Entity> reference \"<key>\" from <Domain>`");
                markdown.AppendLine();
                markdown.AppendLine("Brings another domain's entity id into scope via the identity contract — no " +
                                    "cross-database transaction, no runtime coordination. The id is " +
                                    "`UUIDv5(\"{owningDomain}|{Entity}|{naturalKey}\")`, so both domains derive the same GUID independently.");
                markdown.AppendLine();
                markdown.AppendLine($"This step: `{external.SourceDomain}|{external.Entity}|{external.Key}`");
                markdown.AppendLine();
                markdown.AppendLine("```gherkin\nGiven an external Customer reference \"Acme Ltd\" from Orders\n```");
                break;

            case CreateStep { Rows: not null } table:
                markdown.AppendLine("**Create (DataTable bulk)** — `the following <Entities> exist:` + table");
                markdown.AppendLine();
                markdown.AppendLine("One entity per table row; column headers are property names. Shared " +
                                    "`for <Entity> \"<key>\"` references and `with` defaults before the colon apply " +
                                    "to every row (per-row cells win).");
                markdown.AppendLine();
                markdown.AppendLine("```gherkin\nGiven the following Invoices exist for Account \"Cleanup Account\":\n" +
                                    "  | InvoiceNumber | Amount | Status |\n  | INV-D-01      | 10.00  | Draft  |\n```");
                AppendEntityFacts(markdown, model, table.Entity, table.Domain);
                break;

            case CreateStep { Count: > 1 } bulk:
                markdown.AppendLine("**Create (count bulk)** — `<N> <Entities> exist [with ...]`");
                markdown.AppendLine();
                markdown.AppendLine("N generated rows, chunked bulk insert. Values not overridden come from the " +
                                    "domain's faker, deterministic under the scenario seed.");
                markdown.AppendLine();
                markdown.AppendLine("```gherkin\nGiven 500 Products exist with category \"LoadTest\"\n```");
                AppendEntityFacts(markdown, model, bulk.Entity, bulk.Domain);
                break;

            case CreateStep create:
                markdown.AppendLine("**Create** — `a/an [Domain] <Entity> exists [for <Entity> \"<key>\"]* [with <prop> \"<value>\" and ...]`");
                markdown.AppendLine();
                markdown.AppendLine("Generates the entity with the domain's faker (deterministic under the scenario " +
                                    "seed), applies overrides, resolves `for` references by natural key (context bag " +
                                    "→ database), persists via the repository route.");
                markdown.AppendLine();
                markdown.AppendLine("```gherkin\nGiven an Order exists for Customer \"Acme Ltd\" with status \"Pending\"\n```");
                AppendEntityFacts(markdown, model, create.Entity, create.Domain);
                break;

            case UpdateStep update:
                markdown.AppendLine("**Update** — `the <Entity> \"<key>\" is updated with <prop> \"<value>\" and ...`");
                markdown.AppendLine();
                markdown.AppendLine("Finds the row by natural key and applies the property assignments.");
                markdown.AppendLine();
                markdown.AppendLine("```gherkin\nWhen the Customer \"Acme Ltd\" is updated with tier \"Platinum\"\n```");
                AppendEntityFacts(markdown, model, update.Entity, null);
                break;

            case DeleteStep { All: true } deleteAll:
                markdown.AppendLine("**Delete (filtered/all)** — `all <Entities> [with <prop> \"<value>\"] are deleted`");
                markdown.AppendLine();
                markdown.AppendLine("```gherkin\nWhen all Invoices with status \"Draft\" are deleted\n```");
                AppendEntityFacts(markdown, model, deleteAll.Entity, null);
                break;

            case DeleteStep delete:
                markdown.AppendLine("**Delete** — `the <Entity> \"<key>\" is deleted`");
                markdown.AppendLine();
                markdown.AppendLine("```gherkin\nWhen the Product \"TMP-0001\" is deleted\n```");
                AppendEntityFacts(markdown, model, delete.Entity, null);
                break;

            case LoadStep { ExpectedCount: not null } loadCount:
                markdown.AppendLine("**Verify (count)** — `<N> <Entities> should exist [with <prop> \"<value>\"]`");
                markdown.AppendLine();
                markdown.AppendLine("Read + assert; also part of the benchmark surface.");
                markdown.AppendLine();
                markdown.AppendLine("```gherkin\nThen 2 Products should exist with category \"Widgets\"\n```");
                AppendEntityFacts(markdown, model, loadCount.Entity, null);
                break;

            case LoadStep load:
                markdown.AppendLine("**Verify** — `a/an <Entity> \"<key>\" should exist [with <prop> \"<value>\" and ...]`");
                markdown.AppendLine();
                markdown.AppendLine("Read + assert by natural key; also part of the benchmark surface.");
                markdown.AppendLine();
                markdown.AppendLine("```gherkin\nThen an Order \"ORD-1001\" should exist with status \"Pending\"\n```");
                AppendEntityFacts(markdown, model, load.Entity, null);
                break;

            default:
                markdown.AppendLine("**No TDM grammar rule matches this step.**");
                markdown.AppendLine();
                markdown.AppendLine("Verbs: `exists` / `exist` (create), `is updated with`, `is deleted` / " +
                                    "`are deleted`, `should exist` (verify), `an external <Entity> reference " +
                                    "\"<key>\" from <Domain>`.");
                markdown.AppendLine();
                markdown.AppendLine("Ask the pipeline itself: `tdm explain \"<step text>\"`.");
                break;
        }
        return markdown.ToString().TrimEnd();
    }

    private static void AppendEntityFacts(StringBuilder markdown, TdmModel? model, string entityName, string? domainPin)
    {
        if (model is null) return;
        foreach (var domain in model.Domains)
        {
            if (domainPin is not null &&
                !string.Equals(domain.Name, domainPin, StringComparison.OrdinalIgnoreCase)) continue;
            var entity = domain.Entities.FirstOrDefault(e => NameMatcher.Matches(entityName, e.Name));
            if (entity is null) continue;

            markdown.AppendLine();
            markdown.AppendLine($"---\n`{domain.Name}.{entity.Name}` → `{entity.ClrType}`  ");
            if (entity.NaturalKey is not null) markdown.AppendLine($"natural key: `{entity.NaturalKey}`  ");
            if (entity.Key is not null) markdown.AppendLine($"key: `{entity.Key}`  ");
            if (entity.FakerSource is not null) markdown.AppendLine($"faker: `{entity.FakerSource}`");
            return;
        }
    }
}
