# TDM Wave 3 — Scale & Performance: Implementation Handoff

**Status:** Design proposed, ready for review
**Audience:** Implementing engineer / AI pair
**Owner:** Chris (Engineering Manager)
**Date:** 2026-07-13
**Depends on:** Wave 1 (packaging/CI), Wave 2 (policy engine — perf gates reuse it); v1 baseline

---

## 1. Purpose

Take the TDM from "seeds a scenario" to "seeds millions of rows across many domains, fast,
and proves it isn't getting slower": parallel execution, provider-grade bulk insert,
PostgreSQL support behind a provider plugin interface, resumable runs, and a benchmark trend
store with CI regression gates.

**Non-goals (this wave):** new grammar/generation features, reporting UI, API-based seeding
(Wave 4).

---

## 2. Deliverables

### 2.1 Parallel scenario execution (Decisions W3-D1, W3-D2)

Per-scenario seeds (v1 D8) already guarantee scenarios are order-independent in their
*generated data*; this wave makes them execution-independent too.

- **Unit of parallelism = scenario.** Steps within a scenario stay strictly sequential
  (references depend on step order). `run.maxParallelScenarios` (default 1 — current
  behaviour preserved) with per-domain override.
- **Runtime pooling:** `DomainRuntime` becomes a per-scenario instance created by a
  `DomainRuntimeFactory` that builds the expensive parts **once** (EF model, entity/repo/
  faker bindings — all immutable after build) and stamps out cheap per-scenario execution
  state (contexts, transactions, faker instances, tracked rows). v1's mutable per-scenario
  fields move into a `ScenarioSession` object; the engine holds one session per in-flight
  scenario.
- Manifest ordering stays deterministic: scenarios are recorded in plan order regardless of
  completion order; ordinal-derived identities are unaffected (scenario-scoped by design).
- Safety: scenarios that target the same natural keys can still contend at the database —
  documented guidance (parallelism suits disjoint/bulk seeding; idempotent create-or-reuse
  makes collisions converge rather than fail).

### 2.2 Provider-grade bulk insert + streaming generation (Decisions W3-D3, W3-D4)

- `IBulkInserter` per provider: **SqlBulkCopy** (SqlServer), **binary COPY** (PostgreSQL,
  §2.3), multi-row `INSERT` batching (SQLite). The v1 chunked `AddRange` path remains the
  portable fallback (`"bulkStrategy": "Provider" | "EfCore"`).
- **Streaming generation:** the count-bulk path (`Given 1000000 Products exist ...`)
  becomes a producer/consumer pipeline — generate → override → identity → bulk-write in
  bounded batches — so memory is O(chunk), not O(count).
- **Manifest at volume** (`run.manifestBulkValues`): `All` (v1 behaviour) | `Sample`
  (first/last N rows' full values + count + value-hash of the rest) | `None` (count + hash).
  Default `Sample`. TrackedTeardown at volume switches from per-row instance tracking to
  **set-based delete** by the recorded id range/predicate.
- Chunk-size auto-tune (open item from v1 §16): `tdm bench tune` measures a matrix of chunk
  sizes against the target database and writes the best into settings.

### 2.3 PostgreSQL + provider plugin interface (Decision W3-D5)

- `IProviderBootstrap` (name → configure `DbContextOptionsBuilder`, connection-string
  hygiene, optional `IBulkInserter`), discovered from provider packages:
  `Tdm.Providers.PostgreSql` (Npgsql, version-aligned to the EF baseline) joins the in-box
  Sqlite/SqlServer. `Tdm.EfCore` stops hard-referencing providers it doesn't own.
- Integration test matrix runs the full EfCore + integration suites against SQLite,
  LocalDB/SQL container, and PostgreSQL container (Testcontainers in CI).

### 2.4 Resumable runs (Decision W3-D6)

- The run additionally writes a **JSONL journal** (one line per persisted entity/step
  outcome, flushed eagerly) alongside the end-of-run manifest.
- `tdm run --resume <journal>`: skips scenarios recorded complete; within a partially
  complete scenario, skips persisted ordinals (identity determinism + idempotent
  create-or-reuse make the skip check cheap and safe). Resumed runs record
  `"resumedFrom"` in attribution.

### 2.5 Benchmark trend store + CI perf gates (Decisions W3-D7, W3-D8)

- `tdm publish --manifest <file> --store <url>`: pushes manifests to blob storage
  (Azure Blob / S3) under `{env}/{run-name}/{timestamp}`, maintaining a small JSON index.
- `tdm bench compare --baseline <ref>`: compares the current run's benchmark stats against a
  named baseline (rolling median of last N runs, or a pinned manifest); output as table +
  JUnit so regressions render in CI.
- **Perf gates live in the Wave 2 policy file** (one enforcement pipeline, not two):

```jsonc
"benchmarks": { "gates": [ { "operation": "create:Order", "stat": "p95Ms", "maxRegressionPct": 20 } ] }
```

- **Grafana dashboard pack**: provisioning JSON for the existing `Tdm` OTEL metrics
  (created/updated/deleted/failed counters, step/persist histograms) + starter alert rules,
  shipped in-repo so teams import rather than rebuild.

---

## 3. Decisions log

| # | Decision | Rationale |
|---|---|---|
| W3-D1 | Scenario is the parallelism unit; steps stay sequential | Matches the determinism model (per-scenario seeds); references need step order |
| W3-D2 | Factory + per-scenario sessions instead of locking one shared runtime | Immutable bindings built once; no lock contention; simpler reasoning |
| W3-D3 | Provider-native bulk with EF fallback, behind IBulkInserter | 10–100× bulk throughput where it matters; portability preserved |
| W3-D4 | Manifest sampling mode for bulk (default Sample) | A million-row manifest is unusable; hash keeps it audit-meaningful |
| W3-D5 | Providers become plugin packages via IProviderBootstrap | PostgreSQL lands without touching Tdm.EfCore; more providers follow the same seam |
| W3-D6 | Eager JSONL journal + deterministic-id skip check for resume | Crash-safe progress without a coordination service |
| W3-D7 | Trend store = blob + index, no database/service | Cheapest durable thing CI can write to; registry (W2) links to it |
| W3-D8 | Perf gates are policy rules evaluated by the W2 engine | One policy pipeline; perf budgets sit next to volume caps |

## 4. Phases

1. **W3-P1 — Sessions & parallelism:** runtime factory/session refactor (behaviour-neutral at
   `maxParallelScenarios: 1`), then parallel execution + tests.
2. **W3-P2 — Bulk & streaming:** IBulkInserter (SqlServer, SQLite), streaming pipeline,
   manifest sampling, set-based teardown, `bench tune`.
3. **W3-P3 — PostgreSQL:** IProviderBootstrap seam, Npgsql provider package, Testcontainers matrix.
4. **W3-P4 — Resume + trends:** journal + `--resume`; `publish`, `bench compare`, policy perf
   gates, Grafana pack.

## 5. Acceptance criteria

- 1M-row bulk create into SQL Server completes via SqlBulkCopy with O(chunk) memory and a
  `Sample` manifest; TrackedTeardown removes all rows set-based.
- `maxParallelScenarios: 8` on the sample domains yields identical manifests (values and
  identities) to a serial run, modulo timings.
- Full test suite green on SQLite, SQL Server and PostgreSQL in CI.
- Killing a 100k-row run mid-flight and resuming produces exactly the same final row set as
  an uninterrupted run.
- A deliberate 25% p95 regression on `create:Order` fails the pipeline via a policy gate.

## 6. Risks & mitigations

| Risk | Mitigation |
|---|---|
| Parallel scenarios deadlock on shared tables | Default remains serial; docs steer parallelism to disjoint seeding; lock-timeout surfaced per failure policy |
| Bulk inserters bypass repositories (domain behaviour not exercised) | Already v1 semantics for bulk (D-bulk uses DbContext); document clearly; per-entity opt-out to row-wise repo path |
| Session refactor destabilises v1 behaviour | Phase W3-P1 lands behaviour-neutral first, guarded by the existing 189-test suite before parallelism switches on |
| Benchmark noise on shared CI agents flakes gates | Gates compare against rolling-median baselines with configurable tolerance; `--quarantine` mode reports without failing |

## 7. Open items

- Whether journal replaces the in-memory manifest builder entirely (single write path) or
  complements it (proposed: complement first).
- Cosmos/Mongo (non-relational) demand — would need an `IDomainRuntime` beyond EF; explicitly
  out of scope until a real team asks.
- Cross-scenario parallel `Transactional` mode on SQLite (single-writer) — document as
  unsupported combination or serialise automatically (proposed: auto-serialise with warning).
