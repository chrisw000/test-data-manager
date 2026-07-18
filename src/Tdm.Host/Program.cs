using System.CommandLine;
using Microsoft.Extensions.Logging;
using Tdm.Core.Execution;
using Tdm.Core.Grammar;
using Tdm.Core.Manifest;
using Tdm.Core.Settings;
using Tdm.EfCore;
using Tdm.EfCore.Providers;
using Tdm.Host;
using Tdm.Observability;
using Tdm.Plugins;

var settingsOption = new Option<string>("--settings", "-s")
{
    Description = "Path to tdm.settings.json",
    DefaultValueFactory = _ => "tdm.settings.json",
};
var seedOption = new Option<int?>("--seed") { Description = "Override run.defaultSeed" };
var policyOption = new Option<FailurePolicy?>("--policy") { Description = "Override run.failurePolicy" };
var lifecycleOption = new Option<LifecycleMode?>("--lifecycle") { Description = "Override run.lifecycle" };
var benchmarkOption = new Option<bool?>("--benchmark") { Description = "Override run.benchmark" };
var manifestOption = new Option<string>("--manifest") { Description = "Path to a .tdm.json manifest", Required = true };
var domainOption = new Option<string?>("--domain") { Description = "Restrict to one domain" };
var updatePluginsOption = new Option<bool>("--update-plugins")
{
    Description = "Re-resolve plugin package versions, ignoring tdm.plugins.lock.json",
};
var reportOption = new Option<string[]>("--report")
{
    Description = "Additionally emit the manifest as <format>=<path>; formats: sarif, junit, html. Repeatable.",
    Arity = ArgumentArity.ZeroOrMore,
};
var envOption = new Option<string?>("--env")
{
    Description = "Target environment name — enables tdm.policy.json enforcement for this run (W2-D3).",
};
var policyFileOption = new Option<string>("--policy-file")
{
    Description = "Path to tdm.policy.json (only read when --env is given)",
    DefaultValueFactory = _ => "tdm.policy.json",
};
var approvalOption = new Option<string?>("--approval")
{
    Description = "Approval token to override environment-policy violations (W2-D4); validated against the environment's configured secret.",
};
var resumeOption = new Option<string?>("--resume")
{
    Description = "Path to a previous run's .tdm.journal.jsonl — scenarios/rows it records persisted are skipped (W3-D6). The plan and seeds must match the interrupted run.",
};

var root = new RootCommand("TDM — Gherkin-driven Test Data Manager");

var runCommand = new Command("run", "Parse feature files, seed data, write the run manifest (and a crash-safe .tdm.journal.jsonl).")
{
    settingsOption, seedOption, policyOption, lifecycleOption, benchmarkOption, updatePluginsOption, reportOption,
    envOption, policyFileOption, approvalOption, resumeOption,
};
runCommand.SetAction(async (parseResult, ct) =>
{
    var reports = ParseReports(parseResult.GetValue(reportOption));
    var composer = HostComposer.Create(parseResult.GetValue(settingsOption)!,
        parseResult.GetValue(seedOption), parseResult.GetValue(policyOption),
        parseResult.GetValue(lifecycleOption), parseResult.GetValue(benchmarkOption),
        parseResult.GetValue(updatePluginsOption), parseResult.GetValue(envOption),
        parseResult.GetValue(policyFileOption)!, parseResult.GetValue(approvalOption),
        parseResult.GetValue(resumeOption));
    return await composer.RunAsync(dryRun: false, reports, ct);
});

var validateCommand = new Command("validate", "Parse features and resolve entities/fakers/repositories; persist nothing (CI dry run).")
{
    settingsOption, policyOption, updatePluginsOption, reportOption,
    envOption, policyFileOption, approvalOption,
};
validateCommand.SetAction(async (parseResult, ct) =>
{
    var reports = ParseReports(parseResult.GetValue(reportOption));
    var composer = HostComposer.Create(parseResult.GetValue(settingsOption)!,
        seed: null, parseResult.GetValue(policyOption), lifecycle: null, benchmark: null,
        parseResult.GetValue(updatePluginsOption), parseResult.GetValue(envOption),
        parseResult.GetValue(policyFileOption)!, parseResult.GetValue(approvalOption));
    return await composer.RunAsync(dryRun: true, reports, ct);
});

// Fail fast on malformed --report specs, before any plugin/database work.
static IReadOnlyList<(string Format, string Path)> ParseReports(string[]? specs) =>
    (specs ?? []).Select(Tdm.Observability.Reports.ReportEmitter.ParseSpec).ToList();

var teardownCommand = new Command("teardown", "Delete rows recorded in a manifest, in reverse dependency order.")
{
    settingsOption, manifestOption,
};
teardownCommand.SetAction(async (parseResult, ct) =>
{
    var composer = HostComposer.Create(parseResult.GetValue(settingsOption)!, null, null, null, null);
    return await composer.TeardownAsync(parseResult.GetValue(manifestOption)!, ct);
});

var listEntitiesCommand = new Command("list-entities", "Print the resolved entity/repository/faker map (convention debugging).")
{
    settingsOption, domainOption,
};
listEntitiesCommand.SetAction(async (parseResult, ct) =>
{
    var composer = HostComposer.Create(parseResult.GetValue(settingsOption)!, null, null, null, null);
    return await composer.ListEntitiesAsync(parseResult.GetValue(domainOption), ct);
});

var initDomainOption = new Option<string?>("--domain") { Description = "Domain name to pre-fill (default: MyDomain)" };
var initPackageOption = new Option<string?>("--package") { Description = "NuGet package id of the domain data assembly to pre-fill" };
var initDirOption = new Option<string>("--dir") { Description = "Target directory", DefaultValueFactory = _ => "." };
var initCommand = new Command("init", "Scaffold tdm.settings.json, a starter feature, .gitignore and a CI validate workflow.")
{
    initDomainOption, initPackageOption, initDirOption,
};
initCommand.SetAction(parseResult => InitScaffolder.Execute(
    Path.GetFullPath(parseResult.GetValue(initDirOption)!),
    parseResult.GetValue(initDomainOption), parseResult.GetValue(initPackageOption)));

var exportOutOption = new Option<string>("--out")
{
    Description = "Output path for the model file",
    DefaultValueFactory = _ => "tdm.model.json",
};
var exportModelCommand = new Command("export-model",
    "Serialise the resolved entity map (logical names, properties, natural keys, domains) to tdm.model.json (W4-D2) — " +
    "the offline model `tdm lsp` validates against. Deterministic output: regenerate in CI and fail on diff to catch staleness.")
{
    settingsOption, exportOutOption,
};
exportModelCommand.SetAction(async (parseResult, ct) =>
{
    var composer = HostComposer.Create(parseResult.GetValue(settingsOption)!, null, null, null, null);
    return await composer.ExportModelAsync(parseResult.GetValue(exportOutOption)!, ct);
});

var lspModelOption = new Option<string>("--model")
{
    Description = "Path to the exported model file (see `tdm export-model`); reloaded automatically when it changes",
    DefaultValueFactory = _ => "tdm.model.json",
};
var lspCommand = new Command("lsp",
    "Run the TDM language server on stdio (W4-D2/W4-D3): live StepGrammar diagnostics, entity/property completion " +
    "and verb hover for feature files, validated against tdm.model.json — no database connection. " +
    "Launched by editor clients (VS Code extension in editors/vscode), not interactively.")
{
    lspModelOption,
};
lspCommand.SetAction(async (parseResult, ct) =>
{
    var server = new Tdm.Lsp.LspServer(Console.OpenStandardInput(), Console.OpenStandardOutput(),
        Path.GetFullPath(parseResult.GetValue(lspModelOption)!));
    await server.RunAsync(ct);
    return 0;
});

var profileSampleOption = new Option<int>("--sample")
{
    Description = "Rows sampled per entity (upper bound)",
    DefaultValueFactory = _ => 10_000,
};
var categoricalMaxOption = new Option<int>("--categorical-max")
{
    Description = "Columns with at most this many distinct values are treated as categorical (weights captured)",
    DefaultValueFactory = _ => 10,
};
var noValuesOption = new Option<bool>("--no-values")
{
    Description = "Suppress category labels entirely — cardinalities and numeric shapes only",
};
var profileOutOption = new Option<string>("--out")
{
    Description = "Statistics pack output path",
    DefaultValueFactory = _ => "tdm.stats.json",
};
var fragmentOption = new Option<string?>("--fragment")
{
    Description = "Also emit an entities-config fragment (the seed-pack fragment shape) with the suggested distributions/weights",
};
var profileCommand = new Command("profile",
    "W4-D8 spike (prototype, not GA): connect read-only to a production-like source and emit a statistics pack — " +
    "per-column distributions, cardinalities, correlation hints, never row values. Feed the fragment into " +
    "entities.{X}.properties (see docs/subsetting-spike.md; data-protection review required before use on real production data).")
{
    settingsOption, domainOption, profileSampleOption, categoricalMaxOption, noValuesOption, profileOutOption, fragmentOption,
};
profileCommand.SetAction(async (parseResult, ct) =>
{
    var composer = HostComposer.Create(parseResult.GetValue(settingsOption)!, null, null, null, null);
    return await composer.ProfileAsync(
        parseResult.GetValue(domainOption),
        new Tdm.EfCore.Profiling.ProfileOptions(
            parseResult.GetValue(profileSampleOption),
            parseResult.GetValue(categoricalMaxOption),
            IncludeValues: !parseResult.GetValue(noValuesOption)),
        parseResult.GetValue(profileOutOption)!,
        parseResult.GetValue(fragmentOption), ct);
});

var stepArgument = new Argument<string>("step") { Description = "The step text, e.g. \"an Order exists for Customer \\\"Acme Ltd\\\"\"" };
var keywordOption = new Option<string>("--keyword") { Description = "Gherkin keyword context", DefaultValueFactory = _ => "Given" };
var explainCommand = new Command("explain", "Explain every pipeline decision for a single step: grammar rule, entity resolution, faker, persistence route, identity. No database connection.")
{
    stepArgument, settingsOption, keywordOption,
};
explainCommand.SetAction(async (parseResult, ct) =>
{
    var composer = HostComposer.Create(parseResult.GetValue(settingsOption)!, null, null, null, null);
    return await composer.ExplainAsync(parseResult.GetValue(keywordOption)!, parseResult.GetValue(stepArgument)!, ct);
});

var replayCommand = new Command("replay",
    "Re-create exactly the rows a manifest records — final values, not fakers (W2-D9). Idempotent; only Persistent scenarios play back.")
{
    settingsOption, manifestOption,
};
replayCommand.SetAction(async (parseResult, ct) =>
{
    var composer = HostComposer.Create(parseResult.GetValue(settingsOption)!, null, null, null, null);
    return await composer.ReplayAsync(parseResult.GetValue(manifestOption)!, ct);
});

var verifyCommand = new Command("verify",
    "Drift check (W2-D9): assert every manifest-recorded row still exists with its recorded values. Exit 0 no drift, 1 drift. (File integrity is `tdm manifest verify`.)")
{
    settingsOption, manifestOption,
};
verifyCommand.SetAction(async (parseResult, ct) =>
{
    var composer = HostComposer.Create(parseResult.GetValue(settingsOption)!, null, null, null, null);
    return await composer.VerifyDriftAsync(parseResult.GetValue(manifestOption)!, ct);
});

var manifestFileArgument = new Argument<string>("file") { Description = "Path to a .tdm.json manifest" };
var verifyCertOption = new Option<string?>("--cert")
{
    Description = "Public certificate (.cer/.pem) to verify a detached signature, if one is present",
};
var manifestVerifyCommand = new Command("verify",
    "Verify a manifest's checksum and, if present, its signature (W2-D2). Exit 0 fully verified, 1 partially (signature present but --cert not given), 2 failed/tampered.")
{
    manifestFileArgument, verifyCertOption,
};
manifestVerifyCommand.SetAction(parseResult =>
{
    var outcome = Tdm.Observability.Audit.ManifestSigner.Verify(
        parseResult.GetValue(manifestFileArgument)!, parseResult.GetValue(verifyCertOption));
    Console.WriteLine(outcome.Message);
    return outcome.ExitCode;
});
var manifestCommand = new Command("manifest", "Manifest integrity utilities.") { manifestVerifyCommand };

var storeOption = new Option<string>("--store")
{
    Description = "Trend store root (W3-D7): a directory path — local, network share, or blob storage mounted/synced by CI.",
    Required = true,
};
var publishCommand = new Command("publish",
    "Push a manifest to the trend store under {env}/{run-name}/{timestamp}, maintaining index.json (W3-D7). Baselines for `tdm bench compare` read from here.")
{
    manifestOption, storeOption, envOption,
};
publishCommand.SetAction(async (parseResult, ct) =>
{
    var manifest = ManifestWriter.Read(Path.GetFullPath(parseResult.GetValue(manifestOption)!));
    var environment = parseResult.GetValue(envOption) ?? manifest.Run.Environment ?? "default";
    var store = new Tdm.Observability.Trends.FileSystemTrendStore(parseResult.GetValue(storeOption)!);
    var relative = await store.PublishAsync(manifest, environment, ct);
    Console.WriteLine($"Published: {relative}");
    return 0;
});

var tuneEntityOption = new Option<string?>("--entity") { Description = "Entity to bulk-insert (default: first entity with a client-set single-column key)" };
var tuneRowsOption = new Option<int>("--rows") { Description = "Rows inserted per measurement", DefaultValueFactory = _ => 2000 };
var tuneChunksOption = new Option<string>("--chunk-sizes")
{
    Description = "Comma-separated chunk sizes to measure",
    DefaultValueFactory = _ => "100,250,500,1000,2000",
};
var tuneNoWriteOption = new Option<bool>("--no-write") { Description = "Report the best chunk size without updating tdm.settings.json" };
var benchTuneCommand = new Command("tune",
    "Measure bulk-insert throughput across a matrix of chunk sizes against the target database and write the best into run.bulkChunkSize (W3-D3). Inserts and then deletes --rows rows per chunk size — point it at a dev database.")
{
    settingsOption, domainOption, tuneEntityOption, tuneRowsOption, tuneChunksOption, tuneNoWriteOption,
};
benchTuneCommand.SetAction(async (parseResult, ct) =>
{
    var composer = HostComposer.Create(parseResult.GetValue(settingsOption)!, null, null, null, null);
    return await composer.BenchTuneAsync(
        parseResult.GetValue(domainOption), parseResult.GetValue(tuneEntityOption),
        parseResult.GetValue(tuneRowsOption), parseResult.GetValue(tuneChunksOption)!,
        write: !parseResult.GetValue(tuneNoWriteOption), ct);
});
var compareBaselineOption = new Option<string?>("--baseline")
{
    Description = "Pinned baseline manifest (.tdm.json). Mutually exclusive with --store.",
};
var compareStoreOption = new Option<string?>("--store")
{
    Description = "Trend store root (W3-D7): baseline = per-stat rolling median of the last --baseline-runs stored runs for this run name + environment.",
};
var baselineRunsOption = new Option<int>("--baseline-runs")
{
    Description = "How many stored runs the rolling-median baseline uses",
    DefaultValueFactory = _ => 5,
};
var compareStatOption = new Option<string>("--stat")
{
    Description = "Stat shown in the comparison table: meanMs | p50Ms | p95Ms | maxMs | totalMs",
    DefaultValueFactory = _ => "p95Ms",
};
var quarantineOption = new Option<bool>("--quarantine")
{
    Description = "Report gate failures without failing the pipeline (noisy-agent escape hatch).",
};
var benchCompareCommand = new Command("compare",
    "Compare a run's benchmark stats against a baseline and evaluate the policy file's perf gates (W3-D8): " +
    "exit 0 when every gate holds, 2 on regression. Compare before publishing the current manifest to the store.")
{
    manifestOption, compareBaselineOption, compareStoreOption, baselineRunsOption,
    envOption, policyFileOption, compareStatOption, quarantineOption, reportOption,
};
benchCompareCommand.SetAction(async (parseResult, ct) =>
{
    var reports = ParseReports(parseResult.GetValue(reportOption));
    return await BenchCompare.ExecuteAsync(
        parseResult.GetValue(manifestOption)!,
        parseResult.GetValue(compareBaselineOption),
        parseResult.GetValue(compareStoreOption),
        parseResult.GetValue(baselineRunsOption),
        parseResult.GetValue(envOption),
        parseResult.GetValue(policyFileOption)!,
        parseResult.GetValue(compareStatOption)!,
        parseResult.GetValue(quarantineOption),
        reports, ct);
});
var benchCommand = new Command("bench", "Benchmark utilities.") { benchTuneCommand, benchCompareCommand };

var htmlOutOption = new Option<string?>("--html")
{
    Description = "Output path (default: next to the manifest, .html for .tdm.json)",
};
var reportStoreOption = new Option<string?>("--store")
{
    Description = "Trend store root (W3-D7) — adds trend sparklines from the stored history of this run name + environment.",
};
var trendRunsOption = new Option<int>("--trend-runs")
{
    Description = "How many stored runs the trend sparklines cover",
    DefaultValueFactory = _ => 10,
};
var trendStatOption = new Option<string>("--stat")
{
    Description = "Stat charted in the trend sparklines: meanMs | p50Ms | p95Ms | maxMs | totalMs",
    DefaultValueFactory = _ => "p95Ms",
};
var reportCommand = new Command("report",
    "Render a manifest as a single self-contained HTML file (W4-D1): run header, scenario drill-down, " +
    "reference lineage graph, benchmark charts and (with --store) trend sparklines. No server, no external assets.")
{
    manifestOption, htmlOutOption, reportStoreOption, envOption, trendRunsOption, trendStatOption, verifyCertOption,
};
reportCommand.SetAction(async (parseResult, ct) =>
{
    var manifestPath = Path.GetFullPath(parseResult.GetValue(manifestOption)!);
    var manifest = ManifestWriter.Read(manifestPath);
    var options = new Tdm.Observability.Reports.HtmlReportOptions
    {
        TrendStat = parseResult.GetValue(trendStatOption)!,
        SignatureStatus = Tdm.Observability.Audit.ManifestSigner
            .Verify(manifestPath, parseResult.GetValue(verifyCertOption)).Message,
    };
    if (parseResult.GetValue(reportStoreOption) is { } storeRoot)
    {
        var store = new Tdm.Observability.Trends.FileSystemTrendStore(storeRoot);
        var environment = parseResult.GetValue(envOption) ?? manifest.Run.Environment ?? "default";
        options.TrendHistory = await store.ReadRecentAsync(environment, manifest.Run.Name,
            parseResult.GetValue(trendRunsOption), ct);
    }
    var outPath = parseResult.GetValue(htmlOutOption) is { } explicitOut
        ? Path.GetFullPath(explicitOut)
        : manifestPath.EndsWith(".tdm.json", StringComparison.OrdinalIgnoreCase)
            ? manifestPath[..^".tdm.json".Length] + ".html"
            : manifestPath + ".html";
    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
    File.WriteAllText(outPath, Tdm.Observability.Reports.HtmlReport.Render(manifest, options));
    Console.WriteLine($"Report written: {outPath}");
    return 0;
});

root.Subcommands.Add(runCommand);
root.Subcommands.Add(validateCommand);
root.Subcommands.Add(teardownCommand);
root.Subcommands.Add(listEntitiesCommand);
root.Subcommands.Add(initCommand);
root.Subcommands.Add(explainCommand);
root.Subcommands.Add(manifestCommand);
root.Subcommands.Add(replayCommand);
root.Subcommands.Add(verifyCommand);
root.Subcommands.Add(benchCommand);
root.Subcommands.Add(publishCommand);
root.Subcommands.Add(reportCommand);
root.Subcommands.Add(exportModelCommand);
root.Subcommands.Add(lspCommand);
root.Subcommands.Add(profileCommand);

try
{
    return await root.Parse(args).InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false });
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    // Configuration/environment errors carry actionable messages — show them without a stack trace.
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}

namespace Tdm.Host
{
    /// <summary>Composition root: settings → plugins → domain runtimes → engine → sinks.</summary>
    internal sealed class HostComposer
    {
        private readonly TdmSettings _settings;
        private readonly string _settingsFilePath;
        private readonly string _baseDirectory;
        private readonly bool _updatePlugins;
        private readonly string? _environmentName;
        private readonly string _policyFilePath;
        private readonly string? _approvalToken;
        private readonly string? _resumeJournalPath;
        private IReadOnlyList<Tdm.Core.SeedPacks.SeedPackContent>? _seedPacks;
        private readonly Tdm.Core.Secrets.SecretChain _secrets;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _log;

        private HostComposer(TdmSettings settings, string settingsFilePath, string baseDirectory, bool updatePlugins,
            string? environmentName, string policyFilePath, string? approvalToken, string? resumeJournalPath)
        {
            _settings = settings;
            _settingsFilePath = settingsFilePath;
            _baseDirectory = baseDirectory;
            _updatePlugins = updatePlugins;
            _environmentName = environmentName;
            _policyFilePath = policyFilePath;
            _approvalToken = approvalToken;
            _resumeJournalPath = resumeJournalPath;
            // Fails fast when settings name a cloud provider without a registered adapter (W2-D8).
            _secrets = Tdm.Core.Secrets.SecretChainFactory.Create(settings.Secrets);
            _loggerFactory = LoggerFactory.Create(builder => builder
                .AddSimpleConsole(o =>
                {
                    o.SingleLine = true;
                    o.TimestampFormat = "HH:mm:ss ";
                })
                .SetMinimumLevel(LogLevel.Information));
            _log = _loggerFactory.CreateLogger("Tdm");
        }

        public static HostComposer Create(string settingsPath, int? seed, FailurePolicy? policy,
            LifecycleMode? lifecycle, bool? benchmark, bool updatePlugins = false,
            string? environmentName = null, string policyFilePath = "tdm.policy.json", string? approvalToken = null,
            string? resumeJournalPath = null)
        {
            var fullPath = Path.GetFullPath(settingsPath);
            var settings = TdmSettings.Load(fullPath);
            if (seed is { } s) settings.Run.DefaultSeed = s;
            if (policy is { } p) settings.Run.FailurePolicy = p;
            if (lifecycle is { } l) settings.Run.Lifecycle = l;
            if (benchmark is { } b) settings.Run.Benchmark = b;
            return new HostComposer(settings, fullPath, Path.GetDirectoryName(fullPath)!, updatePlugins,
                environmentName, policyFilePath, approvalToken, resumeJournalPath);
        }

        public async Task<int> RunAsync(bool dryRun, CancellationToken ct) =>
            await RunAsync(dryRun, [], ct);

        public async Task<int> RunAsync(bool dryRun, IReadOnlyList<(string Format, string Path)> reports, CancellationToken ct)
        {
            using var otel = OtelBootstrap.Start(_settings.Run.Name);
            var (runtimes, plugins) = await BuildRuntimesAsync(ct);
            try
            {
                // Write-repository policy gate (ADR-0001): refuse before touching any data.
                var violations = runtimes.SelectMany(r => r.PolicyViolations).ToList();
                if (violations.Count > 0)
                {
                    foreach (var violation in violations)
                        _log.LogError("Policy violation: {Violation}", violation);
                    _log.LogError("{Count} write-repository policy violation(s) — fix the repositories or add explicit " +
                                  "entities.{{Name}}.requireRepository: false exemptions in the settings file (see docs/adr-0001).",
                        violations.Count);
                    return 2;
                }

                // Pack features run before local ones: pack list order, alphabetical within (W4-D7).
                var plan = Tdm.Core.SeedPacks.SeedPackApplier.BuildPlan(
                    new GherkinPlanParser(), _seedPacks ?? [], _settings.Run.FeaturePaths, _baseDirectory);
                var totalScenarios = plan.Features.Sum(f => f.Scenarios.Count);
                _log.LogInformation("{Mode} {Features} feature file(s), {Scenarios} scenario(s)",
                    dryRun ? "Validating" : "Running", plan.Features.Count, totalScenarios);

                // Everything below is statically known from the parsed plan — checked before
                // any execution/persistence (W2-D3, W2-D6).
                var (policyViolations, overrideApplied) = EvaluatePrePersistencePolicy(plan, plugins);
                if (policyViolations.Count > 0 && !overrideApplied)
                {
                    foreach (var violation in policyViolations)
                        _log.LogError("Policy violation [{Rule}]: {Message}", violation.Rule, violation.Message);
                    _log.LogError("{Count} policy violation(s) — refusing to {Mode}.",
                        policyViolations.Count, dryRun ? "validate" : "run");

                    var refusal = new RunManifest
                    {
                        Run = new RunInfo
                        {
                            Name = _settings.Run.Name,
                            StartedUtc = DateTime.UtcNow,
                            Outcome = RunOutcome.Failed,
                            DryRun = dryRun,
                            Environment = _environmentName,
                            PolicyViolations = [.. policyViolations.Select(v => new PolicyViolationInfo { Rule = v.Rule, Message = v.Message })],
                            Attribution = Tdm.Observability.Audit.AttributionCollector.Collect(_settingsFilePath),
                        },
                    };
                    await WriteManifestArtifactsAsync(refusal, reports, ct);
                    return refusal.ExitCode;
                }
                if (overrideApplied)
                {
                    _log.LogWarning("{Count} environment policy violation(s) for '{Env}' overridden via --approval.",
                        policyViolations.Count, _environmentName);
                }

                // Run registry + environment locks (W2-D7): registered and leased before any
                // seeding, released on dispose. Validate never touches data, so no locks.
                var registryApiKey = string.IsNullOrEmpty(_settings.Registry.ApiKeyEnv)
                    ? null
                    : await _secrets.GetSecretAsync(_settings.Registry.ApiKeyEnv, ct);
                await using var registry = dryRun
                    ? null
                    : await RegistrySession.StartAsync(_settings, _environmentName,
                        Tdm.Observability.Audit.AttributionCollector.DetectRunnerId(
                            Environment.GetEnvironmentVariable, Environment.UserName), registryApiKey, _log, ct);
                if (registry?.RefusalReason is { } refusalReason)
                {
                    _log.LogError("Registry refusal: {Reason}", refusalReason);
                    return 2;
                }

                // Crash-safe JSONL journal (W3-D6): written for every real run, alongside the
                // end-of-run manifest. --resume replays a previous journal's progress.
                Tdm.Core.Journal.ResumeState? resumeState = null;
                if (_resumeJournalPath is not null)
                {
                    var resumePath = Path.GetFullPath(_resumeJournalPath, _baseDirectory);
                    if (!File.Exists(resumePath))
                        throw new InvalidOperationException($"--resume journal not found: {resumePath}");
                    resumeState = Tdm.Core.Journal.ResumeState.Load(resumePath);
                    _log.LogInformation("Resuming from journal {Path}", resumePath);
                }
                using var journal = dryRun
                    ? null
                    : new Tdm.Core.Journal.RunJournalWriter(JournalPath());
                if (journal is not null)
                    _log.LogInformation("Journal: {Path}", journal.FilePath);

                var engine = new TdmEngine(_settings, runtimes, _loggerFactory.CreateLogger<TdmEngine>(),
                    journal: journal, resume: resumeState);
                var manifest = await engine.RunAsync(plan, dryRun, ct);
                manifest.Run.Environment = _environmentName;
                manifest.Run.RegistryRunId = registry?.RunId?.ToString();
                manifest.Run.PolicyOverrideApplied = overrideApplied;
                if (overrideApplied)
                {
                    // The override event is recorded, not just its flag — an audit trail
                    // needs to show what was bypassed, not only that something was (W2-D4).
                    manifest.Run.PolicyViolations = [.. policyViolations.Select(v => new PolicyViolationInfo { Rule = v.Rule, Message = v.Message })];
                }

                // Reproducibility down to the plugin version (W1-D2).
                foreach (var plugin in plugins)
                foreach (var (packageId, version) in plugin.Packages)
                    manifest.Run.PluginPackages[$"{plugin.DomainName}:{packageId}"] = version;
                foreach (var pack in _seedPacks ?? [])
                    manifest.Run.SeedPacks[pack.Name] = pack.Version;

                // Attribution captured into the manifest itself, not a side file — one audit
                // artifact, so signing covers it all at once (W2-D1).
                manifest.Run.Attribution = Tdm.Observability.Audit.AttributionCollector.Collect(_settingsFilePath);
                manifest.Run.Attribution.ResumedFrom = resumeState?.JournalPath;
                // Declared stats packs (W4-D8): production-derived shapes are audit-visible.
                manifest.Run.Attribution.StatsPacks = [.. _settings.StatsPacks.Select(path =>
                {
                    var full = Path.GetFullPath(path, _baseDirectory);
                    var hash = File.Exists(full)
                        ? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(full)))[..12]
                        : "missing";
                    return $"{Path.GetFileName(full)}:{hash}";
                })];

                var manifestFile = await WriteManifestArtifactsAsync(manifest, reports, ct);
                if (registry is not null)
                    await registry.FinishAsync(manifest.Run.Outcome.ToString(), manifestFile, ct);
                RunSummary.Log(_log, manifest);
                return manifest.ExitCode;
            }
            finally
            {
                await DisposeRuntimesAsync(runtimes, plugins);
            }
        }

        /// <summary>
        /// Key-registry checks (W2-D6) always run when a referenced domain has published
        /// tdm.keys.json — independent of --env. Environment-policy checks (W2-D3) only run
        /// when --env is given, against tdm.policy.json (--policy-file). Key-registry
        /// violations are never overridable; environment-policy violations are, via a
        /// validated --approval token (W2-D4).
        /// </summary>
        private (List<Tdm.Policy.PolicyViolation> Violations, bool OverrideApplied) EvaluatePrePersistencePolicy(
            SeedingPlan plan, List<LoadedPlugin> plugins)
        {
            var registries = plugins.Where(p => p.KeyRegistry is not null)
                .ToDictionary(p => p.DomainName, p => p.KeyRegistry!, StringComparer.OrdinalIgnoreCase);
            // Seed packs may carry key-registry entries too (W4-D7); a domain's own
            // plugin-published registry stays authoritative.
            var merged = Tdm.Core.SeedPacks.SeedPackApplier.CollectKeyRegistries(_seedPacks ?? [], registries);
            var violations = Tdm.Policy.KeyRegistryChecker.Check(plan, merged);
            if (violations.Count > 0) return (violations, false); // never overridable

            if (_environmentName is null) return (violations, false);

            var policyPath = Path.GetFullPath(_policyFilePath, _baseDirectory);
            if (!File.Exists(policyPath))
            {
                throw new InvalidOperationException(
                    $"--env '{_environmentName}' was given but no policy file was found at '{policyPath}'. " +
                    "Create tdm.policy.json or pass --policy-file.");
            }
            var policy = Tdm.Policy.PolicyDocument.Load(policyPath);
            var result = Tdm.Policy.PolicyEvaluator.Evaluate(policy, _environmentName, plan, _settings,
                _approvalToken, Environment.GetEnvironmentVariable);
            return (result.Violations, result.OverrideApplied);
        }

        /// <summary>Journal file next to where the manifest will land, named like it (W3-D6).</summary>
        private string JournalPath()
        {
            var outputPath = Path.GetFullPath(_settings.Run.OutputPath, _baseDirectory);
            var safeName = string.Join("-", _settings.Run.Name.Split(Path.GetInvalidFileNameChars(),
                StringSplitOptions.RemoveEmptyEntries));
            return Path.Combine(outputPath, $"{safeName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.tdm.journal.jsonl");
        }

        private async Task<string> WriteManifestArtifactsAsync(RunManifest manifest,
            IReadOnlyList<(string Format, string Path)> reports, CancellationToken ct)
        {
            var outputPath = Path.GetFullPath(_settings.Run.OutputPath, _baseDirectory);
            var manifestFile = ManifestWriter.Write(manifest, outputPath);
            _log.LogInformation("Manifest written: {Path}", manifestFile);

            var checksumPath = Tdm.Observability.Audit.ManifestSigner.WriteChecksum(manifestFile);
            _log.LogInformation("Checksum written: {Path}", checksumPath);
            if (_settings.Run.Signing is { } signing)
            {
                // Certificate password via the secret chain (W2-D8) — inline secrets and
                // registered adapters work as well as plain environment variables.
                var password = string.IsNullOrEmpty(signing.CertificatePasswordEnv)
                    ? null
                    : await _secrets.GetSecretAsync(signing.CertificatePasswordEnv, ct);
                var sigPath = Tdm.Observability.Audit.ManifestSigner.Sign(manifestFile, signing.CertificatePath, password);
                _log.LogInformation("Manifest signed: {Path}", sigPath);
            }

            foreach (var (format, path) in reports)
            {
                var written = Tdm.Observability.Reports.ReportEmitter.Write(manifest, format, path, _baseDirectory);
                _log.LogInformation("{Format} report written: {Path}", format, written);
            }
            return manifestFile;
        }

        public async Task<int> ReplayAsync(string manifestPath, CancellationToken ct)
        {
            var manifest = ManifestWriter.Read(Path.GetFullPath(manifestPath));
            var (runtimes, plugins) = await BuildRuntimesAsync(ct);
            try
            {
                var report = await ManifestPlayback.ReplayAsync(manifest, runtimes, _log, ct);
                foreach (var warning in report.Warnings) _log.LogWarning("{Warning}", warning);
                foreach (var failure in report.Failures) _log.LogError("{Failure}", failure);
                return report.ExitCode;
            }
            finally
            {
                await DisposeRuntimesAsync(runtimes, plugins);
            }
        }

        public async Task<int> VerifyDriftAsync(string manifestPath, CancellationToken ct)
        {
            var manifest = ManifestWriter.Read(Path.GetFullPath(manifestPath));
            var (runtimes, plugins) = await BuildRuntimesAsync(ct);
            try
            {
                var report = await ManifestPlayback.VerifyAsync(manifest, runtimes, _log, ct);
                foreach (var warning in report.Warnings) _log.LogWarning("{Warning}", warning);
                foreach (var drift in report.Drift) _log.LogError("DRIFT: {Drift}", drift);
                foreach (var error in report.Errors) _log.LogError("{Error}", error);
                return report.ExitCode;
            }
            finally
            {
                await DisposeRuntimesAsync(runtimes, plugins);
            }
        }

        /// <summary>
        /// `tdm bench tune` (W3-D3): inserts --rows rows of one entity at each candidate chunk
        /// size (TrackedTeardown, so every measurement cleans up set-based afterwards), ranks
        /// by throughput and writes the winner into run.bulkChunkSize — a targeted textual
        /// edit, so settings-file comments survive.
        /// </summary>
        public async Task<int> BenchTuneAsync(string? domainName, string? entityName, int rows,
            string chunkSizesCsv, bool write, CancellationToken ct)
        {
            var chunkSizes = chunkSizesCsv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(int.Parse).Where(c => c > 0).Distinct().ToArray();
            if (chunkSizes.Length == 0) throw new InvalidOperationException("--chunk-sizes contained no positive numbers.");
            if (rows < chunkSizes.Max())
                _log.LogWarning("--rows {Rows} is smaller than the largest chunk size {Max} — measurements will not differentiate.", rows, chunkSizes.Max());

            var (runtimes, plugins) = await BuildRuntimesAsync(ct);
            try
            {
                var runtime = domainName is null
                    ? runtimes[0]
                    : runtimes.FirstOrDefault(r => string.Equals(r.Name, domainName, StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException(
                          $"Domain '{domainName}' is not configured. Known domains: {string.Join(", ", runtimes.Select(r => r.Name))}.");

                EntityDescriptor descriptor;
                if (entityName is not null)
                {
                    if (!runtime.TryResolveEntity(entityName, out var resolved, out var error))
                        throw new InvalidOperationException(error ?? $"Entity '{entityName}' not found in domain '{runtime.Name}'.");
                    descriptor = resolved!;
                }
                else
                {
                    descriptor = runtime.Entities.FirstOrDefault(e =>
                                     e.KeyProperty is not null && !e.KeyIsDbGenerated &&
                                     e.KeyProperty.PropertyType == typeof(Guid))
                                 ?? throw new InvalidOperationException(
                                     $"No entity with a client-set Guid key found in domain '{runtime.Name}' — name one with --entity.");
                }

                _log.LogInformation("Measuring bulk insert of {Rows} {Entity} rows per chunk size against domain {Domain} " +
                                    "(strategy {Strategy}); rows are deleted after each measurement.",
                    rows, descriptor.LogicalName, runtime.Name, _settings.Run.BulkStrategy);

                var results = new List<(int Chunk, double Ms, double RowsPerSec, string? Route)>();
                for (var iteration = 0; iteration < chunkSizes.Length; iteration++)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = chunkSizes[iteration];
                    await runtime.BeginScenarioAsync(LifecycleMode.TrackedTeardown, seed: 7000 + iteration, ct);
                    try
                    {
                        var warnings = new List<string>();
                        var instances = new List<object>(rows);
                        for (var i = 0; i < rows; i++)
                        {
                            var instance = runtime.Generate(descriptor, out _, warnings);
                            // Unique keys per row/iteration — faker output may repeat.
                            if (descriptor.NaturalKeyProperty?.PropertyType == typeof(string))
                                descriptor.NaturalKeyProperty.SetValue(instance, $"bench-tune-{iteration}-{i}");
                            if (descriptor.KeyProperty?.PropertyType == typeof(Guid))
                                descriptor.SetKey(instance, Guid.NewGuid());
                            instances.Add(instance);
                        }

                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var outcome = await runtime.CreateBulkAsync(descriptor, instances,
                            new BulkPersistOptions(chunk, _settings.Run.BulkStrategy), ct);
                        sw.Stop();
                        if (!outcome.Success)
                            throw new InvalidOperationException($"Bulk insert failed at chunk size {chunk}: {outcome.Error}");

                        var rowsPerSec = rows / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                        results.Add((chunk, sw.Elapsed.TotalMilliseconds, rowsPerSec, outcome.Route));
                        _log.LogInformation("  chunk {Chunk,6}: {Ms,8:F0} ms   {Throughput,10:F0} rows/s   via {Route}",
                            chunk, sw.Elapsed.TotalMilliseconds, rowsPerSec, outcome.Route);
                    }
                    finally
                    {
                        var close = await runtime.EndScenarioAsync(CancellationToken.None);
                        if (close.Orphaned.Count > 0)
                            _log.LogWarning("Cleanup left {Count} orphaned row(s): {First}", close.Orphaned.Count, close.Orphaned[0]);
                    }
                }

                var best = results.MaxBy(r => r.RowsPerSec);
                _log.LogInformation("Best: chunk size {Chunk} at {Throughput:F0} rows/s (via {Route}).",
                    best.Chunk, best.RowsPerSec, best.Route);

                if (!write)
                {
                    _log.LogInformation("--no-write given — set run.bulkChunkSize to {Chunk} to apply.", best.Chunk);
                    return 0;
                }

                var text = File.ReadAllText(_settingsFilePath);
                var updated = System.Text.RegularExpressions.Regex.Replace(text,
                    "\"bulkChunkSize\"\\s*:\\s*\\d+", $"\"bulkChunkSize\": {best.Chunk}");
                if (!ReferenceEquals(text, updated) && text != updated)
                {
                    File.WriteAllText(_settingsFilePath, updated);
                    _log.LogInformation("run.bulkChunkSize set to {Chunk} in {File}.", best.Chunk, _settingsFilePath);
                }
                else if (text.Contains($"\"bulkChunkSize\": {best.Chunk}"))
                {
                    _log.LogInformation("run.bulkChunkSize is already {Chunk} — nothing to write.", best.Chunk);
                }
                else
                {
                    _log.LogWarning("No bulkChunkSize property found in {File} — add \"bulkChunkSize\": {Chunk} to the run section.",
                        _settingsFilePath, best.Chunk);
                }
                return 0;
            }
            finally
            {
                await DisposeRuntimesAsync(runtimes, plugins);
            }
        }

        /// <summary>`tdm export-model` (W4-D2): the model is built from the same resolved
        /// runtimes as `list-entities`, so editor tooling cannot drift from engine resolution.
        /// Model building is offline — no database connection is opened.</summary>
        public async Task<int> ExportModelAsync(string outPath, CancellationToken ct)
        {
            var (runtimes, plugins) = await BuildRuntimesAsync(ct);
            try
            {
                var settingsSha = Convert.ToHexStringLower(
                    System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(_settingsFilePath)));
                var model = Tdm.Core.Model.TdmModelBuilder.Build(runtimes, settingsSha);
                var fullPath = Path.GetFullPath(outPath, _baseDirectory);
                File.WriteAllText(fullPath, model.Serialize());
                _log.LogInformation("Model exported: {Path} ({Domains} domain(s), {Entities} entities)",
                    fullPath, model.Domains.Count, model.Domains.Sum(d => d.Entities.Count));
                return 0;
            }
            finally
            {
                await DisposeRuntimesAsync(runtimes, plugins);
            }
        }

        /// <summary>`tdm profile` (W4-D8 spike): read-only statistics over each database
        /// domain's entities. Api domains have no query surface and are skipped.</summary>
        public async Task<int> ProfileAsync(string? domainFilter, Tdm.EfCore.Profiling.ProfileOptions options,
            string outPath, string? fragmentPath, CancellationToken ct)
        {
            var (runtimes, plugins) = await BuildRuntimesAsync(ct);
            try
            {
                var combined = new Tdm.Core.Profiling.StatsPack
                {
                    GeneratedUtc = DateTime.UtcNow,
                    SampleRows = options.SampleRows,
                    ValuesSuppressed = !options.IncludeValues,
                };
                foreach (var domain in _settings.Domains)
                {
                    if (domainFilter is not null &&
                        !string.Equals(domain.Name, domainFilter, StringComparison.OrdinalIgnoreCase)) continue;
                    if (domain.Persistence == PersistenceMode.Api)
                    {
                        _log.LogInformation("Skipping domain {Domain}: persistence Api has no query surface to profile.", domain.Name);
                        continue;
                    }
                    var plugin = plugins.First(p => string.Equals(p.DomainName, domain.Name, StringComparison.OrdinalIgnoreCase));
                    var pack = Tdm.EfCore.Profiling.StatsProfiler.Profile(domain, _settings, plugin.Assemblies, options, _log);
                    foreach (var (entityName, stats) in pack.Entities)
                    {
                        if (!combined.Entities.TryAdd(entityName, stats))
                            _log.LogWarning("Entity name '{Entity}' profiled in multiple domains — keeping the first.", entityName);
                    }
                }

                var fullOut = Path.GetFullPath(outPath, _baseDirectory);
                File.WriteAllText(fullOut, combined.Serialize());
                _log.LogInformation("Statistics pack written: {Path} ({Entities} entities). Never contains row values; " +
                                    "category labels only below the categorical threshold.", fullOut, combined.Entities.Count);

                if (fragmentPath is not null)
                {
                    var fragment = combined.ToFragment();
                    var fullFragment = Path.GetFullPath(fragmentPath, _baseDirectory);
                    File.WriteAllText(fullFragment,
                        System.Text.Json.JsonSerializer.Serialize(fragment, TdmSettings.JsonOptions));
                    _log.LogInformation("Entities-config fragment written: {Path} — merge into tdm.settings.json " +
                                        "entities (and declare the stats pack under statsPacks for attribution).", fullFragment);
                }
                return 0;
            }
            finally
            {
                await DisposeRuntimesAsync(runtimes, plugins);
            }
        }

        public async Task<int> TeardownAsync(string manifestPath, CancellationToken ct)
        {
            var manifest = ManifestWriter.Read(Path.GetFullPath(manifestPath));
            var (runtimes, plugins) = await BuildRuntimesAsync(ct);
            try
            {
                var deleted = 0;
                var orphaned = new List<string>();

                foreach (var scenario in manifest.Scenarios)
                foreach (var bulk in scenario.BulkOperations.Where(b => b.HashedRows > 0))
                {
                    _log.LogWarning("[{Scenario}] bulk create of {Count} {Entity} row(s) was recorded in {Mode} mode — " +
                                    "only the {Sampled} sampled row(s) can be torn down from this manifest. " +
                                    "Use manifestBulkValues: All, or a delete-all step, when bulk rows must be manifest-removable.",
                        scenario.Scenario, bulk.Count, bulk.Entity, bulk.Mode, bulk.SampledRows);
                }

                // Reverse creation order across the whole manifest (children before principals).
                var created = manifest.Scenarios
                    .SelectMany(s => s.Entities)
                    .Where(e => e.Verb is "Create" or "Projection" && e.Id is not null)
                    .Reverse()
                    .ToList();

                foreach (var entity in created)
                {
                    ct.ThrowIfCancellationRequested();
                    var runtime = runtimes.FirstOrDefault(r =>
                        string.Equals(r.Name, entity.Domain, StringComparison.OrdinalIgnoreCase));
                    if (runtime is null)
                    {
                        orphaned.Add($"{entity.Domain}.{entity.Entity}:{entity.Id} — domain not configured");
                        continue;
                    }
                    try
                    {
                        if (await runtime.DeleteByIdAsync(entity.Entity, entity.Id!, ct)) deleted++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        orphaned.Add($"{entity.Domain}.{entity.Entity}:{entity.Id} — {ex.Message}");
                    }
                }

                _log.LogInformation("Teardown complete: {Deleted} deleted, {Orphaned} orphaned", deleted, orphaned.Count);
                foreach (var orphan in orphaned) _log.LogWarning("Orphaned: {Row}", orphan);
                return orphaned.Count == 0 ? 0 : 1;
            }
            finally
            {
                await DisposeRuntimesAsync(runtimes, plugins);
            }
        }

        public async Task<int> ListEntitiesAsync(string? domainFilter, CancellationToken ct)
        {
            var (runtimes, plugins) = await BuildRuntimesAsync(ct);
            try
            {
                foreach (var runtime in runtimes)
                {
                    if (domainFilter is not null &&
                        !string.Equals(runtime.Name, domainFilter, StringComparison.OrdinalIgnoreCase)) continue;

                    Console.WriteLine();
                    Console.WriteLine($"Domain: {runtime.Name} (persistence: {runtime.Settings.Persistence}, profile: {runtime.Settings.ConventionProfile})");
                    Console.WriteLine($"  {"entity",-18} {"clr type",-52} {"key",-28} {"natural key",-12} {"faker",-22} {"persist route",-40} {"read repo"}");
                    foreach (var info in runtime.DescribeEntities().OrderBy(i => i.LogicalName, StringComparer.Ordinal))
                    {
                        Console.WriteLine($"  {info.LogicalName,-18} {info.ClrType,-52} {info.KeyInfo,-28} {info.NaturalKey ?? "-",-12} {info.FakerSource,-22} {info.PersistRoute,-40} {info.ReadRepository ?? "-"}");
                    }
                    foreach (var violation in runtime.PolicyViolations)
                        Console.WriteLine($"  !! policy: {violation}");
                    foreach (var warning in runtime.Warnings)
                        Console.WriteLine($"  ! {warning}");
                }
                return 0;
            }
            finally
            {
                await DisposeRuntimesAsync(runtimes, plugins);
            }
        }

        /// <summary>`tdm explain` (W1-D6): reuses StepGrammar + the real runtimes' resolution
        /// verbatim — no parallel implementation, so the output is guaranteed truthful.
        /// Persists nothing; model building is offline, so no database connection is opened.</summary>
        public async Task<int> ExplainAsync(string keyword, string stepText, CancellationToken ct)
        {
            var step = Tdm.Core.Grammar.StepGrammar.Parse(keyword, stepText, dataTable: null, line: 1);
            Console.WriteLine($"Step        : {keyword} {stepText}");

            if (step is Tdm.Core.Grammar.UnmatchedStep)
            {
                Console.WriteLine("Grammar     : UNMATCHED — no TDM grammar rule fits this text.");
                Console.WriteLine("              Verbs: exists / exist (create), is updated with, is deleted / are deleted,");
                Console.WriteLine("              should exist (verify), an external <Entity> reference \"<key>\" from <Domain>.");
                return 1;
            }

            var (entityName, domainPin, naturalKeyValue) = Describe(step);

            var (runtimes, plugins) = await BuildRuntimesAsync(ct);
            try
            {
                var matches = runtimes
                    .Where(r => domainPin is null || string.Equals(r.Name, domainPin, StringComparison.OrdinalIgnoreCase))
                    .Select(r => (Runtime: r, Resolved: r.TryResolveEntity(entityName, out var d, out _) ? d : null))
                    .Where(m => m.Resolved is not null)
                    .ToList();

                switch (matches.Count)
                {
                    case 0:
                        Console.WriteLine($"Resolution  : entity '{entityName}' not found in " +
                                          (domainPin is null ? "any configured domain" : $"domain '{domainPin}'") +
                                          " — run `tdm list-entities` to see what resolved.");
                        break;
                    case > 1:
                        Console.WriteLine($"Resolution  : AMBIGUOUS — '{entityName}' exists in domains: " +
                                          string.Join(", ", matches.Select(m => m.Runtime.Name)) +
                                          ". Qualify the step with a domain (e.g. \"a Billing Customer ...\").");
                        break;
                    default:
                        {
                            var (runtime, descriptor) = matches[0];
                            var info = runtime.DescribeEntities().First(i => i.LogicalName == descriptor!.LogicalName);
                            Console.WriteLine($"Resolution  : {runtime.Name}.{info.LogicalName} → {info.ClrType}");
                            Console.WriteLine($"  natural key : {info.NaturalKey ?? "(none configured)"}");
                            Console.WriteLine($"  key         : {info.KeyInfo}");
                            Console.WriteLine($"  faker       : {info.FakerSource}");
                            Console.WriteLine($"  persist via : {info.PersistRoute}");
                            Console.WriteLine($"  read repo   : {info.ReadRepository ?? "-"}");

                            if (step is Tdm.Core.Grammar.ExternalReferenceStep)
                            {
                                // Identity belongs to the owning domain — printed in the External section below.
                            }
                            else if (naturalKeyValue is not null && descriptor!.IdStrategy == IdStrategy.Deterministic)
                            {
                                Console.WriteLine($"Identity    : {runtime.Name}|{info.LogicalName}|{naturalKeyValue}");
                                Console.WriteLine($"  uuid v5     : {Tdm.Identity.TdmIdentity.ForNaturalKey(runtime.Name, info.LogicalName, naturalKeyValue)}");
                            }
                            else if (naturalKeyValue is not null)
                            {
                                Console.WriteLine($"Identity    : {descriptor!.IdStrategy} — the database assigns the id; " +
                                                  $"'{naturalKeyValue}' is matched via the natural key column.");
                            }
                            else
                            {
                                Console.WriteLine("Identity    : no natural-key value in this step — derived at run time " +
                                                  "from the generated/overridden natural key.");
                            }
                            break;
                        }
                }

                if (step is Tdm.Core.Grammar.ExternalReferenceStep external)
                {
                    Console.WriteLine($"External    : owned by domain '{external.SourceDomain}' — identity contract id:");
                    Console.WriteLine($"  {external.SourceDomain}|{external.Entity}|{external.Key} → " +
                                      $"{Tdm.Identity.TdmIdentity.ForNaturalKey(external.SourceDomain, external.Entity, external.Key)}");
                }
                return 0;
            }
            finally
            {
                await DisposeRuntimesAsync(runtimes, plugins);
            }
        }

        /// <summary>Prints the grammar decision and extracts (entity, domain pin, natural-key value).</summary>
        private static (string Entity, string? DomainPin, string? NaturalKey) Describe(Tdm.Core.Grammar.StepPlan step)
        {
            switch (step)
            {
                case Tdm.Core.Grammar.CreateStep create:
                    Console.WriteLine($"Grammar     : Create — entity \"{create.Entity}\"" +
                                      (create.Rows is { Count: > 0 } rows ? $", {rows.Count} DataTable row(s)" : $", count {create.Count}") +
                                      (create.Domain is null ? "" : $", domain-pinned to '{create.Domain}'"));
                    Print("overrides ", create.Overrides.Select(o => $"{o.Name} = \"{o.RawValue}\""));
                    Print("references", create.References.Select(r => $"{r.Entity} \"{r.Key}\""));
                    return (create.Entity, create.Domain, null);

                case Tdm.Core.Grammar.UpdateStep update:
                    Console.WriteLine($"Grammar     : Update — entity \"{update.Entity}\", key \"{update.Key}\"");
                    Print("overrides ", update.Overrides.Select(o => $"{o.Name} = \"{o.RawValue}\""));
                    return (update.Entity, null, update.Key);

                case Tdm.Core.Grammar.DeleteStep delete:
                    Console.WriteLine($"Grammar     : Delete — entity \"{delete.Entity}\"" +
                                      (delete.All ? " (all matching)" : $", key \"{delete.Key}\""));
                    Print("filter    ", delete.Filter.Select(f => $"{f.Name} = \"{f.RawValue}\""));
                    return (delete.Entity, null, delete.Key);

                case Tdm.Core.Grammar.LoadStep load:
                    Console.WriteLine($"Grammar     : Load/verify — entity \"{load.Entity}\"" +
                                      (load.ExpectedCount is { } n ? $", expected count {n}" : $", key \"{load.Key}\""));
                    Print("expected  ", load.Expected.Select(e => $"{e.Name} = \"{e.RawValue}\""));
                    return (load.Entity, null, load.Key);

                case Tdm.Core.Grammar.ExternalReferenceStep external:
                    Console.WriteLine($"Grammar     : External reference — {external.Entity} \"{external.Key}\" owned by '{external.SourceDomain}'");
                    return (external.Entity, null, external.Key);

                default:
                    return ("", null, null);
            }

            static void Print(string label, IEnumerable<string> items)
            {
                var list = items.ToList();
                if (list.Count > 0) Console.WriteLine($"  {label}: {string.Join(", ", list)}");
            }
        }

        /// <summary>Connection strings named via connectionStringName resolve through the
        /// secret chain (W2-D8): inline (dev) → environment → registered cloud adapter.
        /// The chain-resolved value is stashed on the domain (never serialized); the built-in
        /// env-var fallback in ResolveConnectionString still applies when the chain misses.</summary>
        private async Task ResolveDomainSecretsAsync(CancellationToken ct)
        {
            foreach (var domain in _settings.Domains)
            {
                if (!string.IsNullOrWhiteSpace(domain.ConnectionString) ||
                    string.IsNullOrWhiteSpace(domain.ConnectionStringName) ||
                    domain.ResolvedConnectionString is not null) continue;

                domain.ResolvedConnectionString = await _secrets.GetFirstAsync(
                [
                    $"TDM_CONNECTIONSTRINGS__{domain.ConnectionStringName.ToUpperInvariant()}",
                    $"ConnectionStrings__{domain.ConnectionStringName}",
                    domain.ConnectionStringName,
                ], ct);
            }
        }

        private async Task<(List<IDomainRuntime> Runtimes, List<LoadedPlugin> Plugins)> BuildRuntimesAsync(CancellationToken ct)
        {
            // Seed packs (W4-D7) resolve before runtimes build: pack entity-config fragments
            // (naturalKey etc.) merge under local settings and must shape the bindings.
            if (_seedPacks is null)
            {
                var resolver = new SeedPackResolver(_settings.Plugins, _baseDirectory, _log) { UpdatePlugins = _updatePlugins };
                _seedPacks = await resolver.ResolveAsync(_settings.SeedPacks, ct);
                if (_seedPacks.Count > 0)
                {
                    Tdm.Core.SeedPacks.SeedPackApplier.MergeConfig(_settings, _seedPacks);
                    _log.LogInformation("Seed pack(s): {Packs}",
                        string.Join(", ", _seedPacks.Select(p => $"{p.Name}@{p.Version}")));
                }
            }

            await ResolveDomainSecretsAsync(ct);
            IPluginAcquirer acquirer = _settings.Plugins.Acquisition == PluginAcquisitionMode.NuGet
                ? new NuGetPluginAcquirer(_settings.Plugins, _baseDirectory, _log) { UpdatePlugins = _updatePlugins }
                : new FolderPluginAcquirer(_baseDirectory);
            var loader = new PluginLoader(acquirer, _log);
            var runtimes = new List<IDomainRuntime>();
            var plugins = new List<LoadedPlugin>();
            foreach (var domain in _settings.Domains)
            {
                var plugin = await loader.LoadAsync(domain, ct);
                plugins.Add(plugin);
                var domainLog = _loggerFactory.CreateLogger($"Tdm.Domain.{domain.Name}");
                if (domain.Persistence == PersistenceMode.Api)
                {
                    // W4-D6: same plugin-loaded CLR types, persistence via the domain's public
                    // API — the engine sees just another IDomainRuntime. Auth token via the
                    // W2-D8 secret chain.
                    var token = domain.Api?.Auth?.TokenSecret is { Length: > 0 } secretName
                        ? await _secrets.GetSecretAsync(secretName, ct)
                        : null;
                    runtimes.Add(Tdm.Api.ApiRuntimeBuilder.Build(domain, _settings, plugin.Assemblies, domainLog, token));
                    continue;
                }
                // Provider plugin packages (W3-D5) travel as dependencies of the domain package;
                // their bootstraps must be registered before the runtime activates any context.
                ProviderRegistry.DiscoverFrom(plugin.Assemblies, _log);
                runtimes.Add(DomainRuntimeBuilder.Build(domain, _settings, plugin.Assemblies, domainLog));
            }
            return (runtimes, plugins);
        }

        private static async Task DisposeRuntimesAsync(List<IDomainRuntime> runtimes, List<LoadedPlugin> plugins)
        {
            foreach (var runtime in runtimes) await runtime.DisposeAsync();
            foreach (var plugin in plugins) plugin.LoadContext.Unload();
        }
    }
}
