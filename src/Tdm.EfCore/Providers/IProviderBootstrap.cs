using Microsoft.EntityFrameworkCore;
using Tdm.EfCore.Bulk;

namespace Tdm.EfCore.Providers;

/// <summary>
/// The provider plugin seam (W3-D5): everything TDM needs from a database provider — a
/// settings name, options configuration, connection-string hygiene and an optional
/// provider-native bulk inserter. Sqlite and SqlServer ship in-box; further providers
/// (e.g. Tdm.Providers.PostgreSql) implement this in their own package and are discovered
/// from plugin assemblies, so Tdm.EfCore never references providers it doesn't own.
/// </summary>
public interface IProviderBootstrap
{
    /// <summary>Matched case-insensitively against <c>domains[].provider</c>, e.g. "PostgreSql".</summary>
    string Name { get; }

    /// <summary>The EF runtime provider name (<c>context.Database.ProviderName</c>),
    /// e.g. "Npgsql.EntityFrameworkCore.PostgreSQL" — maps a live context back to its bootstrap.</summary>
    string EfProviderName { get; }

    /// <summary>Provider-native bulk inserter (W3-D3), or null to always use the portable EF path.</summary>
    IBulkInserter? BulkInserter { get; }

    /// <summary>Connection-string hygiene, applied before <see cref="Configure"/> — normalise the
    /// string and prepare its environment (e.g. Sqlite creates the data directory).</summary>
    string PrepareConnectionString(string connectionString) => connectionString;

    void Configure(DbContextOptionsBuilder builder, string connectionString);
}
