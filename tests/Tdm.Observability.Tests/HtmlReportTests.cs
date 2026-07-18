using AwesomeAssertions;
using Tdm.Core.Manifest;
using Tdm.Observability.Reports;
using Xunit;

namespace Tdm.Observability.Tests;

/// <summary>
/// The living-doc HTML report (W4-D1) is a pure function over RunManifest, but its output is
/// too layout-heavy for byte-golden comparison — these tests pin the structural contract
/// instead: self-containment, escaping, the cross-domain lineage merge, bulk aggregation,
/// benchmark bars and trend sparklines.
/// </summary>
public class HtmlReportTests
{
    private static readonly string AcmeId = Tdm.Identity.TdmIdentity
        .ForNaturalKey("Orders", "Customer", "Acme Ltd").ToString();

    /// <summary>The acceptance-criteria shape (§5): an Orders customer, and a Billing invoice
    /// whose lineage runs back to it via the identity contract.</summary>
    private static RunManifest CrossDomainFixture() => new()
    {
        Run = new RunInfo
        {
            Name = "cross-domain",
            StartedUtc = new DateTime(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc),
            DurationMs = 850,
            TdmVersion = "0.1.0+abc1234",
            Outcome = RunOutcome.Succeeded,
            Environment = "ci",
            Benchmark = new Dictionary<string, BenchmarkStats>
            {
                ["create"] = new() { Count = 3, P50Ms = 10, P95Ms = 40, MaxMs = 45, MeanMs = 18, TotalMs = 54 },
                ["create:Customer"] = new() { Count = 1, P50Ms = 12, P95Ms = 12, MaxMs = 12, MeanMs = 12, TotalMs = 12 },
            },
        },
        Scenarios =
        [
            new ScenarioManifest
            {
                Feature = "Orders seeding",
                Scenario = "Customer exists",
                Seed = 1234,
                Outcome = ScenarioOutcome.Succeeded,
                Entities =
                [
                    new EntityManifest
                    {
                        Ordinal = 1, Entity = "Customer", Verb = "Create", Domain = "Orders",
                        Id = AcmeId, NaturalKey = "Acme Ltd",
                        Values = new Dictionary<string, string?> { ["Name"] = "Acme Ltd", ["Tier"] = "Gold" },
                        OverridesApplied = ["Tier"],
                    },
                ],
            },
            new ScenarioManifest
            {
                Feature = "Billing seeding",
                Scenario = "Invoice for an externally-owned customer",
                Seed = 1234,
                Outcome = ScenarioOutcome.Succeeded,
                Entities =
                [
                    new EntityManifest
                    {
                        Ordinal = 1, Entity = "Invoice", Verb = "Create", Domain = "Billing",
                        Id = "77", NaturalKey = "INV-001",
                    },
                ],
                References =
                [
                    // The external declaration publishes the identity (no source entity)…
                    new ReferenceManifest
                    {
                        Step = 3, Target = "Customer:Acme Ltd", ResolvedFrom = "identityContract",
                        Id = AcmeId, OwningDomain = "Orders",
                    },
                    // …and the invoice's reference clause consumes it from the context bag.
                    new ReferenceManifest
                    {
                        Step = 4, SourceOrdinal = 1, Target = "Customer:Acme Ltd",
                        ResolvedFrom = "contextBag", Id = AcmeId,
                    },
                ],
            },
        ],
    };

    private static string LineageSvg(string html)
    {
        var start = html.IndexOf("<svg class=\"lineage\"", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "the report should contain the lineage SVG");
        return html[start..html.IndexOf("</svg>", start, StringComparison.Ordinal)];
    }

    [Fact]
    public void Lineage_MergesCrossDomainTargetOntoOwningNode()
    {
        var svg = LineageSvg(HtmlReport.Render(CrossDomainFixture()));

        // Two nodes only: the Billing invoice and the Orders customer. The reference target
        // merged onto the created customer via the shared identity-contract id — no third
        // dashed "external" node.
        svg.Should().Contain("Invoice").And.Contain("Customer");
        System.Text.RegularExpressions.Regex.Matches(svg, "<rect ").Count.Should().Be(2);
        svg.Should().NotContain("external");

        // One edge, labelled with resolvedFrom.
        svg.Should().Contain("edge edge-ctx").And.Contain(">contextBag<");
    }

    [Fact]
    public void Lineage_UnseededTarget_RendersDashedExternalNode()
    {
        var manifest = CrossDomainFixture();
        manifest.Scenarios.RemoveAt(0); // the Orders run that owns the customer didn't happen here

        var svg = LineageSvg(HtmlReport.Render(manifest));
        System.Text.RegularExpressions.Regex.Matches(svg, "<rect ").Count.Should().Be(2);
        svg.Should().Contain("external");
    }

    [Fact]
    public void Lineage_BulkCreates_CollapseToOneAggregateNode()
    {
        var manifest = CrossDomainFixture();
        manifest.Scenarios[0].Entities.AddRange(Enumerable.Range(2, 3).Select(i => new EntityManifest
        {
            Ordinal = i, Entity = "Product", Verb = "Create", Domain = "Orders", Id = $"p{i}",
        }));
        manifest.Scenarios[0].BulkOperations =
        [
            new BulkOperationManifest { Entity = "Product", Domain = "Orders", Requested = 500, Count = 500, SampledRows = 3, HashedRows = 497 },
        ];

        var svg = LineageSvg(HtmlReport.Render(manifest));
        svg.Should().Contain("500 &#215; Product"); // "500 × Product", HTML-encoded
        // Customer + Invoice + one aggregate node — not 3 sampled Product nodes.
        System.Text.RegularExpressions.Regex.Matches(svg, "<rect ").Count.Should().Be(3);
    }

    [Fact]
    public void Report_IsSelfContained_NoScriptsNoExternalAssets()
    {
        var html = HtmlReport.Render(CrossDomainFixture());
        html.Should().StartWith("<!DOCTYPE html>");
        html.Should().NotContain("<script");
        html.Should().NotContain("<link");
        html.Should().NotContain("http://").And.NotContain("https://");
    }

    [Fact]
    public void Report_EscapesManifestValues()
    {
        var manifest = CrossDomainFixture();
        manifest.Scenarios[0].Entities[0].Values["Name"] = "<script>alert('x')</script>";
        manifest.Scenarios[0].Scenario = "Tags & <angles>";

        var html = HtmlReport.Render(manifest);
        html.Should().NotContain("<script");
        html.Should().Contain("&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt;");
        html.Should().Contain("Tags &amp; &lt;angles&gt;");
    }

    [Fact]
    public void Report_ShowsHeaderFacts_DrillDown_AndOverrides()
    {
        var html = HtmlReport.Render(CrossDomainFixture(), new HtmlReportOptions
        {
            SignatureStatus = "Checksum OK. Signature OK.",
        });

        html.Should().Contain("cross-domain");
        html.Should().Contain("Succeeded");
        html.Should().Contain("env: ci");
        html.Should().Contain("Checksum OK. Signature OK.");
        html.Should().Contain("seed 1234");
        // Per-entity final values with the applied override marked (§2.1 drill-down).
        html.Should().Contain("Acme Ltd").And.Contain("Gold");
        html.Should().Contain("badge override");
    }

    [Fact]
    public void Report_DefaultSignatureStatus_SaysNotVerified()
    {
        HtmlReport.Render(CrossDomainFixture()).Should().Contain("not verified");
    }

    [Fact]
    public void Report_RendersBenchmarkBars()
    {
        var html = HtmlReport.Render(CrossDomainFixture());
        html.Should().Contain("<h2>Benchmarks</h2>");
        html.Should().Contain("create:Customer");
        // p95 of "create" is the scale max → full-width bar; p50 10/40 → 25%.
        html.Should().Contain("class=\"bar p95\" style=\"width:100%\"");
        html.Should().Contain("class=\"bar p50\" style=\"width:25%\"");
    }

    [Fact]
    public void Report_WithoutTrendHistory_HasNoTrendSection()
    {
        HtmlReport.Render(CrossDomainFixture()).Should().NotContain("<h2>Trends</h2>");
    }

    [Fact]
    public void Report_WithTrendHistory_RendersSparklinesAndDelta()
    {
        var history = Enumerable.Range(0, 3).Select(i =>
        {
            var manifest = CrossDomainFixture();
            manifest.Run.StartedUtc = manifest.Run.StartedUtc.AddDays(i - 3);
            manifest.Run.DurationMs = 800 + i * 10;
            manifest.Run.Benchmark["create"].P95Ms = 20; // baseline median 20 vs current 40
            return manifest;
        }).ToList();

        var html = HtmlReport.Render(CrossDomainFixture(), new HtmlReportOptions { TrendHistory = history });

        html.Should().Contain("<h2>Trends</h2>");
        html.Should().Contain("svg class=\"spark\"");
        html.Should().Contain("run duration");
        // create: current 40 vs median 20 → +100%, flagged as a regression.
        html.Should().Contain("worse\">+100%");
    }

    [Fact]
    public void ReportEmitter_AcceptsHtmlFormat()
    {
        ReportEmitter.ParseSpec("html=out/run.html").Should().Be(("html", "out/run.html"));

        var directory = Path.Combine(Path.GetTempPath(), "tdm-html-report-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var written = ReportEmitter.Write(CrossDomainFixture(), "html", Path.Combine(directory, "run.html"));
            File.ReadAllText(written).Should().StartWith("<!DOCTYPE html>");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }
}
