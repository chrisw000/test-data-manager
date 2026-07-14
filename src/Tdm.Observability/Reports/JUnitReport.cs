using System.Globalization;
using System.Xml.Linq;
using Tdm.Core.Manifest;

namespace Tdm.Observability.Reports;

/// <summary>
/// JUnit XML emission (W1-D3/W1-D4): a pure function over <see cref="RunManifest"/>. Each
/// scenario becomes a &lt;testcase&gt; (classname = feature): Failed → &lt;failure&gt;,
/// Skipped → &lt;skipped/&gt;, CompletedWithWarnings → passed with the warnings in
/// &lt;system-out&gt; — CI UIs render seeding runs as test results.
/// </summary>
public static class JUnitReport
{
    public static string Render(RunManifest manifest)
    {
        var suites = manifest.Scenarios
            .GroupBy(s => s.Feature)
            .Select(feature => new XElement("testsuite",
                new XAttribute("name", feature.Key),
                new XAttribute("tests", feature.Count()),
                new XAttribute("failures", feature.Count(s => s.Outcome == ScenarioOutcome.Failed)),
                new XAttribute("skipped", feature.Count(s => s.Outcome == ScenarioOutcome.Skipped)),
                new XAttribute("time", Seconds(feature.Sum(DurationMs))),
                feature.Select(TestCase)))
            .ToList();

        var root = new XElement("testsuites",
            new XAttribute("name", manifest.Run.Name),
            new XAttribute("tests", manifest.Scenarios.Count),
            new XAttribute("failures", manifest.Scenarios.Count(s => s.Outcome == ScenarioOutcome.Failed)),
            new XAttribute("skipped", manifest.Scenarios.Count(s => s.Outcome == ScenarioOutcome.Skipped)),
            new XAttribute("time", Seconds(manifest.Run.DurationMs)),
            suites);

        return new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString();
    }

    private static XElement TestCase(ScenarioManifest scenario)
    {
        var testCase = new XElement("testcase",
            new XAttribute("classname", scenario.Feature),
            new XAttribute("name", scenario.Scenario),
            new XAttribute("time", Seconds(DurationMs(scenario))));

        var warnings = scenario.Warnings
            .Concat(scenario.Entities.SelectMany(e => e.Warnings))
            .Concat(scenario.UnmatchedSteps.Select(u => $"Unmatched step (line {u.Line}): {u.Text}"))
            .ToList();

        switch (scenario.Outcome)
        {
            case ScenarioOutcome.Failed:
                testCase.Add(new XElement("failure",
                    new XAttribute("message", warnings.FirstOrDefault() ?? "Scenario failed"),
                    string.Join(Environment.NewLine, warnings)));
                break;
            case ScenarioOutcome.Skipped:
                testCase.Add(new XElement("skipped"));
                break;
            default:
                if (warnings.Count > 0)
                    testCase.Add(new XElement("system-out", string.Join(Environment.NewLine, warnings)));
                break;
        }
        return testCase;
    }

    private static double DurationMs(ScenarioManifest scenario) => scenario.Entities.Sum(e => e.DurationMs);

    private static string Seconds(double ms) => (ms / 1000d).ToString("0.###", CultureInfo.InvariantCulture);
}
