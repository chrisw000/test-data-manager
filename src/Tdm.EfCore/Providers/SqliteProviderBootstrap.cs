using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Tdm.EfCore.Bulk;

namespace Tdm.EfCore.Providers;

/// <summary>In-box SQLite bootstrap: ships with the host, version-aligned to the org EF baseline.</summary>
public sealed class SqliteProviderBootstrap : IProviderBootstrap
{
    public string Name => "Sqlite";
    public string EfProviderName => "Microsoft.EntityFrameworkCore.Sqlite";
    public IBulkInserter? BulkInserter => SqliteBatchInserter.Instance;

    // SQLite creates the database file but not its directory.
    public string PrepareConnectionString(string connectionString)
    {
        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrEmpty(dataSource) ||
            dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)) return connectionString;
        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        return connectionString;
    }

    public void Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseSqlite(connectionString);
}
