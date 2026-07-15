# Run registry and environment locks (W2-P3)

Part of [Wave 2](wave-2-handoff.md), decision W2-D7: a **thin lease + index service**
(`Tdm.Registry`) that answers the cross-team question — *who seeded shared-dev, when, with
what* — and stops two runs from seeding the same database at once. The registry links to
manifests; it never duplicates their content.

## The service

Minimal ASP.NET Core + one small SQLite database, shipped as a container:

```bash
docker run --rm -p 8080:8080 -v tdm-registry-data:/data \
  -e TDM_REGISTRY_APIKEY=change-me \
  ghcr.io/chrisw000/tdm-registry:latest
```

| Endpoint | Purpose |
|---|---|
| `POST /runs` | Register a run start: environment, name, settings hash, runner identity |
| `PATCH /runs/{id}` | Report finish: outcome + manifest URL |
| `GET /runs?environment=…` | The index: last 100 runs, newest first |
| `POST /locks` | Acquire a lease on `(environment, domain, database)` — `201` granted, `409` conflict naming the holder |
| `POST /locks/{id}/heartbeat` | Renew the lease TTL while a run executes |
| `DELETE /locks/{id}` | Release (best-effort; expiry reaps abandoned leases anyway) |
| `GET /locks?environment=…` | Active leases |

Configuration is two environment variables: `TDM_REGISTRY_DB` (SQLite path, default
`./tdm-registry.db`) and `TDM_REGISTRY_APIKEY` (optional shared key; when set, every request
must send it as `X-Tdm-ApiKey` — unset means open, for local development).

**Lease semantics:** one live lease per `(environment, domain, database)` — enforced by a
unique index, so a concurrent race resolves to exactly one winner. Leases expire by TTL
(client default 60s, heartbeat every 20s), and expired leases are reaped on the next
acquisition attempt: a crashed run can block a database for at most one TTL. No-environment
runs still collide with each other (null normalises to `""` — SQLite would otherwise treat
NULL environments as distinct and grant both).

## Host integration

```jsonc
// tdm.settings.json
"registry": {
  "url": "https://tdm-registry.internal",
  "apiKeyEnv": "TDM_REGISTRY_APIKEY",   // env var holding the key; unset = anonymous
  "unavailable": "Warn",                // Warn: continue without locks | Fail: refuse (exit 2)
  "lockTtlSeconds": 60,
  "heartbeatSeconds": 20
}
```

When `registry.url` is set, `tdm run` (never `validate` — it touches no data):

1. registers the run (`POST /runs` with the environment from `--env`, run name, and the
   runner identity from the attribution collector),
2. acquires a lease for **every configured domain's target database** before seeding —
   the lease key is `(environment, domain, sha256(provider|connectionString))`; the
   connection string itself never leaves the host, only its hash,
3. heartbeats all leases while the run executes,
4. on completion releases the leases and reports the outcome + manifest path; the manifest
   records `run.registryRunId`, so registry entry and manifest link both ways.

**Lock conflict** is always fatal, whatever `unavailable` says — that's the point:

```
Registry refusal: Database for domain 'Orders' is locked by billing-team nightly-seed
(ci:jenkins#bob) (acquired 2026-07-15 05:35:17Z, lease expires 2026-07-15 05:37:17Z).
Wait for that run to finish or for the lease to expire.
```

The refused run is recorded in the registry too (outcome `LockConflict`); a run that dies
mid-seed is marked `Aborted` on dispose rather than left dangling.

**Registry unreachable** degrades per policy: `Warn` (default) logs and continues without
registry/locks — the registry must never become the reason seeding can't happen — while
`Fail` refuses with exit 2 for environments where unlocked seeding is unacceptable.

## Caveats

- The collision guarantee is only as good as the lease key: teams must use **byte-identical
  connection strings** (after resolution) for the same physical database. Differently
  formatted strings hash differently and won't collide.
- The registry stores no secrets and no seeded data — run metadata and lease bookkeeping
  only. Retention/backup of its one SQLite file is the operator's concern.
