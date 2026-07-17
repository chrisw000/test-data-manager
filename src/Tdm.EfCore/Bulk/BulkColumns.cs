using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Tdm.EfCore.Bulk;

/// <summary>One insertable column: EF store name, CLR source property and provider conversion.</summary>
public sealed class BulkColumn
{
    public required string ColumnName { get; init; }
    public required PropertyInfo Property { get; init; }
    public required Type ProviderClrType { get; init; }
    public ValueConverter? Converter { get; init; }

    /// <summary>The value the provider stores — CLR value put through the EF value converter.</summary>
    public object? ProviderValue(object row)
    {
        var value = Property.GetValue(row);
        if (value is null) return null;
        return Converter is null ? value : Converter.ConvertToProvider(value);
    }
}

/// <summary>Everything a provider-native inserter needs to write one entity's table.</summary>
public sealed class BulkTableMap
{
    public required string TableName { get; init; }
    public string? Schema { get; init; }
    public required IReadOnlyList<BulkColumn> Columns { get; init; }
}

/// <summary>
/// Pure projection of EF metadata into a provider-insertable column map (W3-D3). Rows are CLR
/// instances, so an entity qualifies only when every required column is reachable from a CLR
/// property; store-generated columns are excluded (and disqualify the entity when they are
/// key columns — provider bulk paths don't propagate generated keys back). Ineligible
/// entities fall back to the EF path with the reason surfaced in logs.
/// </summary>
public static class BulkColumns
{
    private static readonly ConcurrentDictionary<IEntityType, (BulkTableMap? Map, string? Reason)> Cache = new();

    public static bool TryMap(IEntityType entityType, out BulkTableMap? map, out string? reason)
    {
        var (cachedMap, cachedReason) = Cache.GetOrAdd(entityType, static t => Build(t));
        map = cachedMap;
        reason = cachedReason;
        return map is not null;
    }

    private static (BulkTableMap?, string?) Build(IEntityType entityType)
    {
        var tableName = entityType.GetTableName();
        if (tableName is null)
            return (null, "entity is not mapped to a table");

        var schema = entityType.GetSchema();
        var store = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(tableName, schema);
        var keyProperties = entityType.FindPrimaryKey()?.Properties ?? [];
        var columns = new List<BulkColumn>();

        foreach (var property in entityType.GetProperties())
        {
            var isKey = keyProperties.Contains(property);
            var storeGenerated = property.ValueGenerated is ValueGenerated.OnAddOrUpdate or ValueGenerated.OnUpdate
                                 || property.GetComputedColumnSql() is not null
                                 || (property.ValueGenerated == ValueGenerated.OnAdd &&
                                     (property.GetDefaultValueSql() is not null || IsIntegral(property.ClrType)));

            if (storeGenerated)
            {
                // Identity/computed keys can't round-trip through a bulk path.
                if (isKey) return (null, $"key column '{property.Name}' is store-generated");
                continue; // the database fills it
            }

            if (property.PropertyInfo is null)
            {
                if (property.IsNullable || property.GetDefaultValueSql() is not null || property.TryGetDefaultValue(out _))
                    continue; // shadow with a safe default — let the database handle it
                return (null, $"required shadow property '{property.Name}' has no CLR source");
            }

            var converter = property.GetTypeMapping().Converter;
            columns.Add(new BulkColumn
            {
                ColumnName = property.GetColumnName(store) ?? property.GetColumnName(),
                Property = property.PropertyInfo,
                Converter = converter,
                ProviderClrType = Nullable.GetUnderlyingType(converter?.ProviderClrType ?? property.ClrType)
                                  ?? converter?.ProviderClrType ?? property.ClrType,
            });
        }

        if (columns.Count == 0) return (null, "no insertable columns");
        return (new BulkTableMap { TableName = tableName, Schema = schema, Columns = columns }, null);
    }

    private static bool IsIntegral(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(short);
    }
}
