using System.Text.Json;
using Tdm.Core.Execution;
using Tdm.Core.Settings;

namespace Tdm.Core.Model;

/// <summary>
/// The exported entity model (`tdm export-model`, W4-D2): everything editor tooling needs to
/// validate and complete feature files offline — logical names, properties, natural keys,
/// domains — with none of the runtime machinery. Checked into the repo or generated in CI;
/// the language server reads it instead of opening database connections. Output is fully
/// deterministic (ordinal-sorted, no timestamps) so CI can regenerate and `git diff` it.
/// </summary>
public sealed class TdmModel
{
    /// <summary>Schema version of this file, for forward evolution.</summary>
    public int ModelVersion { get; set; } = 1;
    /// <summary>Simple TDM version (no build metadata — the file must be CI-diff stable).</summary>
    public string TdmVersion { get; set; } = "";
    /// <summary>SHA-256 of the tdm.settings.json the model was resolved from — the staleness
    /// signal editor clients compare against the current settings file.</summary>
    public string? SettingsFileSha256 { get; set; }
    public List<TdmModelDomain> Domains { get; set; } = [];

    public string Serialize() => JsonSerializer.Serialize(this, TdmSettings.JsonOptions);

    public static TdmModel Load(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<TdmModel>(stream, TdmSettings.JsonOptions)
               ?? throw new InvalidOperationException($"Model file '{path}' deserialised to null.");
    }

    public IEnumerable<(TdmModelDomain Domain, TdmModelEntity Entity)> AllEntities() =>
        Domains.SelectMany(d => d.Entities.Select(e => (d, e)));
}

public sealed class TdmModelDomain
{
    public string Name { get; set; } = "";
    public List<TdmModelEntity> Entities { get; set; } = [];
}

public sealed class TdmModelEntity
{
    /// <summary>Logical (Gherkin) name, e.g. "Customer".</summary>
    public string Name { get; set; } = "";
    public string ClrType { get; set; } = "";
    /// <summary>Natural-key property name, when configured/detected.</summary>
    public string? NaturalKey { get; set; }
    /// <summary>Key description as `tdm list-entities` prints it, e.g. "Id: Guid (client-set)".</summary>
    public string? Key { get; set; }
    public string? FakerSource { get; set; }
    public List<TdmModelProperty> Properties { get; set; } = [];
}

public sealed class TdmModelProperty
{
    public string Name { get; set; } = "";
    /// <summary>Friendly C# type name, e.g. "string", "decimal?", "DateTime".</summary>
    public string Type { get; set; } = "";
}

public static class TdmModelBuilder
{
    /// <summary>Builds the model from resolved runtimes — the same source of truth as
    /// `tdm list-entities`, so the editor cannot drift from what the engine resolves.</summary>
    public static TdmModel Build(IEnumerable<IDomainRuntime> runtimes, string? settingsFileSha256)
    {
        var model = new TdmModel
        {
            // AssemblyName.Version, not the informational version: the +commit metadata would
            // make the exported file differ on every build and break the CI drift check.
            TdmVersion = typeof(TdmModelBuilder).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            SettingsFileSha256 = settingsFileSha256,
        };

        foreach (var runtime in runtimes.OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            var infos = runtime.DescribeEntities().ToDictionary(i => i.LogicalName, StringComparer.Ordinal);
            var domain = new TdmModelDomain { Name = runtime.Name };
            foreach (var descriptor in runtime.Entities.OrderBy(e => e.LogicalName, StringComparer.Ordinal))
            {
                infos.TryGetValue(descriptor.LogicalName, out var info);
                domain.Entities.Add(new TdmModelEntity
                {
                    Name = descriptor.LogicalName,
                    ClrType = descriptor.ClrType.FullName ?? descriptor.ClrType.Name,
                    NaturalKey = descriptor.NaturalKeyProperty?.Name,
                    Key = info?.KeyInfo,
                    FakerSource = info?.FakerSource,
                    Properties = [.. descriptor.ScalarProperties()
                        .Select(p => new TdmModelProperty { Name = p.Name, Type = FriendlyTypeName(p.PropertyType) })
                        .OrderBy(p => p.Name, StringComparer.Ordinal)],
                });
            }
            model.Domains.Add(domain);
        }
        return model;
    }

    private static string FriendlyTypeName(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying) return FriendlyTypeName(underlying) + "?";
        return type switch
        {
            _ when type == typeof(string) => "string",
            _ when type == typeof(int) => "int",
            _ when type == typeof(long) => "long",
            _ when type == typeof(short) => "short",
            _ when type == typeof(byte) => "byte",
            _ when type == typeof(bool) => "bool",
            _ when type == typeof(decimal) => "decimal",
            _ when type == typeof(double) => "double",
            _ when type == typeof(float) => "float",
            _ when type == typeof(char) => "char",
            _ when type == typeof(object) => "object",
            _ => type.Name,
        };
    }
}
