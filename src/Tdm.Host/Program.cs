using System.CommandLine;
using Microsoft.Extensions.Logging;
using Tdm.Core.Execution;
using Tdm.Core.Grammar;
using Tdm.Core.Manifest;
using Tdm.Core.Settings;
using Tdm.EfCore;
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
    Description = "Additionally emit the manifest as <format>=<path>; formats: sarif, junit. Repeatable.",
    Arity = ArgumentArity.ZeroOrMore,
};

var root = new RootCommand("TDM — Gherkin-driven Test Data Manager");

var runCommand = new Command("run", "Parse feature files, seed data, write the run manifest.")
{
    settingsOption, seedOption, policyOption, lifecycleOption, benchmarkOption, updatePluginsOption, reportOption,
};
runCommand.SetAction(async (parseResult, ct) =>
{
    var reports = ParseReports(parseResult.GetValue(reportOption));
    var composer = HostComposer.Create(parseResult.GetValue(settingsOption)!,
        parseResult.GetValue(seedOption), parseResult.GetValue(policyOption),
        parseResult.GetValue(lifecycleOption), parseResult.GetValue(benchmarkOption),
        parseResult.GetValue(updatePluginsOption));
    return await composer.RunAsync(dryRun: false, reports, ct);
});

var validateCommand = new Command("validate", "Parse features and resolve entities/fakers/repositories; persist nothing (CI dry run).")
{
    settingsOption, policyOption, updatePluginsOption, reportOption,
};
validateCommand.SetAction(async (parseResult, ct) =>
{
    var reports = ParseReports(parseResult.GetValue(reportOption));
    var composer = HostComposer.Create(parseResult.GetValue(settingsOption)!,
        seed: null, parseResult.GetValue(policyOption), lifecycle: null, benchmark: null,
        parseResult.GetValue(updatePluginsOption));
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

root.Subcommands.Add(runCommand);
root.Subcommands.Add(validateCommand);
root.Subcommands.Add(teardownCommand);
root.Subcommands.Add(listEntitiesCommand);
root.Subcommands.Add(initCommand);
root.Subcommands.Add(explainCommand);
root.Subcommands.Add(manifestCommand);

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
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _log;

        private HostComposer(TdmSettings settings, string settingsFilePath, string baseDirectory, bool updatePlugins)
        {
            _settings = settings;
            _settingsFilePath = settingsFilePath;
            _baseDirectory = baseDirectory;
            _updatePlugins = updatePlugins;
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
            LifecycleMode? lifecycle, bool? benchmark, bool updatePlugins = false)
        {
            var fullPath = Path.GetFullPath(settingsPath);
            var settings = TdmSettings.Load(fullPath);
            if (seed is { } s) settings.Run.DefaultSeed = s;
            if (policy is { } p) settings.Run.FailurePolicy = p;
            if (lifecycle is { } l) settings.Run.Lifecycle = l;
            if (benchmark is { } b) settings.Run.Benchmark = b;
            return new HostComposer(settings, fullPath, Path.GetDirectoryName(fullPath)!, updatePlugins);
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

                var plan = new GherkinPlanParser().ParsePaths(_settings.Run.FeaturePaths, _baseDirectory);
                var totalScenarios = plan.Features.Sum(f => f.Scenarios.Count);
                _log.LogInformation("{Mode} {Features} feature file(s), {Scenarios} scenario(s)",
                    dryRun ? "Validating" : "Running", plan.Features.Count, totalScenarios);

                var engine = new TdmEngine(_settings, runtimes, _loggerFactory.CreateLogger<TdmEngine>());
                var manifest = await engine.RunAsync(plan, dryRun, ct);

                // Reproducibility down to the plugin version (W1-D2).
                foreach (var plugin in plugins)
                foreach (var (packageId, version) in plugin.Packages)
                    manifest.Run.PluginPackages[$"{plugin.DomainName}:{packageId}"] = version;

                // Attribution captured into the manifest itself, not a side file — one audit
                // artifact, so signing covers it all at once (W2-D1).
                manifest.Run.Attribution = Tdm.Observability.Audit.AttributionCollector.Collect(_settingsFilePath);

                var outputPath = Path.GetFullPath(_settings.Run.OutputPath, _baseDirectory);
                var manifestFile = ManifestWriter.Write(manifest, outputPath);
                _log.LogInformation("Manifest written: {Path}", manifestFile);

                var checksumPath = Tdm.Observability.Audit.ManifestSigner.WriteChecksum(manifestFile);
                _log.LogInformation("Checksum written: {Path}", checksumPath);
                if (_settings.Run.Signing is { } signing)
                {
                    var sigPath = Tdm.Observability.Audit.ManifestSigner.SignFromSettings(manifestFile, signing);
                    _log.LogInformation("Manifest signed: {Path}", sigPath);
                }

                foreach (var (format, path) in reports)
                {
                    var written = Tdm.Observability.Reports.ReportEmitter.Write(manifest, format, path, _baseDirectory);
                    _log.LogInformation("{Format} report written: {Path}", format, written);
                }

                RunSummary.Log(_log, manifest);
                return manifest.ExitCode;
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

        private async Task<(List<IDomainRuntime> Runtimes, List<LoadedPlugin> Plugins)> BuildRuntimesAsync(CancellationToken ct)
        {
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
                runtimes.Add(DomainRuntimeBuilder.Build(domain, _settings, plugin.Assemblies,
                    _loggerFactory.CreateLogger($"Tdm.Domain.{domain.Name}")));
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
