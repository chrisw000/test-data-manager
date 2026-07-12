using Gherkin;
using Gherkin.Ast;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace Tdm.Core.Grammar;

/// <summary>
/// Parses feature files with the official Gherkin parser (the same parser family Reqnroll uses)
/// into a <see cref="SeedingPlan"/>. Supports Scenario, Scenario Outline + Examples, Background,
/// Rule, tags, DataTables and DocStrings (handoff §6).
/// </summary>
public sealed class GherkinPlanParser
{
    private readonly Parser _parser = new();

    /// <summary>Expands glob patterns (including **) relative to <paramref name="basePath"/>.</summary>
    public SeedingPlan ParsePaths(IEnumerable<string> featurePathPatterns, string basePath)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        var literals = new List<string>();
        foreach (var pattern in featurePathPatterns)
        {
            var normalized = pattern.Replace('\\', '/');
            if (normalized.Contains('*'))
                matcher.AddInclude(normalized.TrimStart('.', '/'));
            else
                literals.Add(Path.GetFullPath(Path.Combine(basePath, pattern)));
        }

        var files = new List<string>(literals.Where(File.Exists));
        var result = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(basePath)));
        files.AddRange(result.Files.Select(f => Path.GetFullPath(Path.Combine(basePath, f.Path))));

        var plan = new SeedingPlan();
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            plan.Features.Add(ParseFile(file));
        return plan;
    }

    public FeaturePlan ParseFile(string path)
    {
        using var reader = new StreamReader(path);
        return Parse(reader, path);
    }

    public FeaturePlan ParseText(string featureText, string sourcePath = "<inline>")
    {
        using var reader = new StringReader(featureText);
        return Parse(reader, sourcePath);
    }

    private FeaturePlan Parse(TextReader reader, string sourcePath)
    {
        var document = _parser.Parse(reader);
        var feature = document.Feature
            ?? throw new InvalidOperationException($"'{sourcePath}' contains no Feature.");

        var featureTags = feature.Tags.Select(t => t.Name).ToList();
        var plan = new FeaturePlan { Name = feature.Name, SourcePath = sourcePath };

        var featureBackground = new List<Step>();
        foreach (var child in feature.Children)
        {
            switch (child)
            {
                case Background background:
                    featureBackground.AddRange(background.Steps);
                    break;

                case Scenario scenario:
                    plan.Scenarios.AddRange(ExpandScenario(feature.Name, scenario, featureTags, featureBackground));
                    break;

                case Rule rule:
                    var ruleTags = featureTags.Concat(rule.Tags.Select(t => t.Name)).ToList();
                    var ruleBackground = new List<Step>(featureBackground);
                    foreach (var ruleChild in rule.Children)
                    {
                        switch (ruleChild)
                        {
                            case Background rb:
                                ruleBackground.AddRange(rb.Steps);
                                break;
                            case Scenario rs:
                                plan.Scenarios.AddRange(ExpandScenario(feature.Name, rs, ruleTags, ruleBackground));
                                break;
                        }
                    }
                    break;
            }
        }
        return plan;
    }

    private static IEnumerable<ScenarioPlan> ExpandScenario(
        string featureName, Scenario scenario, List<string> inheritedTags, List<Step> backgroundSteps)
    {
        var scenarioTags = inheritedTags.Concat(scenario.Tags.Select(t => t.Name)).ToList();
        var allSteps = backgroundSteps.Concat(scenario.Steps).ToList();

        var examples = scenario.Examples?.ToList() ?? [];
        if (examples.Count == 0)
        {
            yield return BuildScenario(featureName, scenario.Name, scenarioTags, allSteps, substitutions: null);
            yield break;
        }

        // Scenario Outline: one expanded scenario per Examples row, <placeholder> substitution
        // applied to step text and DataTable cells.
        foreach (var exampleSet in examples)
        {
            var tags = scenarioTags.Concat(exampleSet.Tags.Select(t => t.Name)).ToList();
            var header = exampleSet.TableHeader?.Cells.Select(c => c.Value).ToList() ?? [];
            var rowIndex = 0;
            foreach (var row in exampleSet.TableBody ?? [])
            {
                rowIndex++;
                var values = row.Cells.Select(c => c.Value).ToList();
                var substitutions = new Dictionary<string, string>(StringComparer.Ordinal);
                for (var i = 0; i < header.Count && i < values.Count; i++)
                    substitutions[header[i]] = values[i];

                var name = Substitute(scenario.Name, substitutions);
                if (name == scenario.Name) name = $"{scenario.Name} #{rowIndex}";
                yield return BuildScenario(featureName, name, tags, allSteps, substitutions);
            }
        }
    }

    private static ScenarioPlan BuildScenario(
        string featureName, string name, List<string> tags, List<Step> steps,
        Dictionary<string, string>? substitutions)
    {
        var plan = new ScenarioPlan { FeatureName = featureName, Name = name, Tags = tags };
        foreach (var step in steps)
        {
            var text = Substitute(step.Text, substitutions);
            IReadOnlyList<IReadOnlyList<string>>? table = null;
            if (step.Argument is DataTable dataTable)
            {
                table = dataTable.Rows
                    .Select(r => (IReadOnlyList<string>)r.Cells.Select(c => Substitute(c.Value, substitutions)).ToList())
                    .ToList();
            }
            plan.Steps.Add(StepGrammar.Parse(step.Keyword.Trim(), text, table, step.Location.Line));
        }
        return plan;
    }

    private static string Substitute(string text, Dictionary<string, string>? substitutions)
    {
        if (substitutions is null || substitutions.Count == 0) return text;
        foreach (var (key, value) in substitutions)
            text = text.Replace($"<{key}>", value, StringComparison.Ordinal);
        return text;
    }
}
