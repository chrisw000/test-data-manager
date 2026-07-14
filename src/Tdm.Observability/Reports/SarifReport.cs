using System.Text.Encodings.Web;
using System.Text.Json;
using Tdm.Core.Manifest;

namespace Tdm.Observability.Reports;

/// <summary>
/// SARIF 2.1.0 emission (W1-D3/W1-D4): a pure function over <see cref="RunManifest"/> — the
/// manifest stays the single source of truth. Every warning, unmatched step and failed
/// scenario becomes a result anchored to the feature file and line, so the standard SARIF
/// upload actions render them as inline PR annotations.
/// Rules: TDM0001 unmatched step · TDM0002 warning · TDM0003 scenario failed.
/// </summary>
public static class SarifReport
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Report text must stay human-readable in PR annotations (quotes, em-dashes).
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Render(RunManifest manifest, string? baseDirectory = null)
    {
        var results = new List<object>();
        foreach (var scenario in manifest.Scenarios)
        {
            var uri = RelativeUri(scenario.FeatureFile, baseDirectory);

            foreach (var step in scenario.UnmatchedSteps)
            {
                results.Add(Result("TDM0001", "warning",
                    $"Unmatched step in '{scenario.Scenario}': {step.Text}", uri, step.Line));
            }

            foreach (var warning in scenario.Warnings.Concat(scenario.Entities.SelectMany(e => e.Warnings)))
            {
                results.Add(Result("TDM0002", "warning",
                    $"'{scenario.Scenario}': {warning}", uri, scenario.Line));
            }

            if (scenario.Outcome == ScenarioOutcome.Failed)
            {
                results.Add(Result("TDM0003", "error",
                    $"Scenario '{scenario.Scenario}' failed.", uri, scenario.Line));
            }
        }

        var sarif = new Dictionary<string, object>
        {
            ["$schema"] = "https://json.schemastore.org/sarif-2.1.0.json",
            ["version"] = "2.1.0",
            ["runs"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["tool"] = new Dictionary<string, object>
                    {
                        ["driver"] = new Dictionary<string, object>
                        {
                            ["name"] = "tdm",
                            ["version"] = NuGetStyle(manifest.Run.TdmVersion),
                            ["informationUri"] = "https://github.com/chrisw000/test-data-manager",
                            ["rules"] = new object[]
                            {
                                Rule("TDM0001", "Unmatched step", "A feature step matched no TDM grammar rule."),
                                Rule("TDM0002", "Run warning", "A warning was raised while executing a scenario."),
                                Rule("TDM0003", "Scenario failed", "A scenario failed under the active failure policy."),
                            },
                        },
                    },
                    ["results"] = results,
                },
            },
        };
        return JsonSerializer.Serialize(sarif, JsonOptions);
    }

    private static object Rule(string id, string name, string description) => new Dictionary<string, object>
    {
        ["id"] = id,
        ["name"] = name.Replace(" ", ""),
        ["shortDescription"] = new Dictionary<string, object> { ["text"] = description },
    };

    private static object Result(string ruleId, string level, string message, string uri, int line)
    {
        var result = new Dictionary<string, object>
        {
            ["ruleId"] = ruleId,
            ["level"] = level,
            ["message"] = new Dictionary<string, object> { ["text"] = message },
        };
        if (uri.Length > 0)
        {
            result["locations"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["physicalLocation"] = new Dictionary<string, object>
                    {
                        ["artifactLocation"] = new Dictionary<string, object> { ["uri"] = uri },
                        ["region"] = new Dictionary<string, object> { ["startLine"] = Math.Max(1, line) },
                    },
                },
            };
        }
        return result;
    }

    /// <summary>SARIF artifact URIs are relative, forward-slashed; inline-parsed features have no location.</summary>
    private static string RelativeUri(string featureFile, string? baseDirectory)
    {
        if (string.IsNullOrEmpty(featureFile) || featureFile == "<inline>") return "";
        var path = baseDirectory is not null && Path.IsPathFullyQualified(featureFile)
            ? Path.GetRelativePath(baseDirectory, featureFile)
            : featureFile;
        return path.Replace('\\', '/');
    }

    /// <summary>"0.1.0+abc123" → "0.1.0" (SARIF wants a plain version string).</summary>
    private static string NuGetStyle(string informationalVersion)
    {
        var plus = informationalVersion.IndexOf('+');
        var version = plus >= 0 ? informationalVersion[..plus] : informationalVersion;
        return version.Length > 0 ? version : "0.0.0";
    }
}
