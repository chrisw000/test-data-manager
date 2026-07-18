using System.Text.Json;
using Tdm.Core.Grammar;
using Tdm.Core.Registry;
using Tdm.Core.Settings;

namespace Tdm.Core.SeedPacks;

/// <summary>The optional tdm.entities.json fragment inside a pack (W4-D7).</summary>
public sealed class SeedPackFragment
{
    public Dictionary<string, EntitySettings> Entities { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DatasetSettings> Datasets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A resolved seed pack on disk: name, version, root folder and parsed content.</summary>
public sealed class SeedPackContent
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string RootFolder { get; init; }
    public SeedPackFragment Fragment { get; init; } = new();
    public KeyRegistryDocument? KeyRegistry { get; init; }

    public const string FragmentFileName = "tdm.entities.json";

    public static SeedPackContent Load(string name, string version, string rootFolder)
    {
        if (!Directory.Exists(rootFolder))
            throw new InvalidOperationException($"Seed pack '{name}': folder not found: {rootFolder}");

        var fragmentPath = Path.Combine(rootFolder, FragmentFileName);
        var fragment = File.Exists(fragmentPath)
            ? JsonSerializer.Deserialize<SeedPackFragment>(File.ReadAllText(fragmentPath), TdmSettings.JsonOptions)
              ?? new SeedPackFragment()
            : new SeedPackFragment();

        return new SeedPackContent
        {
            Name = name,
            Version = version,
            RootFolder = rootFolder,
            Fragment = fragment,
            KeyRegistry = KeyRegistryDocument.TryLoad(rootFolder),
        };
    }
}

/// <summary>
/// Applies resolved packs to a run (W4-D7). Pure functions over settings/plans — the host
/// wires them, the engine stays untouched. Merge rules: local settings win over packs;
/// two packs configuring the same entity/dataset key fail loudly (§6 risk table).
/// </summary>
public static class SeedPackApplier
{
    /// <summary>Merges pack entity-config and dataset fragments *under* local settings.
    /// Pack dataset paths are anchored at the pack root, not the settings file.</summary>
    public static void MergeConfig(TdmSettings settings, IReadOnlyList<SeedPackContent> packs)
    {
        var entitySources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var datasetSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pack in packs)
        {
            foreach (var (entityName, entityConfig) in pack.Fragment.Entities)
            {
                if (entitySources.TryGetValue(entityName, out var previousPack))
                {
                    throw new InvalidOperationException(
                        $"Seed pack conflict: entity '{entityName}' is configured by both '{previousPack}' and " +
                        $"'{pack.Name}'. Resolve it by dropping one pack or overriding the entity locally " +
                        "(local entities.{Name} config always wins).");
                }
                entitySources[entityName] = pack.Name;
                if (!settings.Entities.ContainsKey(entityName)) // local wins
                    settings.Entities[entityName] = entityConfig;
            }

            foreach (var (datasetName, dataset) in pack.Fragment.Datasets)
            {
                if (datasetSources.TryGetValue(datasetName, out var previousPack))
                {
                    throw new InvalidOperationException(
                        $"Seed pack conflict: dataset '{datasetName}' is declared by both '{previousPack}' and '{pack.Name}'.");
                }
                datasetSources[datasetName] = pack.Name;
                if (!settings.Datasets.ContainsKey(datasetName))
                {
                    settings.Datasets[datasetName] = new DatasetSettings
                    {
                        Path = System.IO.Path.GetFullPath(dataset.Path, pack.RootFolder),
                    };
                }
            }
        }
    }

    /// <summary>
    /// Pack features execute before local features, in deterministic order: pack list order,
    /// then alphabetical within a pack (the parser sorts) — two repos consuming the same
    /// pack version produce identical plans, and therefore identical identities (§5).
    /// </summary>
    public static SeedingPlan BuildPlan(GherkinPlanParser parser, IReadOnlyList<SeedPackContent> packs,
        IEnumerable<string> localFeaturePaths, string baseDirectory)
    {
        var plan = new SeedingPlan();
        foreach (var pack in packs)
        {
            var packPlan = parser.ParsePaths(["features/**/*.feature"], pack.RootFolder);
            plan.Features.AddRange(packPlan.Features);
        }
        plan.Features.AddRange(parser.ParsePaths(localFeaturePaths, baseDirectory).Features);
        return plan;
    }

    /// <summary>
    /// Pack key-registry entries (W4-D6/W2-D6) join the checker's map. A domain's own
    /// plugin-published registry is authoritative; packs only add domains it hasn't covered.
    /// Two packs publishing a registry for the same domain fail loudly.
    /// </summary>
    public static Dictionary<string, KeyRegistryDocument> CollectKeyRegistries(
        IReadOnlyList<SeedPackContent> packs, IReadOnlyDictionary<string, KeyRegistryDocument> pluginRegistries)
    {
        var registries = new Dictionary<string, KeyRegistryDocument>(pluginRegistries, StringComparer.OrdinalIgnoreCase);
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in packs)
        {
            if (pack.KeyRegistry is not { } registry) continue;
            if (string.IsNullOrWhiteSpace(registry.Domain))
                throw new InvalidOperationException($"Seed pack '{pack.Name}': tdm.keys.json has no \"domain\".");
            if (sources.TryGetValue(registry.Domain, out var previousPack))
            {
                throw new InvalidOperationException(
                    $"Seed pack conflict: packs '{previousPack}' and '{pack.Name}' both publish a key registry " +
                    $"for domain '{registry.Domain}'.");
            }
            sources[registry.Domain] = pack.Name;
            registries.TryAdd(registry.Domain, registry); // the domain's own plugin registry wins
        }
        return registries;
    }
}
