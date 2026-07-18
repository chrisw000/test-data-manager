using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tdm.Core.Benchmarks;
using Tdm.Core.Conversion;
using Tdm.Core.Diagnostics;
using Tdm.Core.Grammar;
using Tdm.Core.Journal;
using Tdm.Core.Manifest;
using Tdm.Core.Naming;
using Tdm.Core.Settings;
using Tdm.Identity;

namespace Tdm.Core.Execution;

/// <summary>
/// The execution engine (handoff §2): walks a <see cref="SeedingPlan"/>, resolves entities,
/// generates data, applies overrides and references, routes persistence through the domain
/// runtimes, and produces the run manifest. Set <c>dryRun</c> for `tdm validate` semantics —
/// parse + resolve everything, persist nothing. When a <paramref name="journal"/> is given,
/// every persisted outcome is eagerly journalled (W3-D6); a <paramref name="resume"/> state
/// skips work a previous run's journal proves was done.
/// </summary>
public sealed class TdmEngine(
    TdmSettings settings,
    IReadOnlyList<IDomainRuntime> domains,
    ILogger<TdmEngine>? logger = null,
    HttpClient? verifyClient = null,
    RunJournalWriter? journal = null,
    ResumeState? resume = null)
{
    private readonly ILogger _log = logger ?? NullLogger<TdmEngine>.Instance;
    private readonly HttpClient _verifyClient = verifyClient ?? new HttpClient();

    private sealed class ScenarioState
    {
        public required ScenarioPlan Plan { get; init; }
        public required ScenarioManifest Manifest { get; init; }
        /// <summary>Journal key "{feature}|{scenario}|{line}" — stable across identical plans (W3-D6).</summary>
        public required string Key { get; init; }
        public required int Seed { get; init; }
        public required bool DeepBenchmark { get; init; }
        public required bool DryRun { get; init; }
        /// <summary>The runtime sessions this scenario executes against — the shared root
        /// instances when serial, per-scenario sessions when parallel (W3-D2).</summary>
        public required IReadOnlyList<IDomainRuntime> Domains { get; init; }
        /// <summary>Non-null when resuming and this scenario is partially complete with a
        /// matching seed — ordinals it records persisted skip their persist call.</summary>
        public ResumeState? Resume { get; init; }
        public ScenarioContextBag Bag { get; } = new();
        public BenchmarkAggregator Bench { get; } = new();
        public int Ordinal;
        public bool Failed;

        public bool IsResumed(int ordinal) => Resume?.IsPersisted(Key, ordinal) == true;
    }

    private static string ScenarioKey(ScenarioManifest manifest) =>
        $"{manifest.Feature}|{manifest.Scenario}|{manifest.Line}";

    public Task<RunManifest> ValidateAsync(SeedingPlan plan, CancellationToken ct = default) =>
        RunAsync(plan, dryRun: true, ct);

    public async Task<RunManifest> RunAsync(SeedingPlan plan, bool dryRun = false, CancellationToken ct = default)
    {
        var runSw = Stopwatch.StartNew();
        var runBench = new BenchmarkAggregator();
        var manifest = new RunManifest
        {
            Run = new RunInfo
            {
                Name = settings.Run.Name,
                StartedUtc = DateTime.UtcNow,
                FailurePolicy = settings.Run.FailurePolicy,
                Lifecycle = settings.Run.Lifecycle,
                TdmVersion = InformationalVersion(typeof(TdmEngine).Assembly),
                BogusVersion = LoadedAssemblyVersion("Bogus"),
                EfVersion = LoadedAssemblyVersion("Microsoft.EntityFrameworkCore"),
                IdentityContractVersion = TdmIdentity.ContractVersion,
                DryRun = dryRun,
            },
        };

        using var runActivity = TdmDiagnostics.ActivitySource.StartActivity("run");
        runActivity?.SetTag("tdm.run", settings.Run.Name);
        runActivity?.SetTag("tdm.policy", settings.Run.FailurePolicy.ToString());
        journal?.RunStarted(settings.Run.Name);

        var parallelism = EffectiveParallelism(plan);
        var aborted = parallelism <= 1
            ? await RunSerialAsync(plan, manifest, runBench, dryRun, ct).ConfigureAwait(false)
            : await RunParallelAsync(plan, manifest, runBench, dryRun, parallelism, ct).ConfigureAwait(false);

        manifest.Run.DurationMs = Math.Round(runSw.Elapsed.TotalMilliseconds, 3);
        manifest.Run.Benchmark = runBench.ByOperation();
        foreach (var (key, stats) in runBench.ByOperationAndEntity())
            manifest.Run.Benchmark[key] = stats;

        manifest.Teardown.Deleted = manifest.Scenarios.Sum(s => s.Teardown?.Deleted ?? 0);
        manifest.Teardown.Orphaned = manifest.Scenarios.SelectMany(s => s.Teardown?.Orphaned ?? []).ToList();

        manifest.Run.Outcome =
            aborted || manifest.Scenarios.Any(s => s.Outcome == ScenarioOutcome.Failed) ? RunOutcome.Failed
            : manifest.Scenarios.Any(s => s.Outcome == ScenarioOutcome.CompletedWithWarnings) ||
              manifest.Teardown.Orphaned.Count > 0 ? RunOutcome.CompletedWithWarnings
            : RunOutcome.Succeeded;
        manifest.Run.Attestation = AttestationBuilder.Build(manifest);

        runActivity?.SetTag("tdm.outcome", manifest.Run.Outcome.ToString());
        _log.LogInformation("Run {Run} finished: {Outcome} in {Ms:F0} ms ({Scenarios} scenarios)",
            settings.Run.Name, manifest.Run.Outcome, manifest.Run.DurationMs, manifest.Scenarios.Count);
        return manifest;
    }

    /// <summary>Strict plan-order execution on the shared root runtimes — v1 behaviour, byte-for-byte.</summary>
    private async Task<bool> RunSerialAsync(SeedingPlan plan, RunManifest manifest,
        BenchmarkAggregator runBench, bool dryRun, CancellationToken ct)
    {
        var aborted = false;
        foreach (var feature in plan.Features)
        {
            using var featureActivity = TdmDiagnostics.ActivitySource.StartActivity("feature");
            featureActivity?.SetTag("tdm.feature", feature.Name);
            using var featureScope = _log.BeginScope("Feature {Feature}", feature.Name);

            foreach (var scenario in feature.Scenarios)
            {
                ct.ThrowIfCancellationRequested();
                var scenarioManifest = new ScenarioManifest
                {
                    Feature = feature.Name,
                    FeatureFile = feature.SourcePath,
                    Scenario = scenario.Name,
                    Line = scenario.Line,
                    Tags = scenario.Tags,
                };
                manifest.Scenarios.Add(scenarioManifest);

                try
                {
                    await ExecuteScenarioAsync(scenario, scenarioManifest, runBench, dryRun, domains, ct).ConfigureAwait(false);
                }
                catch (TdmRunAbortedException ex)
                {
                    scenarioManifest.Outcome = ScenarioOutcome.Failed;
                    scenarioManifest.Warnings.Add($"Run aborted (FailRun): {ex.Message}");
                    _log.LogError(ex, "Run aborted by failure policy in scenario {Scenario}", scenario.Name);
                    aborted = true;
                }
                if (aborted) break;
            }
            if (aborted) break;
        }
        return aborted;
    }

    /// <summary>
    /// Concurrent scenarios, each on its own set of runtime sessions (W3-D1/W3-D2). Manifests
    /// are pre-added in plan order, so completion order never changes the record; per-scenario
    /// seeds keep the generated data identical to a serial run.
    /// </summary>
    private async Task<bool> RunParallelAsync(SeedingPlan plan, RunManifest manifest,
        BenchmarkAggregator runBench, bool dryRun, int parallelism, CancellationToken ct)
    {
        var work = new List<(FeaturePlan Feature, ScenarioPlan Scenario, ScenarioManifest Manifest)>();
        foreach (var feature in plan.Features)
        {
            foreach (var scenario in feature.Scenarios)
            {
                var scenarioManifest = new ScenarioManifest
                {
                    Feature = feature.Name,
                    FeatureFile = feature.SourcePath,
                    Scenario = scenario.Name,
                    Line = scenario.Line,
                    Tags = scenario.Tags,
                };
                manifest.Scenarios.Add(scenarioManifest);
                work.Add((feature, scenario, scenarioManifest));
            }
        }

        var aborted = 0;
        await Parallel.ForEachAsync(work,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            async (item, token) =>
            {
                if (Volatile.Read(ref aborted) != 0)
                {
                    // FailRun abort: in-flight scenarios finish, not-yet-started ones are skipped.
                    item.Manifest.Outcome = ScenarioOutcome.Skipped;
                    item.Manifest.Warnings.Add("Not run: the run was aborted by a FailRun policy violation.");
                    return;
                }

                using var featureActivity = TdmDiagnostics.ActivitySource.StartActivity("feature");
                featureActivity?.SetTag("tdm.feature", item.Feature.Name);
                using var featureScope = _log.BeginScope("Feature {Feature}", item.Feature.Name);

                var sessions = new List<IDomainRuntime>(domains.Count);
                try
                {
                    foreach (var domain in domains)
                        sessions.Add(domain.CreateSession());
                    await ExecuteScenarioAsync(item.Scenario, item.Manifest, runBench, dryRun, sessions, token).ConfigureAwait(false);
                }
                catch (TdmRunAbortedException ex)
                {
                    item.Manifest.Outcome = ScenarioOutcome.Failed;
                    item.Manifest.Warnings.Add($"Run aborted (FailRun): {ex.Message}");
                    _log.LogError(ex, "Run aborted by failure policy in scenario {Scenario}", item.Scenario.Name);
                    Volatile.Write(ref aborted, 1);
                }
                finally
                {
                    foreach (var session in sessions)
                        await session.DisposeAsync().ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

        return aborted != 0;
    }

    /// <summary>
    /// run.maxParallelScenarios, capped by every participating domain's own limit. Transactional
    /// scenarios on SQLite auto-serialise with a warning — SQLite is single-writer, and parallel
    /// scenarios would hold competing write transactions for their whole lifetime.
    /// </summary>
    private int EffectiveParallelism(SeedingPlan plan)
    {
        var parallelism = Math.Max(1, settings.Run.MaxParallelScenarios);
        foreach (var domain in domains)
        {
            if (domain.Settings.MaxParallelScenarios is { } cap)
                parallelism = Math.Min(parallelism, Math.Max(1, cap));
        }

        if (parallelism > 1)
        {
            var transactional = settings.Run.Lifecycle == LifecycleMode.Transactional ||
                plan.Features.SelectMany(f => f.Scenarios).Any(s => s.LifecycleOverride == LifecycleMode.Transactional);
            var sqliteDomains = domains
                .Where(d => string.Equals(d.Settings.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
                .Select(d => d.Name).ToList();
            if (transactional && sqliteDomains.Count > 0)
            {
                _log.LogWarning(
                    "maxParallelScenarios {Parallelism} requested, but Transactional scenarios on SQLite ({Domains}) are single-writer — running serially.",
                    parallelism, string.Join(", ", sqliteDomains));
                parallelism = 1;
            }
        }
        return parallelism;
    }

    private async Task ExecuteScenarioAsync(ScenarioPlan scenario, ScenarioManifest scenarioManifest,
        BenchmarkAggregator runBench, bool dryRun, IReadOnlyList<IDomainRuntime> sessionDomains, CancellationToken ct)
    {
        var seed = scenario.Seed ?? settings.Run.DefaultSeed;
        var lifecycle = scenario.LifecycleOverride ?? settings.Run.Lifecycle;
        scenarioManifest.Seed = seed;
        scenarioManifest.Lifecycle = lifecycle;
        var scenarioKey = ScenarioKey(scenarioManifest);

        using var activity = TdmDiagnostics.ActivitySource.StartActivity("scenario");
        activity?.SetTag("tdm.scenario", scenario.Name);
        activity?.SetTag("tdm.seed", seed);
        using var scope = _log.BeginScope("Scenario {Scenario}", scenario.Name);

        if (scenario.Skip)
        {
            scenarioManifest.Outcome = ScenarioOutcome.Skipped;
            journal?.ScenarioCompleted(scenarioKey, ScenarioOutcome.Skipped);
            _log.LogInformation("Scenario {Scenario} skipped (@skip)", scenario.Name);
            return;
        }

        // Resume (W3-D6): a scenario the journal records complete is not re-run; the new
        // journal records it complete too, so a resumed run's journal is itself resumable.
        if (resume?.IsScenarioComplete(scenarioKey) == true)
        {
            scenarioManifest.Outcome = ScenarioOutcome.Skipped;
            scenarioManifest.Warnings.Add($"Skipped: recorded complete in the resume journal ({resume.JournalPath}).");
            journal?.ScenarioCompleted(scenarioKey, ScenarioOutcome.Skipped);
            _log.LogInformation("Scenario {Scenario} skipped — recorded complete in the resume journal", scenario.Name);
            return;
        }

        // A partial scenario resumes ordinal-by-ordinal only if the seeds match — ordinal
        // identities and generated values are seed-derived, so a mismatch means the journal
        // describes different rows. Re-running everything is safe (idempotent create-or-reuse).
        var scenarioResume = resume;
        if (resume?.RecordedSeed(scenarioKey) is { } recordedSeed && recordedSeed != seed)
        {
            scenarioManifest.Warnings.Add(
                $"Resume journal recorded seed {recordedSeed} but this run uses seed {seed} — re-running all steps.");
            scenarioResume = null;
        }

        var state = new ScenarioState
        {
            Plan = scenario,
            Manifest = scenarioManifest,
            Key = scenarioKey,
            Seed = seed,
            DeepBenchmark = settings.Run.Benchmark || scenario.ForceBenchmark,
            DryRun = dryRun,
            Domains = sessionDomains,
            Resume = scenarioResume,
        };
        journal?.ScenarioStarted(scenarioKey, seed);

        if (!dryRun)
        {
            foreach (var domain in sessionDomains)
                await domain.BeginScenarioAsync(lifecycle, seed, ct).ConfigureAwait(false);
        }

        try
        {
            foreach (var step in scenario.Steps)
            {
                ct.ThrowIfCancellationRequested();
                var stepSw = Stopwatch.StartNew();
                using var stepActivity = TdmDiagnostics.ActivitySource.StartActivity("step");
                stepActivity?.SetTag("tdm.step", step.Text);

                await ExecuteStepAsync(state, step, ct).ConfigureAwait(false);

                TdmDiagnostics.StepDuration.Record(stepSw.Elapsed.TotalMilliseconds,
                    new KeyValuePair<string, object?>("verb", step.GetType().Name));
            }
        }
        finally
        {
            if (!dryRun)
            {
                var teardown = new ScenarioTeardown();
                foreach (var domain in sessionDomains)
                {
                    var close = await domain.EndScenarioAsync(ct).ConfigureAwait(false);
                    teardown.Deleted += close.Deleted;
                    teardown.Orphaned.AddRange(close.Orphaned);
                    if (close.Error is not null)
                        scenarioManifest.Warnings.Add($"Scenario close ({domain.Name}): {close.Error}");
                }
                if (lifecycle == LifecycleMode.TrackedTeardown || teardown.Deleted > 0 || teardown.Orphaned.Count > 0)
                    scenarioManifest.Teardown = teardown;
            }

            scenarioManifest.Benchmark = state.Bench.ByOperation();
            state.Bench.MergeInto(runBench);
        }

        var hasWarnings = scenarioManifest.Warnings.Count > 0
                          || scenarioManifest.UnmatchedSteps.Count > 0
                          || scenarioManifest.Entities.Any(e => e.Warnings.Count > 0);
        scenarioManifest.Outcome = state.Failed ? ScenarioOutcome.Failed
            : hasWarnings ? ScenarioOutcome.CompletedWithWarnings
            : ScenarioOutcome.Succeeded;
        journal?.ScenarioCompleted(scenarioKey, scenarioManifest.Outcome);
    }

    private async Task ExecuteStepAsync(ScenarioState state, StepPlan step, CancellationToken ct)
    {
        switch (step)
        {
            case CreateStep create: await ExecuteCreateAsync(state, create, ct).ConfigureAwait(false); break;
            case UpdateStep update: await ExecuteUpdateAsync(state, update, ct).ConfigureAwait(false); break;
            case DeleteStep delete: await ExecuteDeleteAsync(state, delete, ct).ConfigureAwait(false); break;
            case LoadStep load: await ExecuteLoadAsync(state, load, ct).ConfigureAwait(false); break;
            case ExternalReferenceStep external: await ExecuteExternalReferenceAsync(state, external, ct).ConfigureAwait(false); break;
            case UnmatchedStep:
                state.Manifest.UnmatchedSteps.Add(new UnmatchedStepManifest { Text = step.Text, Line = step.Line });
                StepProblem(state, step, $"Step matches no TDM grammar rule: \"{step.Text}\"");
                break;
        }
    }

    // ---------------------------------------------------------------- Create

    private async Task ExecuteCreateAsync(ScenarioState state, CreateStep step, CancellationToken ct)
    {
        var resolved = ResolveEntity(state, step, step.Domain, step.Entity);
        if (resolved is null) return;
        var (runtime, descriptor) = resolved.Value;

        if (step.Rows is null && step.Count > 1)
        {
            // Count-bulk creates stream through a bounded pipeline (W3-D3/W3-D4).
            await ExecuteBulkCreateAsync(state, step, runtime, descriptor, ct).ConfigureAwait(false);
            return;
        }

        var rows = step.Rows ?? [step.Overrides];

        foreach (var overrides in rows)
        {
            ct.ThrowIfCancellationRequested();
            var ordinal = ++state.Ordinal;
            var entitySw = Stopwatch.StartNew();
            var entry = new EntityManifest
            {
                Ordinal = ordinal,
                Entity = descriptor.LogicalName,
                Verb = "Create",
                Domain = runtime.Name,
            };

            try
            {
                var instance = Generate(state, runtime, descriptor, entry);
                ApplyOverrides(state, descriptor, instance, overrides, entry);
                await ApplyReferencesAsync(state, runtime, descriptor, instance, step.References, step, entry, ct).ConfigureAwait(false);
                ApplyIdentity(state, runtime, descriptor, instance, ordinal, entry);

                // Resume (W3-D6): the journal proves this ordinal was persisted — generation
                // above still ran (seeded faker sequences must stay aligned), the write is skipped.
                if (state.IsResumed(ordinal))
                {
                    entry.PersistedVia = "resumed";
                    CompleteCreatedEntry(state, descriptor, instance, entry);
                    FinishEntry(state, entry, entitySw);
                    continue;
                }

                // Idempotent create: the same natural key means the same deterministic identity
                // (handoff §7), so a pre-existing row is reused rather than collided with —
                // re-running a Persistent environment seed must not explode. The step's explicit
                // overrides declare desired state, so they are re-applied to the existing row.
                if (!state.DryRun && descriptor.GetNaturalKey(instance) is { Length: > 0 } candidateKey)
                {
                    object? existing = null;
                    try { existing = await runtime.FindByNaturalKeyAsync(descriptor, candidateKey, ct).ConfigureAwait(false); }
                    catch (InvalidOperationException) { /* non-unique key — let persist surface it */ }
                    if (existing is not null)
                    {
                        entry.PersistedVia = "already-existed";
                        if (overrides.Count > 0)
                        {
                            entry.OverridesApplied.Clear();
                            ApplyOverrides(state, descriptor, existing, overrides, entry);
                            var updateOutcome = await TimedPersistAsync(state, descriptor, "update",
                                () => runtime.UpdateAsync(descriptor, existing, ct)).ConfigureAwait(false);
                            entry.PersistedVia = updateOutcome.Success
                                ? $"already-existed (updated via {updateOutcome.Route})"
                                : "already-existed";
                            if (!updateOutcome.Success)
                                entry.Warnings.Add($"reapplying declared values failed: {updateOutcome.Error}");
                        }
                        CompleteCreatedEntry(state, descriptor, existing, entry);
                        FinishEntry(state, entry, entitySw);
                        continue;
                    }
                }

                if (state.DryRun)
                {
                    entry.PersistedVia = "dry-run";
                }
                else
                {
                    var outcome = await TimedPersistAsync(state, descriptor, "create",
                        () => runtime.CreateAsync(descriptor, instance, ct: ct)).ConfigureAwait(false);
                    entry.PersistedVia = outcome.Route;
                    if (!outcome.Success)
                    {
                        // A parallel scenario may have won a same-natural-key race after our
                        // existence check — converge on the winner's row, as the sequential
                        // create-or-reuse path would have (W3-D1 safety).
                        var winner = await FindConcurrentWinnerAsync(runtime, descriptor, instance, ct).ConfigureAwait(false);
                        if (winner is null)
                        {
                            TdmDiagnostics.EntitiesFailed.Add(1);
                            entry.Warnings.Add(outcome.Error ?? "persist failed");
                            ObjectProblem(state, $"Persist failed for {descriptor.LogicalName}: {outcome.Error}");
                            FinishEntry(state, entry, entitySw);
                            continue;
                        }

                        entry.PersistedVia = "already-existed (concurrent create)";
                        if (overrides.Count > 0)
                        {
                            entry.OverridesApplied.Clear();
                            ApplyOverrides(state, descriptor, winner, overrides, entry);
                            var reapply = await TimedPersistAsync(state, descriptor, "update",
                                () => runtime.UpdateAsync(descriptor, winner, ct)).ConfigureAwait(false);
                            if (reapply.Success)
                                entry.PersistedVia = $"already-existed (concurrent create; updated via {reapply.Route})";
                            else
                                entry.Warnings.Add($"reapplying declared values failed: {reapply.Error}");
                        }
                        CompleteCreatedEntry(state, descriptor, winner, entry);
                        FinishEntry(state, entry, entitySw);
                        continue;
                    }
                    TdmDiagnostics.EntitiesCreated.Add(1,
                        new KeyValuePair<string, object?>("entity", descriptor.LogicalName));
                }

                CompleteCreatedEntry(state, descriptor, instance, entry);
            }
            catch (TdmObjectRejectedException ex)
            {
                entry.Warnings.Add($"Object rejected (FailObject): {ex.Message}");
                state.Manifest.Warnings.Add($"[line {step.Line}] object rejected: {ex.Message}");
                _log.LogWarning("Object rejected: {Message}", ex.Message);
            }
            catch (Exception ex) when (ex is not TdmRunAbortedException and not OperationCanceledException)
            {
                entry.Warnings.Add($"{ex.GetType().Name}: {ex.Message}");
                ObjectProblem(state, $"Failed to build {descriptor.LogicalName}: {ex.GetType().Name}: {ex.Message}");
            }
            FinishEntry(state, entry, entitySw);
        }
    }

    /// <summary>
    /// The count-bulk streaming pipeline (W3-D3/W3-D4): generate → override → reference →
    /// identity → persist, in bounded chunks, so memory is O(chunk) however large the count.
    /// The manifest records rows per <c>run.manifestBulkValues</c>: all of them, a head/tail
    /// sample plus a value hash of the rest, or count + hash only. Failed rows always keep
    /// their full entries. Bulk rows are not registered in the scenario context bag —
    /// references to them resolve from the database.
    /// </summary>
    private async Task ExecuteBulkCreateAsync(ScenarioState state, CreateStep step,
        IDomainRuntime runtime, EntityDescriptor descriptor, CancellationToken ct)
    {
        var mode = settings.Run.ManifestBulkValues;
        var sampleRows = mode == BulkManifestMode.Sample ? Math.Max(0, settings.Run.ManifestBulkSampleRows) : 0;
        var keepHead = mode == BulkManifestMode.All ? int.MaxValue : sampleRows;
        var keepTail = mode == BulkManifestMode.All ? 0 : sampleRows;
        var chunkSize = Math.Max(1, settings.Run.BulkChunkSize);
        var options = new BulkPersistOptions(chunkSize, settings.Run.BulkStrategy);

        var summary = new BulkOperationManifest
        {
            Entity = descriptor.LogicalName,
            Domain = runtime.Name,
            Verb = "Create",
            Requested = step.Count,
            Mode = mode,
            FirstOrdinal = state.Ordinal + 1,
        };
        state.Manifest.BulkOperations.Add(summary);

        var sw = Stopwatch.StartNew();
        using var restHash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var tail = new Queue<(object Instance, EntityManifest Entry)>();
        var headKept = 0;
        var chunk = new List<(object Instance, EntityManifest Entry, bool Resumed)>(Math.Min(chunkSize, step.Count));

        for (var i = 0; i < step.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var ordinal = ++state.Ordinal;
            var entry = new EntityManifest
            {
                Ordinal = ordinal,
                Entity = descriptor.LogicalName,
                Verb = "Create",
                Domain = runtime.Name,
            };

            try
            {
                var instance = Generate(state, runtime, descriptor, entry);
                ApplyOverrides(state, descriptor, instance, step.Overrides, entry);
                await ApplyReferencesAsync(state, runtime, descriptor, instance, step.References, step, entry, ct).ConfigureAwait(false);
                ApplyIdentity(state, runtime, descriptor, instance, ordinal, entry);
                // Resume (W3-D6): journalled-persisted rows ride the chunk for manifest
                // sampling but are excluded from the write — re-bulk-inserting them would
                // collide on their deterministic keys.
                chunk.Add((instance, entry, state.IsResumed(ordinal)));
            }
            catch (TdmObjectRejectedException ex)
            {
                entry.Warnings.Add($"Object rejected (FailObject): {ex.Message}");
                state.Manifest.Warnings.Add($"[line {step.Line}] object rejected: {ex.Message}");
                summary.Failed++;
                FinishEntry(state, entry, sw: null); // failed rows always keep their entry
            }

            if (chunk.Count >= chunkSize || i == step.Count - 1)
                await FlushChunkAsync().ConfigureAwait(false);
        }

        while (tail.Count > 0)
        {
            var (_, entry) = tail.Dequeue();
            summary.SampledRows++;
            FinishEntry(state, entry, sw: null, journalEntry: false); // journalled at chunk flush
        }

        summary.LastOrdinal = state.Ordinal;
        summary.ValuesSha256 = summary.HashedRows > 0
            ? Convert.ToHexString(restHash.GetHashAndReset()).ToLowerInvariant()
            : null;
        summary.DurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 3);

        async Task FlushChunkAsync()
        {
            if (chunk.Count == 0) return;

            if (state.DryRun)
            {
                foreach (var (instance, entry, _) in chunk)
                {
                    entry.PersistedVia = "dry-run";
                    CompleteCreatedEntry(state, descriptor, instance, entry, addToBag: false);
                }
                summary.Count += chunk.Count;
                summary.PersistedVia ??= "dry-run";
                RecordChunkEntries();
                chunk.Clear();
                return;
            }

            var toPersist = chunk.Where(c => !c.Resumed).Select(c => c.Instance).ToList();
            var outcome = PersistOutcome.Ok("resumed");
            if (toPersist.Count > 0)
            {
                var persistSw = Stopwatch.StartNew();
                outcome = await runtime.CreateBulkAsync(descriptor, toPersist, options, ct).ConfigureAwait(false);
                var ms = persistSw.Elapsed.TotalMilliseconds;
                state.Bench.Record("create", descriptor.LogicalName, ms);
                if (state.DeepBenchmark) state.Bench.Record("persist", descriptor.LogicalName, ms);
                TdmDiagnostics.PersistDuration.Record(ms,
                    new KeyValuePair<string, object?>("entity", descriptor.LogicalName),
                    new KeyValuePair<string, object?>("route", outcome.Route));
            }

            if (!outcome.Success)
            {
                TdmDiagnostics.EntitiesFailed.Add(toPersist.Count);
                summary.Failed += toPersist.Count;
                summary.Count += chunk.Count - toPersist.Count;
                summary.PersistedVia ??= outcome.Route;
                foreach (var (instance, entry, resumed) in chunk)
                {
                    entry.PersistedVia = resumed ? "resumed" : outcome.Route;
                    if (!resumed) entry.Warnings.Add(outcome.Error ?? "bulk persist failed");
                    CompleteCreatedEntry(state, descriptor, instance, entry, addToBag: false);
                    JournalEntity(state, entry);
                    FinishEntry(state, entry, sw: null, journalEntry: false); // failed rows always keep their entries
                }
                chunk.Clear();
                var problem = $"Bulk persist failed for {descriptor.LogicalName}: {outcome.Error}";
                if (outcome.Error is { } error &&
                    (error.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) ||
                     error.Contains("duplicate", StringComparison.OrdinalIgnoreCase)))
                {
                    problem += $" Hint: at {step.Count} rows, a faker natural-key collision is the usual cause — " +
                               "identical natural keys derive identical deterministic ids (the identity contract). " +
                               $"Give {descriptor.LogicalName}'s natural key a collision-free component (e.g. Bogus IndexFaker).";
                }
                // FailRun aborts here; BestEffort/FailObject continue with the next chunk.
                ObjectProblem(state, problem);
                return;
            }

            if (toPersist.Count > 0)
            {
                TdmDiagnostics.EntitiesCreated.Add(toPersist.Count,
                    new KeyValuePair<string, object?>("entity", descriptor.LogicalName));
            }
            summary.Count += chunk.Count;
            summary.PersistedVia ??= outcome.Route;
            foreach (var (instance, entry, resumed) in chunk)
            {
                entry.PersistedVia = resumed ? "resumed" : outcome.Route;
                CompleteCreatedEntry(state, descriptor, instance, entry, addToBag: false);
                JournalEntity(state, entry);
            }
            RecordChunkEntries();
            chunk.Clear();
        }

        void RecordChunkEntries()
        {
            foreach (var item in chunk)
            {
                if (headKept < keepHead)
                {
                    headKept++;
                    summary.SampledRows++;
                    FinishEntry(state, item.Entry, sw: null, journalEntry: false);
                    continue;
                }
                if (keepTail > 0)
                {
                    tail.Enqueue((item.Instance, item.Entry));
                    if (tail.Count > keepTail) HashAndDrop(tail.Dequeue().Entry);
                    continue;
                }
                HashAndDrop(item.Entry);
            }
        }

        void HashAndDrop(EntityManifest entry)
        {
            var canonical = new System.Text.StringBuilder()
                .Append(entry.Ordinal).Append('|')
                .Append(entry.Id).Append('|')
                .Append(entry.NaturalKey);
            foreach (var (name, value) in entry.Values.OrderBy(v => v.Key, StringComparer.Ordinal))
                canonical.Append('\u001f').Append(name).Append('=').Append(value);
            restHash.AppendData(System.Text.Encoding.UTF8.GetBytes(canonical.ToString()));
            summary.HashedRows++;
        }
    }

    /// <summary>
    /// After a failed create: the row a concurrent scenario persisted under this instance's
    /// natural key, if one now exists. Non-null only when a same-key race was genuinely lost —
    /// the pre-persist existence check already returned nothing.
    /// </summary>
    private static async Task<object?> FindConcurrentWinnerAsync(IDomainRuntime runtime,
        EntityDescriptor descriptor, object instance, CancellationToken ct)
    {
        if (descriptor.GetNaturalKey(instance) is not { Length: > 0 } naturalKey) return null;
        try
        {
            return await runtime.FindByNaturalKeyAsync(descriptor, naturalKey, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return null; // non-unique or unqueryable key — keep the original failure
        }
    }

    private void CompleteCreatedEntry(ScenarioState state, EntityDescriptor descriptor, object instance,
        EntityManifest entry, bool addToBag = true)
    {
        var id = descriptor.GetKey(instance);
        entry.Id = Convert.ToString(id, System.Globalization.CultureInfo.InvariantCulture);
        entry.IdStrategy = descriptor.KeyIsDbGenerated ? "DbGenerated"
            : descriptor.HasClientSettableGuidKey && descriptor.IdStrategy != IdStrategy.DbGenerated ? "Deterministic"
            : "ClientSet";
        entry.Values = descriptor.SnapshotValues(instance);

        var naturalKey = descriptor.GetNaturalKey(instance);
        entry.NaturalKey = naturalKey;
        // Bulk rows stay out of the bag — holding a million instances would defeat the
        // O(chunk) pipeline; references to them resolve from the database instead.
        if (addToBag && !string.IsNullOrEmpty(naturalKey))
            state.Bag.AddCreated(descriptor, naturalKey, instance, id);
    }

    // ---------------------------------------------------------------- Update

    private async Task ExecuteUpdateAsync(ScenarioState state, UpdateStep step, CancellationToken ct)
    {
        var resolved = ResolveEntity(state, step, domain: null, step.Entity);
        if (resolved is null) return;
        var (runtime, descriptor) = resolved.Value;

        var entitySw = Stopwatch.StartNew();
        var entry = new EntityManifest
        {
            Ordinal = ++state.Ordinal,
            Entity = descriptor.LogicalName,
            Verb = "Update",
            Domain = runtime.Name,
            NaturalKey = step.Key,
        };

        try
        {
            var instance = await FindTargetAsync(state, runtime, descriptor, step.Key, step, ct).ConfigureAwait(false);
            if (instance is null) { FinishEntry(state, entry, entitySw); return; }

            ApplyOverrides(state, descriptor, instance, step.Overrides, entry);
            await ApplyReferencesAsync(state, runtime, descriptor, instance, step.References, step, entry, ct).ConfigureAwait(false);

            if (state.IsResumed(entry.Ordinal))
            {
                entry.PersistedVia = "resumed"; // journal-proven applied by the interrupted run (W3-D6)
            }
            else if (!state.DryRun)
            {
                var outcome = await TimedPersistAsync(state, descriptor, "update",
                    () => runtime.UpdateAsync(descriptor, instance, ct)).ConfigureAwait(false);
                entry.PersistedVia = outcome.Route;
                if (!outcome.Success)
                {
                    TdmDiagnostics.EntitiesFailed.Add(1);
                    entry.Warnings.Add(outcome.Error ?? "update failed");
                    ObjectProblem(state, $"Update failed for {descriptor.LogicalName} \"{step.Key}\": {outcome.Error}");
                }
                else
                {
                    TdmDiagnostics.EntitiesUpdated.Add(1,
                        new KeyValuePair<string, object?>("entity", descriptor.LogicalName));
                }
            }
            entry.Id = Convert.ToString(descriptor.GetKey(instance), System.Globalization.CultureInfo.InvariantCulture);
            entry.Values = descriptor.SnapshotValues(instance);
        }
        catch (TdmObjectRejectedException ex)
        {
            entry.Warnings.Add($"Object rejected (FailObject): {ex.Message}");
            state.Manifest.Warnings.Add($"[line {step.Line}] object rejected: {ex.Message}");
        }
        FinishEntry(state, entry, entitySw);
    }

    // ---------------------------------------------------------------- Delete

    private async Task ExecuteDeleteAsync(ScenarioState state, DeleteStep step, CancellationToken ct)
    {
        var resolved = ResolveEntity(state, step, domain: null, step.Entity);
        if (resolved is null) return;
        var (runtime, descriptor) = resolved.Value;

        var entitySw = Stopwatch.StartNew();
        var entry = new EntityManifest
        {
            Ordinal = ++state.Ordinal,
            Entity = descriptor.LogicalName,
            Verb = "Delete",
            Domain = runtime.Name,
            NaturalKey = step.Key,
        };

        if (state.DryRun)
        {
            BuildFilters(state, descriptor, step.Filter, step);
            entry.PersistedVia = "dry-run";
            FinishEntry(state, entry, entitySw);
            return;
        }

        if (state.IsResumed(entry.Ordinal))
        {
            entry.PersistedVia = "resumed"; // journal-proven deleted by the interrupted run (W3-D6)
            FinishEntry(state, entry, entitySw);
            return;
        }

        if (step.All)
        {
            var filters = BuildFilters(state, descriptor, step.Filter, step);
            if (filters is null) { FinishEntry(state, entry, entitySw); return; }
            var sw = Stopwatch.StartNew();
            var count = await runtime.DeleteWhereAsync(descriptor, filters, ct).ConfigureAwait(false);
            state.Bench.Record("delete", descriptor.LogicalName, sw.Elapsed.TotalMilliseconds);
            // Journalled as persisted (W3-D6): a resumed run must NOT re-run a delete-all —
            // it would wipe rows whose re-creation the same resume then skips.
            entry.PersistedVia = "DbContext(where)";
            entry.Values["deletedCount"] = count.ToString();
            TdmDiagnostics.EntitiesDeleted.Add(count,
                new KeyValuePair<string, object?>("entity", descriptor.LogicalName));
        }
        else
        {
            var instance = await FindTargetAsync(state, runtime, descriptor, step.Key!, step, ct).ConfigureAwait(false);
            if (instance is null) { FinishEntry(state, entry, entitySw); return; }
            entry.Id = Convert.ToString(descriptor.GetKey(instance), System.Globalization.CultureInfo.InvariantCulture);

            var outcome = await TimedPersistAsync(state, descriptor, "delete",
                () => runtime.DeleteAsync(descriptor, instance, ct)).ConfigureAwait(false);
            entry.PersistedVia = outcome.Route;
            if (!outcome.Success)
            {
                TdmDiagnostics.EntitiesFailed.Add(1);
                entry.Warnings.Add(outcome.Error ?? "delete failed");
                ObjectProblem(state, $"Delete failed for {descriptor.LogicalName} \"{step.Key}\": {outcome.Error}");
            }
            else
            {
                TdmDiagnostics.EntitiesDeleted.Add(1,
                    new KeyValuePair<string, object?>("entity", descriptor.LogicalName));
            }
        }
        FinishEntry(state, entry, entitySw);
    }

    // ---------------------------------------------------------------- Load (verify)

    private async Task ExecuteLoadAsync(ScenarioState state, LoadStep step, CancellationToken ct)
    {
        var resolved = ResolveEntity(state, step, domain: null, step.Entity);
        if (resolved is null) return;
        var (runtime, descriptor) = resolved.Value;

        if (state.DryRun)
        {
            BuildFilters(state, descriptor, step.Expected, step);
            return;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            if (step.Key is not null)
            {
                var instance = await runtime.FindByNaturalKeyAsync(descriptor, step.Key, ct).ConfigureAwait(false);
                if (instance is null)
                {
                    StepProblem(state, step, $"Load assertion failed: {descriptor.LogicalName} \"{step.Key}\" does not exist.");
                    return;
                }
                foreach (var expectation in step.Expected)
                {
                    var prop = PropertyMatcher.Find(descriptor.ClrType, expectation.Name);
                    if (prop is null)
                    {
                        StepProblem(state, step, $"Load assertion: property '{expectation.Name}' not found on {descriptor.ClrType.Name}.");
                        continue;
                    }
                    if (!Conversion.ValueConverter.TryConvert(expectation.RawValue, prop.PropertyType, out var expected, out var error))
                    {
                        StepProblem(state, step, $"Load assertion: {error}");
                        continue;
                    }
                    var actual = prop.GetValue(instance);
                    if (!Equals(actual, expected))
                    {
                        StepProblem(state, step,
                            $"Load assertion failed: {descriptor.LogicalName} \"{step.Key}\" {prop.Name} is \"{actual}\", expected \"{expected}\".");
                    }
                }
            }
            else if (step.ExpectedCount is { } expectedCount)
            {
                var filters = BuildFilters(state, descriptor, step.Expected, step);
                if (filters is null) return;
                var actualCount = await runtime.CountAsync(descriptor, filters, ct).ConfigureAwait(false);
                if (actualCount != expectedCount)
                {
                    StepProblem(state, step,
                        $"Load assertion failed: expected {expectedCount} {descriptor.LogicalName} row(s), found {actualCount}.");
                }
            }
        }
        finally
        {
            state.Bench.Record("load", descriptor.LogicalName, sw.Elapsed.TotalMilliseconds);
        }
    }

    // ---------------------------------------------------------------- External references (§8.5)

    private async Task ExecuteExternalReferenceAsync(ScenarioState state, ExternalReferenceStep step, CancellationToken ct)
    {
        var logicalName = NameMatcher.Singularize(step.Entity);
        var owningDomain = step.SourceDomain;
        var id = TdmIdentity.ForNaturalKey(owningDomain, logicalName, step.Key);
        var owningSettings = settings.FindDomain(owningDomain);
        var mode = owningSettings?.ExternalReferences ?? ExternalReferenceMode.Synthesize;
        var entityConfig = settings.EntityFor(logicalName);

        state.Bag.AddExternal(logicalName, step.Key, id, owningDomain);

        var reference = new ReferenceManifest
        {
            Step = step.Line,
            Target = $"{logicalName}:{step.Key}",
            ResolvedFrom = "identityContract",
            Id = id.ToString(),
            OwningDomain = owningDomain,
            Mode = mode,
            Behavior = entityConfig.ExternalBehavior,
        };
        state.Manifest.References.Add(reference);

        if (mode == ExternalReferenceMode.Verify && !state.DryRun)
        {
            if (string.IsNullOrWhiteSpace(owningSettings?.VerifyEndpoint))
            {
                reference.VerifyOutcome = "NoEndpoint";
                StepProblem(state, step,
                    $"External reference mode is Verify but domain '{owningDomain}' has no verifyEndpoint configured.");
            }
            else
            {
                var url = owningSettings.VerifyEndpoint
                    .Replace("{entity}", Uri.EscapeDataString(logicalName), StringComparison.OrdinalIgnoreCase)
                    .Replace("{id}", id.ToString(), StringComparison.OrdinalIgnoreCase)
                    .Replace("{key}", Uri.EscapeDataString(step.Key), StringComparison.OrdinalIgnoreCase);
                try
                {
                    var response = await _verifyClient.GetAsync(url, ct).ConfigureAwait(false);
                    reference.VerifyOutcome = response.IsSuccessStatusCode ? "Verified" : $"Failed({(int)response.StatusCode})";
                    if (!response.IsSuccessStatusCode)
                        StepProblem(state, step, $"External reference verification failed: GET {url} → {(int)response.StatusCode}.");
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    reference.VerifyOutcome = $"Error({ex.GetType().Name})";
                    StepProblem(state, step, $"External reference verification error: {ex.Message}");
                }
            }
        }

        // Projection behaviour: seed the local read-model row messaging would normally produce,
        // using the owning domain's derived PK. DbContext-only by design — projections are
        // infrastructure, not domain behaviour (handoff §16).
        if (entityConfig.ExternalBehavior == ExternalBehavior.Projection &&
            !string.IsNullOrWhiteSpace(entityConfig.ProjectionEntity))
        {
            var resolved = ResolveEntity(state, step, domain: null, entityConfig.ProjectionEntity);
            if (resolved is null) return;
            var (runtime, projection) = resolved.Value;

            var entitySw = Stopwatch.StartNew();
            var entry = new EntityManifest
            {
                Ordinal = ++state.Ordinal,
                Entity = projection.LogicalName,
                Verb = "Projection",
                Domain = runtime.Name,
                NaturalKey = step.Key,
            };
            try
            {
                if (!state.DryRun)
                {
                    object? existingProjection = null;
                    try { existingProjection = await runtime.FindByNaturalKeyAsync(projection, step.Key, ct).ConfigureAwait(false); }
                    catch (InvalidOperationException) { /* non-unique — let persist surface it */ }
                    if (existingProjection is not null)
                    {
                        entry.PersistedVia = "already-existed";
                        CompleteCreatedEntry(state, projection, existingProjection, entry);
                        FinishEntry(state, entry, entitySw);
                        return;
                    }
                }

                var instance = Generate(state, runtime, projection, entry);
                if (projection.NaturalKeyProperty is not null &&
                    Conversion.ValueConverter.TryConvert(step.Key, projection.NaturalKeyProperty.PropertyType, out var nk, out _))
                {
                    projection.NaturalKeyProperty.SetValue(instance, nk);
                }
                if (projection.KeyProperty is not null && projection.HasClientSettableGuidKey)
                    projection.SetKey(instance, id);

                if (state.DryRun)
                {
                    entry.PersistedVia = "dry-run";
                }
                else
                {
                    var outcome = await TimedPersistAsync(state, projection, "create",
                        () => runtime.CreateAsync(projection, instance, forceDbContext: true, ct)).ConfigureAwait(false);
                    entry.PersistedVia = outcome.Route;
                    if (!outcome.Success)
                    {
                        // Two parallel scenarios can project the same external row (W3-D1 safety).
                        var winner = await FindConcurrentWinnerAsync(runtime, projection, instance, ct).ConfigureAwait(false);
                        if (winner is not null)
                        {
                            entry.PersistedVia = "already-existed (concurrent create)";
                            CompleteCreatedEntry(state, projection, winner, entry);
                            FinishEntry(state, entry, entitySw);
                            return;
                        }
                        entry.Warnings.Add(outcome.Error ?? "projection persist failed");
                        ObjectProblem(state, $"Projection persist failed for {projection.LogicalName}: {outcome.Error}");
                    }
                    else
                    {
                        TdmDiagnostics.EntitiesCreated.Add(1,
                            new KeyValuePair<string, object?>("entity", projection.LogicalName));
                    }
                }
                CompleteCreatedEntry(state, projection, instance, entry);
            }
            catch (TdmObjectRejectedException ex)
            {
                entry.Warnings.Add($"Object rejected (FailObject): {ex.Message}");
                state.Manifest.Warnings.Add($"[line {step.Line}] object rejected: {ex.Message}");
            }
            FinishEntry(state, entry, entitySw);
        }
    }

    // ---------------------------------------------------------------- Shared plumbing

    private (IDomainRuntime Runtime, EntityDescriptor Descriptor)? ResolveEntity(
        ScenarioState state, StepPlan step, string? domain, string entityName)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var pinned = domain ?? state.Plan.DomainPin;
            if (pinned is not null)
            {
                var runtime = state.Domains.FirstOrDefault(d =>
                    string.Equals(d.Name, pinned, StringComparison.OrdinalIgnoreCase));
                if (runtime is null)
                {
                    StepProblem(state, step, $"Domain '{pinned}' is not configured. Known domains: {string.Join(", ", state.Domains.Select(d => d.Name))}.");
                    return null;
                }
                if (!runtime.TryResolveEntity(entityName, out var descriptor, out var error))
                {
                    StepProblem(state, step, error ?? $"Entity '{entityName}' not found in domain '{pinned}'.");
                    return null;
                }
                return (runtime, descriptor!);
            }

            var matches = new List<(IDomainRuntime, EntityDescriptor)>();
            var errors = new List<string>();
            foreach (var runtime in state.Domains)
            {
                if (runtime.TryResolveEntity(entityName, out var descriptor, out var error))
                    matches.Add((runtime, descriptor!));
                else if (error is not null)
                    errors.Add(error);
            }

            switch (matches.Count)
            {
                case 1:
                    return matches[0];
                case 0:
                    StepProblem(state, step, errors.Count > 0
                        ? string.Join(" ", errors)
                        : $"Entity '{entityName}' not found in any configured domain ({string.Join(", ", state.Domains.Select(d => d.Name))}).");
                    return null;
                default:
                    StepProblem(state, step,
                        $"Entity '{entityName}' is ambiguous across domains: " +
                        string.Join(", ", matches.Select(m => $"{m.Item1.Name}.{m.Item2.ClrType.Name}")) +
                        ". Qualify the step (e.g. \"a Billing {Entity} ...\") or use @domain:.");
                    return null;
            }
        }
        finally
        {
            if (state.DeepBenchmark)
                state.Bench.Record("resolve", entityName, sw.Elapsed.TotalMilliseconds);
        }
    }

    private object Generate(ScenarioState state, IDomainRuntime runtime, EntityDescriptor descriptor, EntityManifest entry)
    {
        var sw = Stopwatch.StartNew();
        var warnings = new List<string>();
        var instance = runtime.Generate(descriptor, out var fakerSource, warnings);
        entry.Warnings.AddRange(warnings);
        if (state.DeepBenchmark)
            state.Bench.Record("generate", descriptor.LogicalName, sw.Elapsed.TotalMilliseconds);
        entry.FakerSource = fakerSource;
        return instance;
    }

    private void ApplyOverrides(ScenarioState state, EntityDescriptor descriptor, object instance,
        PropertyBag overrides, EntityManifest entry)
    {
        var sw = Stopwatch.StartNew();
        foreach (var (name, raw) in overrides.Select(a => (a.Name, a.RawValue)))
        {
            var prop = PropertyMatcher.Find(descriptor.ClrType, name);
            if (prop is null)
            {
                PropertyProblem(state, entry,
                    $"Property '{name}' not found (or read-only) on {descriptor.ClrType.Name}; raw value \"{raw}\".");
                continue;
            }
            if (!Conversion.ValueConverter.TryConvert(raw, prop.PropertyType, out var value, out var error))
            {
                PropertyProblem(state, entry, $"Property '{prop.Name}': {error}");
                continue;
            }
            prop.SetValue(instance, value);
            entry.OverridesApplied.Add(prop.Name);
        }
        if (state.DeepBenchmark && overrides.Count > 0)
            state.Bench.Record("override", descriptor.LogicalName, sw.Elapsed.TotalMilliseconds);
    }

    private async Task ApplyReferencesAsync(ScenarioState state, IDomainRuntime runtime,
        EntityDescriptor descriptor, object instance, List<ReferenceClause> references,
        StepPlan step, EntityManifest sourceEntry, CancellationToken ct)
    {
        foreach (var clause in references)
        {
            var sw = Stopwatch.StartNew();
            var reference = new ReferenceManifest
            {
                Step = step.Line,
                SourceOrdinal = sourceEntry.Ordinal,
                Target = $"{NameMatcher.Singularize(clause.Entity)}:{clause.Key}",
            };

            object? referencedInstance = null;
            object? referencedId = null;
            EntityDescriptor? referencedDescriptor = null;

            if (state.Bag.TryGet(clause.Entity, clause.Key, out var entry))
            {
                reference.ResolvedFrom = "contextBag";
                referencedInstance = entry.DomainName == runtime.Name ? entry.Instance : null;
                referencedId = entry.Id;
                if (entry.Instance is not null)
                    runtime.TryResolveEntity(clause.Entity, out referencedDescriptor, out _);
            }
            else if (state.DryRun)
            {
                reference.ResolvedFrom = "dry-run-skipped";
                state.Manifest.References.Add(reference);
                continue;
            }
            else
            {
                // DB lookup — supports referencing well-known base seed data. Environmental
                // non-determinism is accepted and made visible via resolvedFrom (handoff §8).
                if (runtime.TryResolveEntity(clause.Entity, out referencedDescriptor, out var resolveError))
                {
                    object? found;
                    try
                    {
                        found = await runtime.FindByNaturalKeyAsync(referencedDescriptor!, clause.Key, ct).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException ex)
                    {
                        StepProblem(state, step, $"Reference {clause.Entity} \"{clause.Key}\": {ex.Message}");
                        continue;
                    }
                    if (found is not null)
                    {
                        referencedInstance = found;
                        referencedId = referencedDescriptor!.GetKey(found);
                        reference.ResolvedFrom = "database";
                    }
                }
                else if (resolveError is not null && resolveError.Contains("ambiguous", StringComparison.OrdinalIgnoreCase))
                {
                    StepProblem(state, step, resolveError);
                    continue;
                }

                if (referencedId is null && referencedInstance is null)
                {
                    StepProblem(state, step,
                        $"Reference {clause.Entity} \"{clause.Key}\" not found in the scenario context bag or the {runtime.Name} database.");
                    continue;
                }
            }

            reference.Id = Convert.ToString(referencedId, System.Globalization.CultureInfo.InvariantCulture);
            state.Manifest.References.Add(reference);
            if (state.DeepBenchmark)
                state.Bench.Record("resolve", clause.Entity, sw.Elapsed.TotalMilliseconds);

            if (!runtime.TrySetReference(instance, descriptor, NameMatcher.Singularize(clause.Entity),
                    referencedDescriptor, referencedInstance, referencedId, out var setError))
            {
                StepProblem(state, step,
                    $"Cannot wire reference {clause.Entity} \"{clause.Key}\" onto {descriptor.ClrType.Name}: {setError}");
            }
        }
    }

    private void ApplyIdentity(ScenarioState state, IDomainRuntime runtime, EntityDescriptor descriptor,
        object instance, int ordinal, EntityManifest entry)
    {
        if (!descriptor.HasClientSettableGuidKey || descriptor.IdStrategy == IdStrategy.DbGenerated)
            return;
        // An explicit key override wins over the identity contract.
        if (descriptor.KeyProperty is not null && entry.OverridesApplied.Contains(descriptor.KeyProperty.Name))
            return;

        var naturalKey = descriptor.GetNaturalKey(instance);
        var id = !string.IsNullOrEmpty(naturalKey)
            ? TdmIdentity.ForNaturalKey(runtime.Name, descriptor.LogicalName, naturalKey)
            : TdmIdentity.ForOrdinal(runtime.Name, descriptor.LogicalName, state.Plan.Name, state.Seed, ordinal);
        descriptor.SetKey(instance, id);
    }

    private async Task<object?> FindTargetAsync(ScenarioState state, IDomainRuntime runtime,
        EntityDescriptor descriptor, string key, StepPlan step, CancellationToken ct)
    {
        if (state.DryRun)
        {
            if (state.Bag.TryGet(descriptor.LogicalName, key, out var bagEntry) && bagEntry.Instance is not null)
                return bagEntry.Instance;
            return null;
        }

        object? instance;
        try
        {
            instance = await runtime.FindByNaturalKeyAsync(descriptor, key, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            StepProblem(state, step, $"{descriptor.LogicalName} \"{key}\": {ex.Message}");
            return null;
        }
        if (instance is null)
            StepProblem(state, step, $"{descriptor.LogicalName} \"{key}\" not found.");
        return instance;
    }

    private IReadOnlyList<PropertyFilter>? BuildFilters(ScenarioState state, EntityDescriptor descriptor,
        PropertyBag bag, StepPlan step)
    {
        var filters = new List<PropertyFilter>();
        foreach (var (name, raw) in bag.Select(a => (a.Name, a.RawValue)))
        {
            var prop = PropertyMatcher.Find(descriptor.ClrType, name);
            if (prop is null)
            {
                StepProblem(state, step, $"Filter property '{name}' not found on {descriptor.ClrType.Name}.");
                return null;
            }
            if (!Conversion.ValueConverter.TryConvert(raw, prop.PropertyType, out var value, out var error))
            {
                StepProblem(state, step, $"Filter property '{prop.Name}': {error}");
                return null;
            }
            filters.Add(new PropertyFilter(prop, value));
        }
        return filters;
    }

    private async Task<PersistOutcome> TimedPersistAsync(ScenarioState state, EntityDescriptor descriptor,
        string operation, Func<Task<PersistOutcome>> persist)
    {
        var sw = Stopwatch.StartNew();
        var outcome = await persist().ConfigureAwait(false);
        var ms = sw.Elapsed.TotalMilliseconds;
        state.Bench.Record(operation, descriptor.LogicalName, ms);
        if (state.DeepBenchmark) state.Bench.Record("persist", descriptor.LogicalName, ms);
        TdmDiagnostics.PersistDuration.Record(ms,
            new KeyValuePair<string, object?>("entity", descriptor.LogicalName),
            new KeyValuePair<string, object?>("verb", operation),
            new KeyValuePair<string, object?>("route", outcome.Route));
        return outcome;
    }

    private void FinishEntry(ScenarioState state, EntityManifest entry, Stopwatch? sw, bool journalEntry = true)
    {
        // Bulk rows carry no per-entity timing — the chunk persist time lives in the
        // benchmark stats and the bulk summary.
        if (sw is not null)
            entry.DurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 3);
        state.Manifest.Entities.Add(entry);
        // Bulk rows are journalled at chunk flush (all of them, sampled or not) —
        // journalEntry: false stops those sampled in the manifest being written twice.
        if (journalEntry)
            JournalEntity(state, entry);
    }

    /// <summary>Journal an entity outcome (W3-D6). "Persisted" means resume may skip it:
    /// a real persist route, no warnings — dry runs never journal (no writer is passed).</summary>
    private void JournalEntity(ScenarioState state, EntityManifest entry)
    {
        var persisted = entry.PersistedVia is { } via && via != "dry-run" && entry.Warnings.Count == 0;
        journal?.Entity(state.Key, entry, persisted);
    }

    /// <summary>
    /// Step-level problem (unresolved entity/faker/reference, unmatched step, failed assertion):
    /// BestEffort/FailObject → warn + skip step; FailRun → abort run (handoff §10).
    /// </summary>
    private void StepProblem(ScenarioState state, StepPlan step, string message)
    {
        var full = $"[line {step.Line}] {message}";
        state.Manifest.Warnings.Add(full);
        _log.LogWarning("{Message} (step: \"{Step}\")", message, step.Text);
        if (settings.Run.FailurePolicy == FailurePolicy.FailRun)
            throw new TdmRunAbortedException(full);
        if (settings.Run.FailurePolicy == FailurePolicy.FailObject)
            state.Failed = true;
    }

    /// <summary>Object-level problem (persist failure): BestEffort → warn + skip object; FailRun → abort.</summary>
    private void ObjectProblem(ScenarioState state, string message)
    {
        state.Manifest.Warnings.Add(message);
        _log.LogWarning("{Message}", message);
        if (settings.Run.FailurePolicy == FailurePolicy.FailRun)
            throw new TdmRunAbortedException(message);
        if (settings.Run.FailurePolicy == FailurePolicy.FailObject)
            state.Failed = true;
    }

    /// <summary>
    /// Property-level problem: BestEffort → warn + skip property; FailObject → reject object;
    /// FailRun → abort run.
    /// </summary>
    private void PropertyProblem(ScenarioState state, EntityManifest entry, string message)
    {
        entry.Warnings.Add(message);
        _log.LogWarning("{Message}", message);
        switch (settings.Run.FailurePolicy)
        {
            case FailurePolicy.FailRun: throw new TdmRunAbortedException(message);
            case FailurePolicy.FailObject: throw new TdmObjectRejectedException(message);
        }
    }

    private static string InformationalVersion(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? assembly.GetName().Version?.ToString() ?? "unknown";

    private static string LoadedAssemblyVersion(string simpleName)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase));
        return assembly is null ? "" : InformationalVersion(assembly);
    }
}
