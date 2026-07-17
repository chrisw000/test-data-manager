using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Tdm.EfCore.Bulk;

/// <summary>
/// SqlBulkCopy for SQL Server (W3-D3): rows are projected through the EF value converters
/// into a DataTable (see <see cref="BuildTable"/>, pure and testable offline) and streamed
/// over the context's connection, enlisted in its current transaction.
/// </summary>
public sealed class SqlServerBulkInserter : IBulkInserter
{
    public static readonly SqlServerBulkInserter Instance = new();

    public string Route => "SqlBulkCopy";

    public async Task InsertAsync(DbContext context, IEntityType entityType, BulkTableMap map,
        IReadOnlyList<object> rows, CancellationToken ct)
    {
        var table = BuildTable(map, rows);

        var connection = (SqlConnection)context.Database.GetDbConnection();
        var transaction = (SqlTransaction?)context.Database.CurrentTransaction?.GetDbTransaction();
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed) await context.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction)
            {
                DestinationTableName = map.Schema is null ? $"[{map.TableName}]" : $"[{map.Schema}].[{map.TableName}]",
                BatchSize = rows.Count,
                BulkCopyTimeout = 0,
            };
            foreach (var column in map.Columns)
                bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);

            await bulk.WriteToServerAsync(table, ct).ConfigureAwait(false);
        }
        finally
        {
            if (wasClosed) await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Provider-typed staging table for one chunk — DBNull for nulls, converter-applied values.</summary>
    public static DataTable BuildTable(BulkTableMap map, IReadOnlyList<object> rows)
    {
        var table = new DataTable();
        foreach (var column in map.Columns)
            table.Columns.Add(new DataColumn(column.ColumnName, column.ProviderClrType));

        foreach (var row in rows)
        {
            var values = new object?[map.Columns.Count];
            for (var c = 0; c < map.Columns.Count; c++)
                values[c] = map.Columns[c].ProviderValue(row) ?? DBNull.Value;
            table.Rows.Add(values);
        }
        return table;
    }
}
