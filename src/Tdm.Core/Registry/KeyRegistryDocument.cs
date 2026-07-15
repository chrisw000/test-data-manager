using System.Text.Json;
using Tdm.Core.Settings;

namespace Tdm.Core.Registry;

/// <summary>
/// tdm.keys.json (W2-D6): a domain's registry of the natural keys that participate in
/// cross-domain identity, shipped inside its data package/plugin folder so it versions and
/// ships alongside the code it describes. Makes the v1 accepted constraint — "natural keys
/// referenced across domains must be stable and agreed" — machine-checked at validate time.
/// </summary>
public sealed class KeyRegistryDocument
{
    public const string FileName = "tdm.keys.json";

    public int RegistryVersion { get; set; } = 1;
    public string Domain { get; set; } = "";
    public Dictionary<string, EntityKeyRegistry> Entities { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads tdm.keys.json from a plugin folder, or null if the domain hasn't published one.</summary>
    public static KeyRegistryDocument? TryLoad(string pluginFolder)
    {
        var path = Path.Combine(pluginFolder, FileName);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<KeyRegistryDocument>(File.ReadAllText(path), TdmSettings.JsonOptions);
    }

    /// <summary>
    /// True if <paramref name="key"/> is a declared value for <paramref name="entity"/> —
    /// exact match against <see cref="EntityKeyRegistry.Keys"/>, or a
    /// <see cref="EntityKeyRegistry.KeyPattern"/> match. An entity absent from the registry
    /// is treated as <b>not governed</b> (always true) — registries are adopted incrementally
    /// per entity, so omission is not itself a violation.
    /// </summary>
    public bool IsKeyKnown(string entity, string key)
    {
        if (!Entities.TryGetValue(entity, out var registry)) return true;
        if (registry.Keys.Contains(key, StringComparer.Ordinal)) return true;
        return registry.KeyPattern is { Length: > 0 } pattern &&
               System.Text.RegularExpressions.Regex.IsMatch(key, pattern);
    }
}

public sealed class EntityKeyRegistry
{
    public string NaturalKey { get; set; } = "";
    public List<string> Keys { get; set; } = [];
    /// <summary>Regex covering a generated key space (e.g. "^SKU-\\d{4}-[A-Z]{2}$"), for
    /// entities with too many keys to enumerate.</summary>
    public string? KeyPattern { get; set; }
}
