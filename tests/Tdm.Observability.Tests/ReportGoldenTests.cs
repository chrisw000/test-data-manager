using AwesomeAssertions;
using Tdm.Core.Manifest;
using Tdm.Observability.Reports;
using Xunit;

namespace Tdm.Observability.Tests;

/// <summary>
/// Golden-output tests (W1-P3): the emitters are pure functions over RunManifest, so a fixed
/// manifest must render byte-identical reports. A diff here means the report contract changed —
/// update the golden text deliberately.
/// </summary>
public class ReportGoldenTests
{
    /// <summary>One of each interesting case: passed-with-warnings (+ unmatched step),
    /// failed, skipped, and clean-pass in a second feature.</summary>
    private static RunManifest Fixture() => new()
    {
        Run = new RunInfo
        {
            Name = "golden-run",
            TdmVersion = "0.1.0+abc1234",
            DurationMs = 1234.5,
            Outcome = RunOutcome.CompletedWithWarnings,
            PolicyViolations = [new PolicyViolationInfo { Rule = "MaxBulkRowsPerStep", Message = "creates 500 row(s) of 'Product', exceeding the max of 100." }],
        },
        Scenarios =
        [
            new ScenarioManifest
            {
                Feature = "Orders regression seed",
                FeatureFile = "features/orders-seeding.feature",
                Scenario = "Customer places an order",
                Line = 12,
                Outcome = ScenarioOutcome.CompletedWithWarnings,
                Warnings = ["no OrderFaker found — heuristic auto-faker will be used."],
                UnmatchedSteps = [new UnmatchedStepManifest { Text = "the moon is full", Line = 17 }],
                Entities =
                [
                    new EntityManifest { Entity = "Customer", DurationMs = 40, Warnings = ["tier defaulted"] },
                    new EntityManifest { Entity = "Order", DurationMs = 60 },
                ],
            },
            new ScenarioManifest
            {
                Feature = "Orders regression seed",
                FeatureFile = "features/orders-seeding.feature",
                Scenario = "Bulk catalogue",
                Line = 25,
                Outcome = ScenarioOutcome.Failed,
                Warnings = ["Create Product failed: UNIQUE constraint"],
            },
            new ScenarioManifest
            {
                Feature = "Orders regression seed",
                FeatureFile = "features/orders-seeding.feature",
                Scenario = "Not yet implemented",
                Line = 40,
                Outcome = ScenarioOutcome.Skipped,
            },
            new ScenarioManifest
            {
                Feature = "Billing cross-domain seeding",
                FeatureFile = "features/billing-cross-domain.feature",
                Scenario = "Invoice for an externally-owned customer",
                Line = 6,
                Outcome = ScenarioOutcome.Succeeded,
                Entities = [new EntityManifest { Entity = "Invoice", DurationMs = 100 }],
            },
        ],
    };

    [Fact]
    public void Sarif_Golden()
    {
        var expected = """
            {
              "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
              "version": "2.1.0",
              "runs": [
                {
                  "tool": {
                    "driver": {
                      "name": "tdm",
                      "version": "0.1.0",
                      "informationUri": "https://github.com/chrisw000/test-data-manager",
                      "rules": [
                        {
                          "id": "TDM0001",
                          "name": "Unmatchedstep",
                          "shortDescription": {
                            "text": "A feature step matched no TDM grammar rule."
                          }
                        },
                        {
                          "id": "TDM0002",
                          "name": "Runwarning",
                          "shortDescription": {
                            "text": "A warning was raised while executing a scenario."
                          }
                        },
                        {
                          "id": "TDM0003",
                          "name": "Scenariofailed",
                          "shortDescription": {
                            "text": "A scenario failed under the active failure policy."
                          }
                        },
                        {
                          "id": "TDM0004",
                          "name": "Policyviolation",
                          "shortDescription": {
                            "text": "A policy (tdm.policy.json) or key-registry (tdm.keys.json) rule was violated before persistence."
                          }
                        }
                      ]
                    }
                  },
                  "results": [
                    {
                      "ruleId": "TDM0004",
                      "level": "error",
                      "message": {
                        "text": "[MaxBulkRowsPerStep] creates 500 row(s) of 'Product', exceeding the max of 100."
                      }
                    },
                    {
                      "ruleId": "TDM0001",
                      "level": "warning",
                      "message": {
                        "text": "Unmatched step in 'Customer places an order': the moon is full"
                      },
                      "locations": [
                        {
                          "physicalLocation": {
                            "artifactLocation": {
                              "uri": "features/orders-seeding.feature"
                            },
                            "region": {
                              "startLine": 17
                            }
                          }
                        }
                      ]
                    },
                    {
                      "ruleId": "TDM0002",
                      "level": "warning",
                      "message": {
                        "text": "'Customer places an order': no OrderFaker found — heuristic auto-faker will be used."
                      },
                      "locations": [
                        {
                          "physicalLocation": {
                            "artifactLocation": {
                              "uri": "features/orders-seeding.feature"
                            },
                            "region": {
                              "startLine": 12
                            }
                          }
                        }
                      ]
                    },
                    {
                      "ruleId": "TDM0002",
                      "level": "warning",
                      "message": {
                        "text": "'Customer places an order': tier defaulted"
                      },
                      "locations": [
                        {
                          "physicalLocation": {
                            "artifactLocation": {
                              "uri": "features/orders-seeding.feature"
                            },
                            "region": {
                              "startLine": 12
                            }
                          }
                        }
                      ]
                    },
                    {
                      "ruleId": "TDM0002",
                      "level": "warning",
                      "message": {
                        "text": "'Bulk catalogue': Create Product failed: UNIQUE constraint"
                      },
                      "locations": [
                        {
                          "physicalLocation": {
                            "artifactLocation": {
                              "uri": "features/orders-seeding.feature"
                            },
                            "region": {
                              "startLine": 25
                            }
                          }
                        }
                      ]
                    },
                    {
                      "ruleId": "TDM0003",
                      "level": "error",
                      "message": {
                        "text": "Scenario 'Bulk catalogue' failed."
                      },
                      "locations": [
                        {
                          "physicalLocation": {
                            "artifactLocation": {
                              "uri": "features/orders-seeding.feature"
                            },
                            "region": {
                              "startLine": 25
                            }
                          }
                        }
                      ]
                    }
                  ]
                }
              ]
            }
            """;
        SarifReport.Render(Fixture()).ReplaceLineEndings().Should().Be(expected.ReplaceLineEndings());
    }

    [Fact]
    public void JUnit_Golden()
    {
        var expected = """
            <testsuites name="golden-run" tests="4" failures="1" skipped="1" time="1.235">
              <testsuite name="Orders regression seed" tests="3" failures="1" skipped="1" time="0.1">
                <testcase classname="Orders regression seed" name="Customer places an order" time="0.1">
                  <system-out>no OrderFaker found — heuristic auto-faker will be used.
            tier defaulted
            Unmatched step (line 17): the moon is full</system-out>
                </testcase>
                <testcase classname="Orders regression seed" name="Bulk catalogue" time="0">
                  <failure message="Create Product failed: UNIQUE constraint">Create Product failed: UNIQUE constraint</failure>
                </testcase>
                <testcase classname="Orders regression seed" name="Not yet implemented" time="0">
                  <skipped />
                </testcase>
              </testsuite>
              <testsuite name="Billing cross-domain seeding" tests="1" failures="0" skipped="0" time="0.1">
                <testcase classname="Billing cross-domain seeding" name="Invoice for an externally-owned customer" time="0.1" />
              </testsuite>
            </testsuites>
            """;
        JUnitReport.Render(Fixture()).ReplaceLineEndings().Should().Be(expected.ReplaceLineEndings());
    }

    [Fact]
    public void Sarif_AbsolutePaths_MadeRelativeToBaseDirectory()
    {
        var manifest = Fixture();
        var baseDirectory = Path.Combine(Path.GetTempPath(), "repo");
        manifest.Scenarios[0].FeatureFile = Path.Combine(baseDirectory, "features", "orders-seeding.feature");

        SarifReport.Render(manifest, baseDirectory)
            .Should().Contain("\"uri\": \"features/orders-seeding.feature\"");
    }

    [Fact]
    public void Sarif_InlineFeatures_EmitNoLocation()
    {
        var manifest = Fixture();
        foreach (var scenario in manifest.Scenarios) scenario.FeatureFile = "<inline>";
        SarifReport.Render(manifest).Should().NotContain("physicalLocation");
    }

    [Fact]
    public void ReportEmitter_ParsesSpecs_AndRejectsBadOnes()
    {
        ReportEmitter.ParseSpec("sarif=./out/x.sarif").Should().Be(("sarif", "./out/x.sarif"));
        ReportEmitter.ParseSpec("JUNIT=results.xml").Should().Be(("junit", "results.xml"));
        ReportEmitter.ParseSpec("html=report.html").Should().Be(("html", "report.html"));

        foreach (var bad in new[] { "sarif", "=x", "csv=x.csv", "sarif=" })
        {
            FluentActions.Invoking(() => ReportEmitter.ParseSpec(bad))
                .Should().Throw<ArgumentException>().Which.Message.Should().Contain("sarif, junit, html");
        }
    }

    [Fact]
    public void ReportEmitter_WritesFile_CreatingDirectories()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tdm-report-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var written = ReportEmitter.Write(Fixture(), "junit", Path.Combine(directory, "nested", "run.xml"));
            File.ReadAllText(written).Should().Contain("<testsuites name=\"golden-run\"");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }
}
