using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Tdm.Core.Benchmarks;

namespace Tdm.Observability.Reports;

/// <summary>
/// `tdm bench compare` output (W3-D8): a fixed-width console table of current-vs-baseline
/// stats, and a JUnit projection of the gate results so regressions render as failed tests
/// in CI — same posture as the W1-D3 report formats, pure functions over the comparison.
/// </summary>
public static class BenchmarkCompareReport
{
    public static string RenderTable(IReadOnlyList<ComparisonRow> rows, string stat, string baselineDescription)
    {
        var table = new StringBuilder();
        table.AppendLine($"Benchmark comparison ({stat}) — baseline: {baselineDescription}");
        table.AppendLine($"{"operation",-32} {"baseline",12} {"current",12} {"change",10}");
        foreach (var row in rows)
        {
            table.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0,-32} {1,12} {2,12} {3,10}",
                row.Operation,
                Ms(row.BaselineMs), Ms(row.CurrentMs),
                row.RegressionPct is { } pct ? (pct >= 0 ? $"+{pct:0.#}%" : $"{pct:0.#}%") : "-"));
        }
        return table.ToString();
    }

    public static string RenderJUnit(IReadOnlyList<GateResult> gates, string baselineDescription)
    {
        var suite = new XElement("testsuite",
            new XAttribute("name", "tdm-bench-compare"),
            new XAttribute("tests", gates.Count),
            new XAttribute("failures", gates.Count(g => !g.Passed)),
            new XAttribute("skipped", 0),
            gates.Select(g =>
            {
                var testCase = new XElement("testcase",
                    new XAttribute("classname", "tdm-bench-compare"),
                    new XAttribute("name", $"{g.Gate.Operation} {g.Gate.Stat} ≤ baseline +{g.Gate.MaxRegressionPct}%"));
                if (!g.Passed)
                    testCase.Add(new XElement("failure", new XAttribute("message", g.Message), g.Message));
                else
                    testCase.Add(new XElement("system-out", $"{g.Message} (baseline: {baselineDescription})"));
                return testCase;
            }));

        var root = new XElement("testsuites",
            new XAttribute("name", "tdm-bench-compare"),
            new XAttribute("tests", gates.Count),
            new XAttribute("failures", gates.Count(g => !g.Passed)),
            suite);
        return new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString();
    }

    private static string Ms(double? value) =>
        value is { } ms ? ms.ToString("0.###", CultureInfo.InvariantCulture) : "-";
}
