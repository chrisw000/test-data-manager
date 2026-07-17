using Microsoft.EntityFrameworkCore;
using Tdm.EfCore.Bulk;

namespace Tdm.EfCore.Providers;

/// <summary>In-box SQL Server bootstrap: ships with the host, version-aligned to the org EF baseline.</summary>
public sealed class SqlServerProviderBootstrap : IProviderBootstrap
{
    public string Name => "SqlServer";
    public string EfProviderName => "Microsoft.EntityFrameworkCore.SqlServer";
    public IBulkInserter? BulkInserter => SqlServerBulkInserter.Instance;

    public void Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseSqlServer(connectionString);
}
