# Database providers & the plugin seam (W3-D5)

TDM talks to a database through an `IProviderBootstrap` (Tdm.EfCore.Providers): a settings
name, `DbContextOptionsBuilder` configuration, connection-string hygiene, and an optional
provider-native `IBulkInserter`. Sqlite and SqlServer ship in-box, pre-registered in the
`ProviderRegistry`; every other provider is a **plugin package** — Tdm.EfCore never
references providers it doesn't own.

```jsonc
"domains": [ { "name": "Orders", "provider": "PostgreSql", "connectionStringName": "Orders" } ]
```

| Provider (settings name) | Package | Bulk route |
|---|---|---|
| `Sqlite` | in-box | `Sqlite(batch)` — multi-row INSERT |
| `SqlServer` | in-box | `SqlBulkCopy` |
| `PostgreSql` | `Tdm.Providers.PostgreSql` | `Npgsql(COPY)` — binary COPY |

## How a provider package reaches the host

The provider package travels as a **NuGet dependency of the domain data package**, so it
lands in the domain's plugin folder alongside the domain assemblies. At plugin load:

1. `PluginLoadContext` loads `Tdm.Providers.*` assemblies **from the folder** — the one
   exception to the "`Tdm.*` is host-provided" sharing rule (`IProviderBootstrap` itself
   lives in the shared Tdm.EfCore, so type identity unifies with the host).
2. The host scans the loaded assemblies and registers every concrete `IProviderBootstrap`
   (`ProviderRegistry.DiscoverFrom`) **before** the domain runtime activates any context.
3. `domains[].provider` resolves through the registry; an unknown name fails fast listing
   the registered providers.

Embedding TDM as a library (compile-time mode)? Reference the provider package directly and
call `ProviderRegistry.Register(new PostgreSqlProviderBootstrap())`.

## PostgreSQL specifics

- `Npgsql.EntityFrameworkCore.PostgreSQL` is version-aligned to the org EF baseline
  (see [compatibility](compatibility.md)); the EF version-skew check applies to provider
  packages like any other plugin assembly.
- Bulk creates use wire-level binary `COPY … FROM STDIN (FORMAT BINARY)` on the context's
  connection, enlisting in the scenario transaction. Values are written with the column's
  declared store type (binary COPY has no server-side coercion). A COPY stream is atomic
  per chunk: an aborted stream writes nothing.
- PostgreSQL `timestamp` precision is microseconds; .NET ticks are 100 ns. Manifest
  `verify` compares temporals at the coarsest common store precision (1 µs), so a faithful
  round-trip never reads as drift.

## The provider test matrix (W3-P3)

The full EfCore + integration suites run against all three providers. `TDM_TEST_PROVIDER`
selects the provider for the test process (`Sqlite` default — no Docker needed);
`SqlServer` and `PostgreSql` start one Testcontainer per process, with each test fixture
getting uniquely named databases inside it. CI runs the matrix on every push/PR
(`provider-matrix` job).

```bash
TDM_TEST_PROVIDER=PostgreSql dotnet test tests/Tdm.EfCore.Tests tests/Tdm.Integration.Tests
```

Note: `mcr.microsoft.com/mssql/server` images are amd64-only — on ARM hosts (Apple
Silicon, Windows-on-ARM) the SqlServer leg needs an amd64 Docker host; the PostgreSql leg
runs natively everywhere.

## Writing a new provider

1. New package `Tdm.Providers.{Name}` referencing Tdm.EfCore and the EF provider,
   version-aligned to the baseline.
2. Implement `IProviderBootstrap` (public, parameterless constructor — discovery
   instantiates it). Implement `IBulkInserter` if the provider has a native bulk API;
   return null to use the portable EF path.
3. Domain packages that target the provider add the package as a dependency — nothing else
   changes host-side.
