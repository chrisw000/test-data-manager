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

var root = new RootCommand("TDM — Gherkin-driven Test Data Manager");

var runCommand = new Command("run", "Parse feature files, seed data, write the run manifest.")
{
    settingsOption, seedOption, policyOption, lifecycleOption, benchmarkOption,
};
runCommand.SetAction(async (parseResult, ct) =>
{
    var composer = HostComposer.Create(parseResult.GetValue(settingsOption)!,
        parseResult.GetValue(seedOption), parseResult.GetValue(policyOption),
        parseResult.GetValue(lifecycleOption), parseResult.GetValue(benchmarkOption));
    return await composer.RunAsync(dryRun: false, ct);
});

var validateCommand = new Command("validate", "Parse features and resolve entities/fakers/repositories; persist nothing (CI dry run).")
{
    settingsOption, policyOption,
};
validateCommand.SetAction(async (parseResult, ct) =>
{
    var composer = HostComposer.Create(parseResult.GetValue(settingsOption)!,
        seed: null, parseResult.GetValue(policyOption), lifecycle: null, benchmark: null);
    return await composer.RunAsync(dryRun: true, ct);
});

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

root.Subcommands.Add(runCommand);
root.Subcommands.Add(validateCommand);
root.Subcommands.Add(teardownCommand);
root.Subcommands.Add(listEntitiesCommand);

return await root.Parse(args).InvokeAsync();

namespace Tdm.Host
{
    /// <summary>Composition root: settings → plugins → domain runtimes → engine → sinks.</summary>
    internal sealed class HostComposer
    {
        private readonly TdmSettings _settings;
        private readonly string _baseDirectory;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _log;

        private HostComposer(TdmSettings settings, string baseDirectory)
        {
            _settings = settings;
            _baseDirectory = baseDirectory;
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
            LifecycleMode? lifecycle, bool? benchmark)
        {
            var fullPath = Path.GetFullPath(settingsPath);
            var settings = TdmSettings.Load(fullPath);
            if (seed is { } s) settings.Run.DefaultSeed = s;
            if (policy is { } p) settings.Run.FailurePolicy = p;
            if (lifecycle is { } l) settings.Run.Lifecycle = l;
            if (benchmark is { } b) settings.Run.Benchmark = b;
            return new HostComposer(settings, Path.GetDirectoryName(fullPath)!);
        }

        public async Task<int> RunAsync(bool dryRun, CancellationToken ct)
        {
            using var otel = OtelBootstrap.Start(_settings.Run.Name);
            var (runtimes, plugins) = await BuildRuntimesAsync(ct);
            try
            {
                var plan = new GherkinPlanParser().ParsePaths(_settings.Run.FeaturePaths, _baseDirectory);
                var totalScenarios = plan.Features.Sum(f => f.Scenarios.Count);
                _log.LogInformation("{Mode} {Features} feature file(s), {Scenarios} scenario(s)",
                    dryRun ? "Validating" : "Running", plan.Features.Count, totalScenarios);

                var engine = new TdmEngine(_settings, runtimes, _loggerFactory.CreateLogger<TdmEngine>());
                var manifest = await engine.RunAsync(plan, dryRun, ct);

                var outputPath = Path.GetFullPath(_settings.Run.OutputPath, _baseDirectory);
                var manifestFile = ManifestWriter.Write(manifest, outputPath);
                _log.LogInformation("Manifest written: {Path}", manifestFile);

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
                    Console.WriteLine($"  {"entity",-18} {"clr type",-52} {"key",-28} {"natural key",-12} {"faker",-22} {"persist route"}");
                    foreach (var info in runtime.DescribeEntities().OrderBy(i => i.LogicalName, StringComparer.Ordinal))
                    {
                        Console.WriteLine($"  {info.LogicalName,-18} {info.ClrType,-52} {info.KeyInfo,-28} {info.NaturalKey ?? "-",-12} {info.FakerSource,-22} {info.PersistRoute}");
                    }
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

        private async Task<(List<IDomainRuntime> Runtimes, List<LoadedPlugin> Plugins)> BuildRuntimesAsync(CancellationToken ct)
        {
            var loader = new PluginLoader(new FolderPluginAcquirer(_baseDirectory), _log);
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
