using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Tdm.EfCore.Bulk;

namespace Tdm.Providers.PostgreSql;

/// <summary>
/// Binary COPY for PostgreSQL (W3-D3): rows are projected through the EF value converters and
/// streamed with <c>COPY ... FROM STDIN (FORMAT BINARY)</c> on the context's connection. COPY
/// is a single statement, so it participates in the scenario's transaction when one is active
/// (Transactional lifecycle) and is atomic per chunk on its own — an aborted stream writes
/// nothing. Values are sent with the column's declared store type: binary COPY has no server-
/// side coercion, so CLR-type inference alone would reject e.g. an int written to a bigint.
/// </summary>
public sealed class PostgreSqlBinaryCopyInserter : IBulkInserter
{
    public static readonly PostgreSqlBinaryCopyInserter Instance = new();

    public string Route => "Npgsql(COPY)";

    public async Task InsertAsync(DbContext context, IEntityType entityType, BulkTableMap map,
        IReadOnlyList<object> rows, CancellationToken ct)
    {
        var helper = context.GetService<ISqlGenerationHelper>();
        var table = helper.DelimitIdentifier(map.TableName, map.Schema);
        var columnList = string.Join(", ", map.Columns.Select(c => helper.DelimitIdentifier(c.ColumnName)));
        var dataTypeNames = map.Columns.Select(c => DataTypeName(c.StoreType)).ToArray();

        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed) await context.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var importer = await connection.BeginBinaryImportAsync(
                $"COPY {table} ({columnList}) FROM STDIN (FORMAT BINARY)", ct).ConfigureAwait(false);

            foreach (var row in rows)
            {
                await importer.StartRowAsync(ct).ConfigureAwait(false);
                for (var c = 0; c < map.Columns.Count; c++)
                {
                    var value = map.Columns[c].ProviderValue(row);
                    if (value is null)
                        await importer.WriteNullAsync(ct).ConfigureAwait(false);
                    else
                        await importer.WriteAsync(value, dataTypeNames[c], ct).ConfigureAwait(false);
                }
            }
            await importer.CompleteAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (wasClosed) await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Store type → wire data type name: length/precision facets are a declaration
    /// concern, not a wire one ("numeric(18,2)" → "numeric", "timestamp(6) with time zone"
    /// → "timestamp with time zone").</summary>
    public static string DataTypeName(string storeType)
    {
        var open = storeType.IndexOf('(');
        if (open < 0) return storeType;
        var close = storeType.IndexOf(')', open);
        if (close < 0) return storeType[..open].TrimEnd();
        return (storeType[..open].TrimEnd() + storeType[(close + 1)..]).Trim();
    }
}
