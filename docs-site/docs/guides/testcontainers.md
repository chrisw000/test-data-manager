---
tour_prev: guides/seed-packs.md
tour_next: guides/complex-domains.md
---

# TestContainers & the provider matrix

**Personas:** developer, platform. The same features should seed identically on SQLite,
SQL Server and PostgreSQL. This repository proves it on every push, and its harness is the
template you copy.

## The matrix, as this repo runs it

`TDM_TEST_PROVIDER` selects the provider; SQLite (temp files) is the default leg, and
SQL Server / PostgreSQL spin up via [Testcontainers](https://testcontainers.com):

```csharp
public static string ProviderName =>
    Environment.GetEnvironmentVariable("TDM_TEST_PROVIDER") ?? "Sqlite";
// SqlServer  → new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
// PostgreSql → new PostgreSqlBuilder("postgres:16-alpine")
```

The `ProviderMatrix` / `TestDomains` harness (`tests/Tdm.Tests.Matrix`,
`tests/Tdm.EfCore.Tests`) starts one container per fixture, hands out isolated
connection strings, and the *same* EfCore and integration suites run unchanged against
whichever provider is selected. Bulk inserts pick the provider-native route each engine
supports:

| Provider | Bulk route the manifest records |
|---|---|
| SQLite | `Sqlite(batch)` (multi-row INSERT) |
| SQL Server | `SqlBulkCopy` |
| PostgreSQL | `Npgsql(COPY)` (binary COPY) |

## Running TDM (not just unit tests) against a container

The same idea applies to `tdm run`: point the domain's connection string at a container.

- **Connection strings** resolve through the [secret chain](cd-environments.md#secrets) —
  `connectionStringName: "OrdersDb"` reads `TDM_CONNECTIONSTRINGS__ORDERSDB`, which your
  test harness sets to the container's string. The settings file never changes between
  providers.
- **Schema** — `ensureCreated: true` is the quick path for throwaway containers; for
  parity with production, run your real EF migrations against the container first and leave
  `ensureCreated` off.
- **PostgreSQL** ships as a provider *plugin* (`Tdm.Providers.PostgreSql`); SQLite and
  SQL Server are in-box. Any `IProviderBootstrap` in a plugin folder is discovered the same
  way — see [providers](https://github.com/chrisw000/test-data-manager/blob/main/docs/providers.md).

## The CI recipe

The repo's `ci.yml` runs a `provider-matrix` job with `fail-fast: false` over
`[SqlServer, PostgreSql]` (SQLite is covered by the default `build-test-validate` job):

```yaml
provider-matrix:
  strategy:
    fail-fast: false
    matrix:
      provider: [SqlServer, PostgreSql]
  steps:
    - uses: actions/checkout@v7
    - uses: actions/setup-dotnet@v5
      with: { dotnet-version: 10.0.x }
    - run: dotnet build --no-restore -warnaserror
    - name: Test on ${{ matrix.provider }}
      env:
        TDM_TEST_PROVIDER: ${{ matrix.provider }}
      run: |
        dotnet test tests/Tdm.EfCore.Tests --no-build
        dotnet test tests/Tdm.Integration.Tests --no-build
```

`fail-fast: false` means a SQL Server failure doesn't cancel the PostgreSQL leg — you see
every provider's result. The container runtime is provided by the GitHub runner; no
services block is needed because Testcontainers manages lifecycles itself.

## Where next

- [Testing complex domains](complex-domains.md) — the Fulfilment domain, seeded on the
  SQLite leg in CI.
- [Performance testing](performance-testing.md) — provider-native bulk routes at scale.
- [Plugin packaging](../reference/plugin-packaging.md) — shipping a provider plugin.

**Guided tour:** next stop → [Testing complex domains](complex-domains.md)
