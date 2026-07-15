# Configuration reference

`tdm.settings.json` (JSON with comments and trailing commas allowed). Scaffold an annotated
starting point with `tdm init`.

## run

```jsonc
"run": {
  "name": "orders-seed",
  "failurePolicy": "BestEffort",     // BestEffort | FailObject | FailRun
  "lifecycle": "TrackedTeardown",    // Persistent | Transactional | TrackedTeardown
  "defaultSeed": 1,
  "featurePaths": ["features/**/*.feature"],
  "benchmark": false,
  "bulkChunkSize": 500,              // AddRange+SaveChanges batch size for count-bulk creates
  "outputPath": "./output",          // manifests land here
  "signing": {                       // optional — see docs/audit-and-signing.md
    "certificatePath": "./keys/tdm-signing.pfx",
    "certificatePasswordEnv": "TDM_SIGNING_CERT_PASSWORD"
  }
}
```

- **failurePolicy** — `BestEffort` logs and continues; `FailObject` skips the failed object;
  `FailRun` aborts the run. Exit codes: `0` succeeded, `1` completed with warnings, `2` failed.
- **lifecycle** — `Persistent` rows stay; `Transactional` rolls everything back at scenario
  end; `TrackedTeardown` deletes created rows in reverse dependency order.
  Scenario tags `@persistent` / `@ephemeral` override per scenario.
- **signing** — every manifest gets a SHA-256 checksum regardless; configuring `signing`
  additionally writes a detached signature (`<manifest>.sig`), verified with
  `tdm manifest verify <file> --cert <public-cert>`.

## plugins

```jsonc
"plugins": {
  "acquisition": "Folder",           // Folder (default) | NuGet
  "feeds": [ { "url": "https://nuget.acme.internal/v3/index.json" } ],
  "cachePath": "~/.tdm/cache"        // downloaded .nupkg cache
}
```

With `NuGet` acquisition, `domains[].package` (+ optional `packageVersion`, floating like
`"3.2.*"` allowed) is resolved from the feeds, transitive dependencies included, **excluding
host-shared assemblies** (framework, EF Core, `Microsoft.Extensions.*`, `Bogus`, `Tdm.*`).
Resolved versions + SHA-512 hashes are pinned in **`tdm.plugins.lock.json`** — commit it.
Re-resolve with `tdm run --update-plugins`. Resolved versions are recorded in each
manifest's `run.pluginPackages`. Feed auth uses the standard nuget.config credential chain;
TDM handles no secrets.

## domains

```jsonc
"domains": [{
  "name": "Orders",
  "package": "Acme.Orders.Data.Persistence",  // NuGet package id (NuGet acquisition)
  "packageVersion": "3.2.*",                  // optional; omit = latest stable
  "pluginPath": "./plugins/Orders",           // Folder acquisition; defaults to ./plugins/{name}
  "provider": "Sqlite",                       // Sqlite | SqlServer
  "connectionString": "Data Source=./output/orders.db",
  "connectionStringName": "OrdersDb",         // else resolved from env: TDM_CONNECTIONSTRINGS__ORDERSDB
  "conventionProfile": "modern",              // see Convention profiles
  "persistence": "RepositoryFirst",           // RepositoryFirst | DbContextOnly | RepositoryOnly
  "externalReferences": "Synthesize",         // Synthesize | Verify | Trust
  "verifyEndpoint": "https://crm/api/{entity}/{id}",  // Verify mode URL template
  "ensureCreated": true                       // create schema on first use — local/demo only
}]
```

## entities

Per-entity overrides, keyed by logical name:

```jsonc
"entities": {
  "Product":  { "naturalKey": "Sku", "requireRepository": false },
  "Order":    { "naturalKey": "OrderNumber" },
  "Customer": { "externalBehavior": "Projection", "projectionEntity": "CustomerSummary" },
  "Odd":      { "writeRepository": "ILegacyOddGateway", "idStrategy": "DbGenerated" }
}
```

| Key | Meaning |
|---|---|
| `naturalKey` | Property used as the business key (profile default: `Name`) |
| `idStrategy` | `Auto` (detected) \| `Deterministic` (identity contract) \| `DbGenerated` |
| `requireRepository` | Overrides the profile's write-repository policy (ADR-0001) for this entity |
| `writeRepository` | Explicit write-repository interface name when the probe patterns don't fit |
| `externalBehavior` | `FkOnly` \| `Projection` (seed a local read-model row for external refs) |
| `projectionEntity` | Logical name of the projection row seeded in `Projection` mode |

## secrets

```jsonc
"secrets": {
  "provider": "Environment",   // default; cloud adapters (AzureKeyVault, …) are host-registered ISecretProviders
  "inline": { "OrdersDb": "Data Source=./output/orders.db" }   // dev only — tried first
}
```

Connection strings (`connectionStringName`), the signing-certificate password, and the
registry API key all resolve through this chain: inline → environment → adapter. TDM ships
no cloud SDKs and stores nothing — see
[secrets-and-playback](https://github.com/chrisw000/test-data-manager/blob/main/docs/secrets-and-playback.md).

## registry

```jsonc
"registry": {
  "url": "https://tdm-registry.internal",  // enables run tracking + environment locks (W2-D7)
  "apiKeyEnv": "TDM_REGISTRY_APIKEY",      // env var holding the key; unset = anonymous
  "unavailable": "Warn",                   // Warn: continue without locks | Fail: refuse (exit 2)
  "lockTtlSeconds": 60,
  "heartbeatSeconds": 20
}
```

When set, `tdm run` registers the run and leases every domain's target database before
seeding — two runs targeting the same database collide, and the second fails fast naming the
holder. See
[run-registry-and-locks](https://github.com/chrisw000/test-data-manager/blob/main/docs/run-registry-and-locks.md).

## Policy as code and the key registry

Opt-in, CLI-driven (not part of `tdm.settings.json`) — see
[policy-and-key-registry](https://github.com/chrisw000/test-data-manager/blob/main/docs/policy-and-key-registry.md)
for the full rule set:

```bash
tdm validate --env shared-dev --policy-file tdm.policy.json
tdm run --env shared-dev --approval "$TOKEN"   # bypasses violations if the environment allows it
```

The natural-key registry (`tdm.keys.json`, shipped inside a domain's plugin output) checks
every external reference against the owning domain's declared keys — always on, no `--env`
needed, never overridable.

## CLI

| Command | Purpose |
|---|---|
| `tdm init [--domain X --package Y]` | Scaffold settings, starter feature, .gitignore, CI workflow |
| `tdm validate [--report sarif=…] [--update-plugins]` | Parse + resolve everything, persist nothing; policy gate; exit 0/1/2 |
| `tdm run [--seed N] [--policy P] [--lifecycle L] [--benchmark] [--report …]` | Seed and write the manifest |
| `tdm teardown --manifest <file>` | Delete manifest-recorded rows in reverse order |
| `tdm list-entities [--domain X]` | Resolved entity → CLR type, keys, faker, write/read repository |
| `tdm explain "<step text>" [--keyword When]` | Every pipeline decision for one step; no DB connection |
| `tdm manifest verify <file> [--cert <public-cert>]` | Check a manifest's checksum and, if present, signature — see [audit-and-signing](https://github.com/chrisw000/test-data-manager/blob/main/docs/audit-and-signing.md) |
| `tdm replay --manifest <file>` | Re-create exactly the rows a manifest records — final values, not fakers (W2-D9) |
| `tdm verify --manifest <file>` | Drift check: every recorded row still exists with its recorded values; exit 0/1 |

`--env`, `--policy-file`, `--approval` (on run/validate): environment-policy enforcement —
see policy as code below.

`--report <format>=<path>` (repeatable, on run/validate): `sarif` for PR annotations,
`junit` for CI test UIs. A composite GitHub Action wrapping all of this ships in-repo at
`.github/actions/tdm`.
