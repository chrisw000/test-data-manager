using System.Collections.Concurrent;
using System.Text;

namespace Tdm.Core.Generation;

/// <summary>
/// A parsed correlated-tuple dataset (W4-D5): CSV, first row is the header. Rows are
/// sampled whole so correlated fields (city↔postcode↔country) stay consistent.
/// </summary>
public sealed class DatasetTable
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Header { get; init; }
    public required IReadOnlyList<IReadOnlyList<string>> Rows { get; init; }

    public int ColumnIndex(string column)
    {
        for (var i = 0; i < Header.Count; i++)
            if (string.Equals(Header[i], column, StringComparison.OrdinalIgnoreCase)) return i;
        throw new InvalidOperationException(
            $"Dataset '{Name}' has no column '{column}'. Columns: {string.Join(", ", Header)}.");
    }

    public static DatasetTable Load(string name, string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"Dataset '{name}': file not found: {path}");
        var records = ParseCsv(File.ReadAllText(path));
        if (records.Count < 2)
            throw new InvalidOperationException(
                $"Dataset '{name}' ({path}) needs a header row and at least one data row.");
        return new DatasetTable { Name = name, Header = records[0], Rows = records.Skip(1).ToList() };
    }

    /// <summary>Minimal RFC-4180: quoted fields may contain commas, newlines and doubled quotes.</summary>
    private static List<IReadOnlyList<string>> ParseCsv(string text)
    {
        var records = new List<IReadOnlyList<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': inQuotes = true; break;
                case ',': record.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n':
                    record.Add(field.ToString()); field.Clear();
                    if (record.Count > 1 || record[0].Length > 0) records.Add(record);
                    record = [];
                    break;
                default: field.Append(c); break;
            }
        }
        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            if (record.Count > 1 || record[0].Length > 0) records.Add(record);
        }
        return records;
    }
}

/// <summary>Lazy, thread-safe dataset cache — parallel scenario sessions share parsed tables.</summary>
public sealed class DatasetStore(IReadOnlyDictionary<string, Settings.DatasetSettings> datasets, string? baseDirectory)
{
    private readonly ConcurrentDictionary<string, DatasetTable> _cache = new(StringComparer.OrdinalIgnoreCase);

    public DatasetTable Get(string name)
    {
        if (!datasets.TryGetValue(name, out var settings))
        {
            throw new InvalidOperationException(
                $"Unknown dataset '{name}'. Configured datasets: " +
                (datasets.Count == 0 ? "(none)" : string.Join(", ", datasets.Keys)) +
                ". Declare it under \"datasets\" in tdm.settings.json.");
        }
        return _cache.GetOrAdd(name, n =>
            DatasetTable.Load(n, Path.GetFullPath(settings.Path, baseDirectory ?? Directory.GetCurrentDirectory())));
    }
}
