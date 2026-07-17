using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Tdm.EfCore.Bulk;

/// <summary>
/// Provider-native bulk insert (W3-D3): SqlBulkCopy on SQL Server, multi-row INSERT batching
/// on SQLite, binary COPY on PostgreSQL (Tdm.Providers.PostgreSql). Inserters are contributed
/// by provider bootstraps (W3-D5) and resolved via <c>ProviderRegistry.InserterFor</c>. The
/// chunked EF AddRange path remains the portable fallback for providers (or entities) no
/// inserter can handle. Implementations use the context's open connection and enlist in its
/// current transaction, so Transactional lifecycle semantics hold.
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

