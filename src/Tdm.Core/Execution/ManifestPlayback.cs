using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tdm.Core.Conversion;
using Tdm.Core.Manifest;
using Tdm.Core.Naming;
using Tdm.Core.Settings;

namespace Tdm.Core.Execution;

public sealed class ReplayReport
{
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Deleted { get; set; }
    public int Skipped { get; set; }
    public List<string> Warnings { get; } = [];
    public List<string> Failures { get; } = [];
    public int ExitCode => Failures.Count > 0 ? 2 : Warnings.Count > 0 ? 1 : 0;
}

public sealed class DriftReport
{
    public int RowsChecked { get; set; }
    public int SkippedScenarios { get; set; }
    /// <summary>Each line: one missing row, surviving deleted row, or changed property.</summary>
    public List<string> Drift { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<string> Errors { get; } = [];
    public int ExitCode => Errors.Count > 0 ? 2 : Drift.Count > 0 ? 1 : 0;
}

/// <summary>
/// Manifest playback (W2-D9): <see cref="ReplayAsync"/> re-creates exactly the rows a
/// manifest records — final values, not fakers, including DB-resolved reference ids (FK
/// columns are part of the value snapshot) — and <see cref="VerifyAsync"/> asserts every
/// recorded row still exists with its recorded values (the scheduled "has anyone corrupted
/// the shared environment" job). Both consume the manifest only; no feature files needed —
/// the manifest is the reproducibility contract, and this proves it.
/// Only Persistent scenarios play back: Transactional/TrackedTeardown scenarios deliberately
/// left no rows behind.
/// </summary>
public static class ManifestPlayback
{
    public static async Task<ReplayReport> ReplayAsync(RunManifest manifest,
        IReadOnlyList<IDomainRuntime> runtimes, ILogger? logger = null, CancellationToken ct = default)
    {
        var log = logger ?? NullLogger.Instance;
        var report = new ReplayReport();

        foreach (var scenario in manifest.Scenarios)
        {
            ct.ThrowIfCancellationRequested();
            if (scenario.Lifecycle != LifecycleMode.Persistent || scenario.Outcome == ScenarioOutcome.Skipped)
            {
                report.Skipped += scenario.Entities.Count;
                continue;
            }

            foreach (var bulk in scenario.BulkOperations.Where(b => b.HashedRows > 0))
            {
                report.Warnings.Add(
                    $"[{scenario.Scenario}] bulk create of {bulk.Count} {bulk.Entity} row(s) was recorded in " +
                    $"{bulk.Mode} mode — only the {bulk.SampledRows} sampled row(s) can be replayed; " +
                    $"re-run the original feature to reproduce the full set.");
            }

            // Replay always runs Persistent — the rows are the point.
            foreach (var runtime in runtimes)
                await runtime.BeginScenarioAsync(LifecycleMode.Persistent, scenario.Seed, ct).ConfigureAwait(false);
            try
            {
                foreach (var entry in scenario.Entities.OrderBy(e => e.Ordinal))
                    await ReplayEntryAsync(entry, scenario, runtimes, report, ct).ConfigureAwait(false);
            }
            finally
            {
                foreach (var runtime in runtimes)
                    await runtime.EndScenarioAsync(ct).ConfigureAwait(false);
            }
        }

        log.LogInformation("Replay finished: {Created} created, {Updated} updated, {Deleted} deleted, " +
                           "{Skipped} skipped, {Warnings} warning(s), {Failures} failure(s)",
            report.Created, report.Updated, report.Deleted, report.Skipped, report.Warnings.Count, report.Failures.Count);
        return report;
    }

    private static async Task ReplayEntryAsync(EntityManifest entry, ScenarioManifest scenario,
        IReadOnlyList<IDomainRuntime> runtimes, ReplayReport report, CancellationToken ct)
    {
        if (entry.PersistedVia == "dry-run") { report.Skipped++; return; }

        switch (entry.Verb)
        {
            case "Create" or "Projection":
            {
                if (Resolve(entry, runtimes, report.Failures) is not var (runtime, descriptor)) return;

                // Idempotent replay: an existing row (same id / natural key) has the recorded
                // values re-applied rather than colliding — declared-state semantics again.
                var existing = await FindRecordedRowAsync(runtime, descriptor, entry, ct).ConfigureAwait(false);
                if (existing is not null)
                {
                    ApplyValues(descriptor, existing, entry, report.Warnings);
                    var updated = await runtime.UpdateAsync(descriptor, existing, ct).ConfigureAwait(false);
                    if (updated.Success) report.Updated++;
                    else report.Failures.Add($"{Label(entry, scenario)}: re-applying values failed: {updated.Error}");
                    return;
                }

                object instance;
                try { instance = Activator.CreateInstance(descriptor.ClrType)!; }
                catch (Exception ex) when (ex is MissingMethodException or MemberAccessException)
                {
                    report.Failures.Add($"{Label(entry, scenario)}: {descriptor.ClrType.Name} has no parameterless constructor.");
                    return;
                }
                ApplyValues(descriptor, instance, entry, report.Warnings);
                var outcome = await runtime.CreateAsync(descriptor, instance, ct: ct).ConfigureAwait(false);
                if (outcome.Success) report.Created++;
                else report.Failures.Add($"{Label(entry, scenario)}: persist failed: {outcome.Error}");
                return;
            }

            case "Update":
            {
                if (Resolve(entry, runtimes, report.Failures) is not var (runtime, descriptor)) return;
                var target = await FindRecordedRowAsync(runtime, descriptor, entry, ct).ConfigureAwait(false);
                if (target is null)
                {
                    report.Failures.Add($"{Label(entry, scenario)}: row to update not found (id {entry.Id ?? "-"}, key \"{entry.NaturalKey ?? "-"}\").");
                    return;
                }
                ApplyValues(descriptor, target, entry, report.Warnings);
                var outcome = await runtime.UpdateAsync(descriptor, target, ct).ConfigureAwait(false);
                if (outcome.Success) report.Updated++;
                else report.Failures.Add($"{Label(entry, scenario)}: update failed: {outcome.Error}");
                return;
            }

            case "Delete" when entry.Id is not null:
            {
                if (Resolve(entry, runtimes, report.Failures) is not var (runtime, _)) return;
                // False = already absent — fine, replay is idempotent.
                if (await runtime.DeleteByIdAsync(entry.Entity, entry.Id, ct).ConfigureAwait(false))
                    report.Deleted++;
                return;
            }

            case "Delete":
                report.Warnings.Add($"{Label(entry, scenario)}: delete-all steps record only a count, not the " +
                                    "affected ids — this deletion cannot be replayed exactly.");
                return;

            default:
                report.Skipped++;
                return;
        }
    }

    public static async Task<DriftReport> VerifyAsync(RunManifest manifest,
        IReadOnlyList<IDomainRuntime> runtimes, ILogger? logger = null, CancellationToken ct = default)
    {
        var log = logger ?? NullLogger.Instance;
        var report = new DriftReport();

        // Fold the manifest into the expected end state: last write per row wins, single
        // deletes become tombstones, delete-alls make that entity type unverifiable (the
        // affected ids were never recorded).
        var expected = new Dictionary<string, (EntityManifest Entry, ScenarioManifest Scenario, bool Deleted)>(StringComparer.OrdinalIgnoreCase);
        foreach (var scenario in manifest.Scenarios)
        {
            if (scenario.Lifecycle != LifecycleMode.Persistent || scenario.Outcome == ScenarioOutcome.Skipped)
            {
                report.SkippedScenarios++;
                continue;
            }
            foreach (var bulk in scenario.BulkOperations.Where(b => b.HashedRows > 0))
            {
                report.Warnings.Add(
                    $"[{scenario.Scenario}] bulk create of {bulk.Count} {bulk.Entity} row(s) was recorded in " +
                    $"{bulk.Mode} mode — only the {bulk.SampledRows} sampled row(s) are verifiable.");
            }
            foreach (var entry in scenario.Entities.OrderBy(e => e.Ordinal))
            {
                if (entry.PersistedVia == "dry-run") continue;
                switch (entry.Verb)
                {
                    case "Create" or "Projection" or "Update":
                        expected[RowKey(entry)] = (entry, scenario, Deleted: false);
                        break;
                    case "Delete" when entry.Id is not null:
                        expected[RowKey(entry)] = (entry, scenario, Deleted: true);
                        break;
                    case "Delete":
                    {
                        var prefix = $"{entry.Domain}|{entry.Entity}|";
                        foreach (var key in expected.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                            expected.Remove(key);
                        report.Warnings.Add($"[{scenario.Scenario}] delete-all of {entry.Entity} recorded no ids — " +
                                            $"{entry.Entity} rows from earlier steps are unverifiable and were skipped.");
                        break;
                    }
                }
            }
        }

        foreach (var (entry, scenario, deleted) in expected.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (Resolve(entry, runtimes, report.Errors) is not var (runtime, descriptor)) continue;

            object? actual;
            try { actual = await FindRecordedRowAsync(runtime, descriptor, entry, ct).ConfigureAwait(false); }
            catch (InvalidOperationException ex)
            {
                report.Errors.Add($"{Label(entry, scenario)}: {ex.Message}");
                continue;
            }
            report.RowsChecked++;

            if (deleted)
            {
                if (actual is not null)
                    report.Drift.Add($"{Label(entry, scenario)}: row was deleted by the run but exists again.");
                continue;
            }
            if (actual is null)
            {
                report.Drift.Add($"{Label(entry, scenario)}: row is missing (id {entry.Id ?? "-"}, key \"{entry.NaturalKey ?? "-"}\").");
                continue;
            }

            // Recorded values were produced by the same SnapshotValues stringification —
            // string equality is exact, with one normalisation (see ValuesEqual).
            var actualValues = descriptor.SnapshotValues(actual);
            foreach (var (property, recorded) in entry.Values)
            {
                if (!actualValues.TryGetValue(property, out var current))
                {
                    report.Warnings.Add($"{Label(entry, scenario)}: property '{property}' no longer exists on {descriptor.ClrType.Name}.");
                    continue;
                }
                if (!ValuesEqual(recorded, current))
                    report.Drift.Add($"{Label(entry, scenario)}: {property} is \"{current}\", manifest recorded \"{recorded}\".");
            }
        }

        log.LogInformation("Verify finished: {Checked} row(s) checked, {Drift} drift finding(s), {Warnings} warning(s)",
            report.RowsChecked, report.Drift.Count, report.Warnings.Count);
        return report;
    }

    // ---------------------------------------------------------------- Shared

    private static string RowKey(EntityManifest entry) =>
        $"{entry.Domain}|{entry.Entity}|{entry.Id ?? "nk:" + entry.NaturalKey}";

    private static (IDomainRuntime Runtime, EntityDescriptor Descriptor)? Resolve(
        EntityManifest entry, IReadOnlyList<IDomainRuntime> runtimes, List<string> problems)
    {
        var runtime = runtimes.FirstOrDefault(r => string.Equals(r.Name, entry.Domain, StringComparison.OrdinalIgnoreCase));
        if (runtime is null)
        {
            problems.Add($"{entry.Domain}.{entry.Entity}: domain '{entry.Domain}' is not configured.");
            return null;
        }
        if (!runtime.TryResolveEntity(entry.Entity, out var descriptor, out var error))
        {
            problems.Add($"{entry.Domain}.{entry.Entity}: {error ?? "entity not found in the domain."}");
            return null;
        }
        return (runtime, descriptor!);
    }

    /// <summary>Ids are the strongest handle (natural keys can be re-pointed); fall back to natural key.</summary>
    private static async Task<object?> FindRecordedRowAsync(IDomainRuntime runtime, EntityDescriptor descriptor,
        EntityManifest entry, CancellationToken ct)
    {
        if (entry.Id is not null && descriptor.KeyProperty is not null)
            return await runtime.FindByIdAsync(descriptor, entry.Id, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(entry.NaturalKey) && descriptor.NaturalKeyProperty is not null)
            return await runtime.FindByNaturalKeyAsync(descriptor, entry.NaturalKey, ct).ConfigureAwait(false);
        return null;
    }

    private static void ApplyValues(EntityDescriptor descriptor, object instance, EntityManifest entry, List<string> warnings)
    {
        foreach (var (name, raw) in entry.Values)
        {
            var property = PropertyMatcher.Find(descriptor.ClrType, name);
            if (property is null)
            {
                warnings.Add($"{entry.Domain}.{entry.Entity}: recorded property '{name}' no longer exists (or is read-only) — skipped.");
                continue;
            }
            if (raw is null)
            {
                var isNullable = !property.PropertyType.IsValueType || Nullable.GetUnderlyingType(property.PropertyType) is not null;
                if (isNullable) property.SetValue(instance, null);
                continue;
            }
            if (!ValueConverter.TryConvert(raw, property.PropertyType, out var value, out var error))
            {
                warnings.Add($"{entry.Domain}.{entry.Entity}: property '{name}': {error}");
                continue;
            }
            property.SetValue(instance, value);
        }
    }

    /// <summary>
    /// Two database round-trip asymmetries are not drift:
    /// (1) DateTime Kind — manifests record in-memory instances where UTC stamps carry
    ///     Kind=Utc (trailing "Z"); the database materialises them Kind=Unspecified.
    ///     Identical ticks = identical stored value.
    /// (2) Decimal scale — providers without a native decimal type (SQLite stores TEXT)
    ///     don't preserve trailing zeros: 633.80 reads back as 633.8. Numerically equal
    ///     values where at least one side has a fractional part are the same value.
    ///     (Dot required so genuinely different strings like "007" vs "7" stay drift.)
    /// </summary>
    private static bool ValuesEqual(string? recorded, string? current)
    {
        if (string.Equals(recorded, current, StringComparison.Ordinal)) return true;
        if (recorded is null || current is null) return false;

        if ((recorded.Contains('.') || current.Contains('.')) &&
            decimal.TryParse(recorded, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var recordedNumber) &&
            decimal.TryParse(current, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var currentNumber))
        {
            return recordedNumber == currentNumber;
        }

        if (DateTime.TryParse(recorded, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var recordedTime) &&
            DateTime.TryParse(current, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var currentTime))
        {
            return recordedTime.Ticks == currentTime.Ticks;
        }
        return false;
    }

    private static string Label(EntityManifest entry, ScenarioManifest scenario) =>
        $"[{scenario.Scenario}] {entry.Domain}.{entry.Entity} #{entry.Ordinal}";
}
