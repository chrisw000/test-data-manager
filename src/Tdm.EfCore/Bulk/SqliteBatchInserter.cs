using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Tdm.EfCore.Bulk;

/// <summary>
/// Multi-row INSERT batching for SQLite (W3-D3): one statement per ~900 parameters instead of
/// one row per statement, on the context's connection/transaction. SQLite has no wire-level
/// bulk API — fewer, larger statements is its fast path.
/// </summary>
public sealed class SqliteBatchInserter : IBulkInserter
{
    public static readonly SqliteBatchInserter Instance = new();

    /// <summary>SQLITE_MAX_VARIABLE_NUMBER is 999 in older builds — stay under it.</summary>
    private const int MaxParametersPerStatement = 900;

    public string Route => "Sqlite(batch)";

    public async Task InsertAsync(DbContext context, IEntityType entityType, BulkTableMap map,
        IReadOnlyList<object> rows, CancellationToken ct)
    {
        var helper = context.GetService<ISqlGenerationHelper>();
        var table = helper.DelimitIdentifier(map.TableName, map.Schema);
        var columnList = string.Join(", ", map.Columns.Select(c => helper.DelimitIdentifier(c.ColumnName)));
        var rowsPerStatement = Math.Max(1, MaxParametersPerStatement / Math.Max(1, map.Columns.Count));

        var connection = context.Database.GetDbConnection();
        var wasClosed = connection.State != System.Data.ConnectionState.Open;
        if (wasClosed) await context.Database.OpenConnectionAsync(ct).ConfigureAwait(false);

        // A chunk spans several INSERT statements — make it atomic. Enlist in the scenario's
        // transaction when there is one (Transactional lifecycle); otherwise the chunk gets
        // its own, so a failed chunk never leaves untracked partial rows behind.
        var ambient = context.Database.CurrentTransaction?.GetDbTransaction();
        var local = ambient is null ? await connection.BeginTransactionAsync(ct).ConfigureAwait(false) : null;
        try
        {
            for (var offset = 0; offset < rows.Count; offset += rowsPerStatement)
            {
                ct.ThrowIfCancellationRequested();
                var batch = Math.Min(rowsPerStatement, rows.Count - offset);

                await using var command = connection.CreateCommand();
                command.Transaction = ambient ?? local;

                var sql = new StringBuilder($"INSERT INTO {table} ({columnList}) VALUES ");
                var parameterIndex = 0;
                for (var r = 0; r < batch; r++)
                {
                    sql.Append(r == 0 ? "(" : ", (");
                    for (var c = 0; c < map.Columns.Count; c++)
                    {
                        var name = $"@p{parameterIndex++}";
                        sql.Append(c == 0 ? name : ", " + name);
                        var parameter = command.CreateParameter();
                        parameter.ParameterName = name;
                        parameter.Value = map.Columns[c].ProviderValue(rows[offset + r]) ?? DBNull.Value;
                        command.Parameters.Add(parameter);
                    }
                    sql.Append(')');
                }
                command.CommandText = sql.ToString();
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            if (local is not null) await local.CommitAsync(ct).ConfigureAwait(false);
        }
        catch when (local is not null)
        {
            await local.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (local is not null) await local.DisposeAsync().ConfigureAwait(false);
            if (wasClosed) await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}
