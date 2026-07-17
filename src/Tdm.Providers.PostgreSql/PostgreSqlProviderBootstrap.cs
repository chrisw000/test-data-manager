using Microsoft.EntityFrameworkCore;
using Tdm.EfCore.Bulk;
using Tdm.EfCore.Providers;

namespace Tdm.Providers.PostgreSql;

/// <summary>
/// PostgreSQL bootstrap (W3-D5): plugin-shipped, discovered from a domain's plugin assemblies
/// by the host — Tdm.EfCore never references Npgsql. Settings usage:
/// <c>"provider": "PostgreSql"</c>.
/// </summary>
public sealed class PostgreSqlProviderBootstrap : IProviderBootstrap
{
    public string Name => "PostgreSql";
    public string EfProviderName => "Npgsql.EntityFrameworkCore.PostgreSQL";
    public IBulkInserter? BulkInserter => PostgreSqlBinaryCopyInserter.Instance;

    public void Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(connectionString);
}
