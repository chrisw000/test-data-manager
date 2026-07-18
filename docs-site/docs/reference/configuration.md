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
  "bulkChunkSize": 500,              // bulk generate/persist batch size — `tdm bench tune` measures the best
  "bulkStrategy": "Provider",        // Provider (SqlBulkCopy / multi-row INSERT / binary COPY) | EfCore (portable AddRange)
  "manifestBulkValues": "Sample",    // All | Sample | None — manifest detail for count-bulk creates (W3-D4)
  "manifestBulkSampleRows": 5,       // rows kept with full values at each end in Sample mode
  "maxParallelScenarios": 1,         // >1 runs scenarios concurrently (W3-D1); steps stay sequential
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
- **bulkStrategy / manifestBulkValues** — count-bulk creates stream in O(chunk) memory
  through provider-native inserters (SqlBulkCopy, SQLite multi-row INSERT, PostgreSQL binary COPY) with the EF path
  as fallback; `Sample` mode keeps manifests usable at a million rows (head/tail values +
  count + value hash). See
  [bulk-and-streaming](https://github.com/chrisw000/test-data-manager/blob/main/docs/bulk-and-streaming.md).
- **maxParallelScenarios** — the scenario is the unit of parallelism; manifests record
  scenarios in plan order regardless of completion order, and per-scenario seeds keep the
  data identical to a serial run. Any domain's own `maxParallelScenarios` caps the run's;
  Transactional scenarios on SQLite auto-serialise with a warning (single-writer). Best for
  disjoint seeding — see
  [parallel-execution](https://github.com/chrisw000/test-data-manager/blob/main/docs/parallel-execution.md).

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
  "provider": "Sqlite",                       // Sqlite | SqlServer (in-box) | PostgreSql (plugin) | any registered IProviderBootstrap
  "connectionString": "Data Source=./output/orders.db",
  "connectionStringName": "OrdersDb",         // else resolved from env: TDM_CONNECTIONSTRINGS__ORDERSDB
  "conventionProfile": "modern",              // see Convention profiles
  "persistence": "RepositoryFirst",           // RepositoryFirst | DbContextOnly | RepositoryOnly
  "externalReferences": "Synthesize",         // Synthesize | Verify | Trust
  "verifyEndpoint": "https://crm/api/{entity}/{id}",  // Verify mode URL template
  "ensureCreated": true,                      // create schema on first use — local/demo only
  "maxParallelScenarios": 1,                  // optional cap on run.maxParallelScenarios for this domain
  "locale": "en_GB",                          // Bogus vocabulary for generated names/addresses (W4-D5); determinism unchanged
  "api": { … }                                // seed via the domain's HTTP API instead of its DbContext — see below
}]
```

### domains[].api — API seeding (W4-D6)

With `"persistence": "Api"`, seeding routes through the domain's public HTTP API — which
exercises its validation, auth and side-effects for free. Supported lifecycles:
`Persistent` and `TrackedTeardown` (deletes via API, reverse order); `Transactional`
fails validation.

```jsonc
"api": {
  "baseUrl": "https://orders.internal",
  "auth": { … },                       // header/bearer schemes; secrets via the secrets chain
  "timeoutSeconds": 30,
  "maxRetries": 2,                     // per request, on 5xx/connection failure
  "retryDelayMs": 200,
  "entities": {                        // this map IS the domain's entity list in Api mode
    "Customer": {
      "create": "POST /api/customers",
      "read":   "GET /api/customers?name={key}",   // {id} = key value, {key} = URL-escaped natural key
      "delete": "DELETE /api/customers/{id}"
    }
  }
}
```

See the [API seeding guide](../guides/api-seeding.md) and
[api-seeding.md](https://github.com/chrisw000/test-data-manager/blob/main/docs/api-seeding.md)
(engineering record).

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
| `properties` | Per-property statistical generation — see below |

### entities.{X}.properties — statistical generation (W4-D4)

Declarative distributions applied over the faker's output, drawn from the per-scenario
seed — realistic shapes, still deterministic. Step overrides always win. Exactly one of
`distribution`, `weights` or `dataset` per property:

```jsonc
"Order": {
  "naturalKey": "OrderNumber",
  "properties": {
    "Status": { "weights": { "Pending": 0.6, "Shipped": 0.3, "Cancelled": 0.1 } },
    "Total":  { "distribution": "lognormal", "mean": 120, "sigma": 1.2 },
    "City":   { "dataset": "uk-places", "column": "City" }
  }
}
```

| Key | Meaning |
|---|---|
| `distribution` | `normal` \| `lognormal` \| `uniform` \| `exponential` |
| `mean` | normal: the mean · lognormal: the **median** (scale, `exp(μ)`) · exponential: the mean (`1/rate`) |
| `sigma` | normal: standard deviation · lognormal: σ of the underlying normal |
| `min` / `max` | uniform bounds; an optional clamp for the others |
| `decimals` | rounding for floating targets (default 2); integer targets round whole |
| `weights` | categorical value → relative weight (normalised at sample time) |
| `dataset` / `column` | fill from a named [dataset](#datasets) row; column defaults to the property name |

Engineering record:
[statistical-generation.md](https://github.com/chrisw000/test-data-manager/blob/main/docs/statistical-generation.md).

## datasets

Named CSV files (first row = header, paths relative to the settings file) whose rows are
sampled **whole** — all properties of one entity naming the same dataset are filled from
a single sampled row, so city ↔ postcode ↔ country stay consistent (W4-D5):

```jsonc
"datasets": {
  "uk-places": { "path": "./datasets/uk-places.csv" }
}
```

## seedPacks

Versioned packages of feature files + entity-config fragments + key-registry entries,
executed **before** local features (pack list order, alphabetical within) — "EU reference
customers v2" becomes a dependency, not a copy-paste (W4-D7). NuGet packs ride the plugin
feeds and are pinned in `tdm.plugins.lock.json`; a local `path` wins for development:

```jsonc
"seedPacks": [
  { "package": "Acme.SeedPacks.EuCustomers", "version": "2.*" },
  { "path": "../shared-packs/eu-customers" }
]
```

Pack layout: `features/*.feature`, optional `tdm.entities.json` fragment, optional
`tdm.keys.json` registry, optional `datasets/`. Applied packs and versions are recorded
in each manifest's `run.seedPacks`. See the [seed packs guide](../guides/seed-packs.md).

## statsPacks

Paths of [`tdm profile`](cli.md#tdm-profile) statistics packs whose derived config this
workspace uses (W4-D8). Declared so every run's attribution records them (name + content
hash) — the attestation posture stays truthful about production-derived shapes:

```jsonc
"statsPacks": ["./tdm.stats.json"]
```

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

Every command and option lives on the dedicated [CLI reference](cli.md), which docs-CI
drift-checks against the live `tdm --help` output. A composite GitHub Action wrapping
validate/run/report ships in-repo at `.github/actions/tdm` — see the
[CI guide](../guides/ci.md).

## Where next

- [CLI reference](cli.md) — the switches that override these settings per run.
- [Convention profiles](profiles.md) — what `conventionProfile` selects.
- [Reports & the manifest](reports-and-manifest.md) — where `outputPath` artifacts come from.
