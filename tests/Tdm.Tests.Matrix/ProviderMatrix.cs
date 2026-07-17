using Microsoft.Data.SqlClient;
using Npgsql;
using Tdm.EfCore.Providers;
using Tdm.Providers.PostgreSql;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace Tdm.Tests.Matrix;

/// <summary>
/// The provider test matrix (W3-P3): <c>TDM_TEST_PROVIDER</c> selects Sqlite (default),
/// SqlServer or PostgreSql for the whole test process, so the same EfCore + integration
/// suites prove all three providers. Container providers start one Testcontainer lazily per
/// process; each fixture gets uniquely named databases inside it (created by EnsureCreated,
/// discarded with the container). SQLite keeps the v1 temp-file behaviour.
/// </summary>
public static class ProviderMatrix
{
    public static string ProviderName { get; } =
        Environment.GetEnvironmentVariable("TDM_TEST_PROVIDER") is { Length: > 0 } provider
            ? provider
            : "Sqlite";

    static ProviderMatrix()
    {
        // In-proc registration: tests reference the provider package directly (secondary
        // compile-time mode); the host discovers it from plugin folders instead (W3-D5).
        ProviderRegistry.Register(new PostgreSqlProviderBootstrap());
    }

    public static bool IsSqlite => ProviderName.Equals("Sqlite", StringComparison.OrdinalIgnoreCase);

    /// <summary>The bulk-insert manifest route the current provider's native inserter reports.</summary>
    public static string ExpectedBulkRoute => ProviderName.ToLowerInvariant() switch
    {
        "sqlite" => "Sqlite(batch)",
        "sqlserver" => "SqlBulkCopy",
        "postgresql" => "Npgsql(COPY)",
        var other => throw new InvalidOperationException($"TDM_TEST_PROVIDER '{other}' is not part of the matrix."),
    };

    private static readonly Lazy<Task<MsSqlContainer>> SqlServerContainer = new(async () =>
    {
        var container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await container.StartAsync().ConfigureAwait(false);
        return container;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<Task<PostgreSqlContainer>> PostgreSqlContainer = new(async () =>
    {
        var container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await container.StartAsync().ConfigureAwait(false);
        return container;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Connection strings for one isolated fixture — a fresh pair of databases named
    /// <paramref name="databases"/> with a unique suffix. Blocks on first-use container start;
    /// fixture constructors are synchronous by design.</summary>
    public static TestDatabases CreateDatabases(params string[] databases)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        switch (ProviderName.ToLowerInvariant())
        {
            case "sqlite":
            {
                var directory = Path.Combine(Path.GetTempPath(), "tdm-provider-matrix", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                return new TestDatabases(ProviderName, directory,
                    databases.ToDictionary(d => d, d => $"Data Source={Path.Combine(directory, d + ".db")}"));
            }
            case "sqlserver":
            {
                var container = Await(SqlServerContainer.Value);
                return new TestDatabases(ProviderName, null, databases.ToDictionary(d => d, d =>
                    new SqlConnectionStringBuilder(container.GetConnectionString())
                    {
                        InitialCatalog = $"tdm_{d}_{suffix}",
                    }.ConnectionString));
            }
            case "postgresql":
            {
                var container = Await(PostgreSqlContainer.Value);
                return new TestDatabases(ProviderName, null, databases.ToDictionary(d => d, d =>
                    new NpgsqlConnectionStringBuilder(container.GetConnectionString())
                    {
                        Database = $"tdm_{d}_{suffix}",
                    }.ConnectionString));
            }
            default:
                throw new InvalidOperationException(
                    $"TDM_TEST_PROVIDER '{ProviderName}' is not part of the matrix (Sqlite, SqlServer, PostgreSql).");
        }
    }

    // xunit runs without a synchronization context; Task.Run keeps the one-time container
    // start off the caller's thread pool queue path regardless.
    private static T Await<T>(Task<T> task) => Task.Run(() => task).GetAwaiter().GetResult();
}

/// <summary>One fixture's databases: logical name → connection string in the active provider.</summary>
public sealed class TestDatabases(string provider, string? sqliteDirectory, IReadOnlyDictionary<string, string> connectionStrings)
    : IAsyncDisposable
{
    public string Provider { get; } = provider;

    public string this[string database] => connectionStrings[database];

    public ValueTask DisposeAsync()
    {
        // Container databases die with the per-process container (Ryuk); only SQLite
        // leaves files behind.
        if (sqliteDirectory is not null)
            try { Directory.Delete(sqliteDirectory, recursive: true); } catch { /* best effort */ }
        return ValueTask.CompletedTask;
    }
}
