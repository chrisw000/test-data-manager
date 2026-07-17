using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Tdm.EfCore.Bulk;

/// <summary>
/// Provider-native bulk insert (W3-D3): SqlBulkCopy on SQL Server, multi-row INSERT batching
/// on SQLite, binary COPY on PostgreSQL (W3-P3). The chunked EF AddRange path remains the
/// portable fallback for providers (or entities) no inserter can handle. Implementations use
/// the context's open connection and enlist in its current transaction, so Transactional
/// lifecycle semantics hold.
/// </summary>
public interface IBulkInserter
{
    /// <summary>Manifest route label, e.g. "SqlBulkCopy", "Sqlite(batch)".</summary>
    string Route { get; }

    /// <summary>Inserts one already-chunked batch. Throws on failure — the runtime reports
    /// rows persisted before the failing chunk.</summary>
    Task InsertAsync(DbContext context, IEntityType entityType, BulkTableMap map,
        IReadOnlyList<object> rows, CancellationToken ct);
}

/// <summary>Built-in inserter per EF provider. W3-P3 moves this behind IProviderBootstrap so
/// provider plugin packages can contribute their own.</summary>
internal static class BulkInserters
{
    public static IBulkInserter? For(DbContext context) => context.Database.ProviderName switch
    {
        "Microsoft.EntityFrameworkCore.Sqlite" => SqliteBatchInserter.Instance,
        "Microsoft.EntityFrameworkCore.SqlServer" => SqlServerBulkInserter.Instance,
        _ => null,
    };
}
