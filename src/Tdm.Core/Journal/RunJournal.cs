using System.Text.Json;
using System.Text.Json.Serialization;
using Tdm.Core.Manifest;
using Tdm.Core.Settings;

namespace Tdm.Core.Journal;

/// <summary>
/// One line of the JSONL run journal (W3-D6). The journal complements the end-of-run
/// manifest (open item resolved: complement, not replace): it is written and flushed
/// *during* the run — one line per persisted entity outcome and per scenario boundary —
/// so a killed run leaves a crash-safe record of exactly what reached the database.
/// </summary>
public sealed class JournalLine
{
    /// <summary>run-start | scenario-start | entity | scenario-complete.</summary>
    public string Kind { get; set; } = "";
    /// <summary>Scenario key "{feature}|{scenario}|{line}" — stable across identical plans.</summary>
    public string? Scenario { get; set; }
    /// <summary>run-start: the run name; scenario-complete: the outcome.</summary>
    public string? Value { get; set; }
    /// <summary>scenario-start: the effective seed — resume refuses a seed mismatch.</summary>
    public int? Seed { get; set; }
    public int? Ordinal { get; set; }
    public string? Entity { get; set; }
    public string? Verb { get; set; }
    public string? Id { get; set; }
    /// <summary>True when the entity outcome was a successful persist — only these are
    /// skipped on resume; anything else is retried (idempotent create-or-reuse converges).</summary>
    public bool? Persisted { get; set; }
    public DateTime AtUtc { get; set; }
}

/// <summary>
/// Eagerly flushed JSONL journal writer (W3-D6). Thread-safe: parallel scenarios (W3-D1)
/// append from concurrent workers; each line is written and flushed atomically under a lock
/// so a kill can lose at most the line being written — never interleave two.
/// </summary>
public sealed class RunJournalWriter : IDisposable
{
    internal static readonly JsonSerializerOptions LineOptions = new(TdmSettings.JsonOptions)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly StreamWriter _writer;
    private readonly Lock _lock = new();

    public RunJournalWriter(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read));
        FilePath = Path.GetFullPath(path);
    }

    public string FilePath { get; }

    public void RunStarted(string runName) =>
        Append(new JournalLine { Kind = "run-start", Value = runName });

    public void ScenarioStarted(string scenarioKey, int seed) =>
        Append(new JournalLine { Kind = "scenario-start", Scenario = scenarioKey, Seed = seed });

    public void Entity(string scenarioKey, EntityManifest entry, bool persisted) =>
        Append(new JournalLine
        {
            Kind = "entity",
            Scenario = scenarioKey,
            Ordinal = entry.Ordinal,
            Entity = entry.Entity,
            Verb = entry.Verb,
            Id = entry.Id,
            Persisted = persisted,
        });

    public void ScenarioCompleted(string scenarioKey, ScenarioOutcome outcome) =>
        Append(new JournalLine { Kind = "scenario-complete", Scenario = scenarioKey, Value = outcome.ToString() });

    private void Append(JournalLine line)
    {
        line.AtUtc = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(line, LineOptions);
        lock (_lock)
        {
            _writer.WriteLine(json);
            _writer.Flush(); // eager — the journal's whole point is surviving a kill
        }
    }

    public void Dispose()
    {
        lock (_lock) _writer.Dispose();
    }
}

/// <summary>
/// What a previous run's journal proves was done (W3-D6). Scenarios recorded complete are
/// skipped whole; within a partially complete scenario, ordinals recorded persisted skip
/// their persist call (generation still runs, keeping seeded faker sequences aligned).
/// Identity determinism + idempotent create-or-reuse make re-running anything unrecorded
/// safe — the journal is an optimisation and a proof, not a lock.
/// </summary>
public sealed class ResumeState
{
    private readonly HashSet<string> _completed = [];
    private readonly Dictionary<string, HashSet<int>> _persisted = [];
    private readonly Dictionary<string, int> _seeds = [];

    public required string JournalPath { get; init; }

    public bool IsScenarioComplete(string scenarioKey) => _completed.Contains(scenarioKey);

    public bool IsPersisted(string scenarioKey, int ordinal) =>
        _persisted.TryGetValue(scenarioKey, out var ordinals) && ordinals.Contains(ordinal);

    /// <summary>The seed the journalled run used for this scenario, if it started.</summary>
    public int? RecordedSeed(string scenarioKey) =>
        _seeds.TryGetValue(scenarioKey, out var seed) ? seed : null;

    public static ResumeState Load(string path)
    {
        var state = new ResumeState { JournalPath = Path.GetFullPath(path) };
        foreach (var text in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            JournalLine? line;
            try { line = JsonSerializer.Deserialize<JournalLine>(text, RunJournalWriter.LineOptions); }
            catch (JsonException) { continue; } // a kill can truncate the final line — ignore it
            if (line?.Scenario is not { } scenario) continue;

            switch (line.Kind)
            {
                case "scenario-start" when line.Seed is { } seed:
                    state._seeds[scenario] = seed;
                    break;
                case "entity" when line is { Persisted: true, Ordinal: { } ordinal }:
                    (state._persisted.TryGetValue(scenario, out var ordinals)
                        ? ordinals
                        : state._persisted[scenario] = []).Add(ordinal);
                    break;
                case "scenario-complete" when line.Value is nameof(ScenarioOutcome.Succeeded)
                    or nameof(ScenarioOutcome.CompletedWithWarnings) or nameof(ScenarioOutcome.Skipped):
                    state._completed.Add(scenario);
                    break;
            }
        }
        return state;
    }
}
