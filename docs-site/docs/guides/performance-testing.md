---
tour_prev: guides/cd-environments.md
tour_next: guides/statistical-generation.md
---

# Performance testing & tracking

**Personas:** QA, platform. Seed at scale, measure honestly, and gate regressions before
they reach anyone. This guide's pipeline runs end to end in CI (bounded to 5,000 rows)
against an isolated workspace.

## Seeding at volume

Count-bulk grammar generates and persists in bounded batches, so memory stays O(chunk)
however large the count:

```gherkin
Given 5000 Products exist with category "PerfDemo"
```

- **`run.bulkChunkSize`** — the batch size. Provider-native bulk paths (SqlBulkCopy,
  SQLite multi-row INSERT, PostgreSQL binary COPY) move each batch; the EF `AddRange`
  path is the portable fallback (`run.bulkStrategy`).
- **`tdm bench tune`** measures the best chunk size against *your* database — it inserts
  and deletes across a matrix of sizes and writes the winner into `run.bulkChunkSize`:

```bash
tdm bench tune --settings tdm.settings.json --rows 2000 --chunk-sizes 100,250,500,1000,2000
```

  Point it at a dev database — it writes and cleans up real rows.

## The manifest at scale

A million-row bulk create can't record a million rows verbatim, so `run.manifestBulkValues`
controls the detail:

| Mode | Manifest keeps | Teardown can remove |
|---|---|---|
| `All` | every row's values | everything (exact) |
| `Sample` (default) | head/tail rows + count + a value hash | only the sampled rows |
| `None` | count only | nothing by row |

This is a real tradeoff: under `Sample`, `tdm teardown` removes only the rows it can see,
so **repeatable bulk seeds should clear by filter first** —
`Given all Products with category "PerfDemo" are deleted` — rather than relying on
teardown to undo a sampled bulk. (That is exactly what the pipeline below does.) Use `All`
when exact teardown matters more than manifest size.

## Parallelism & resilience

- **`run.maxParallelScenarios`** — the scenario is the unit of parallelism; steps within
  a scenario stay sequential, and per-scenario seeds keep data identical to a serial run.
  Any domain's own cap lowers the run's. Best for disjoint seeding; SQLite serialises
  writers (single-writer) with a warning.
- **The journal & `--resume`** — every run streams a crash-safe `.tdm.journal.jsonl`.
  After a mid-run kill, `tdm run --resume <journal>` skips scenarios/rows the journal
  proved persisted (plan and seeds must match), so a big seed continues rather than
  restarting.

## Measuring

Turn on benchmarks (`run.benchmark: true` or `--benchmark`) and the manifest records
per-operation stats — `generate` / `resolve` / `override` / `persist` / `load`, each also
broken down `operation:Entity` (`persist:Product`). The HTML report charts them; read
**p95** for tail latency, not just the mean. `@benchmark` on a single scenario captures
stats for just that one.

## Tracking regressions

The loop is **publish → compare → gate**:

1. **Publish** a run's manifest to a trend store — a directory (local, network share, or
   CI-synced blob storage): `tdm publish --manifest <file> --store <root> --env <name>`
   files it under `{env}/{run}/{timestamp}` and maintains an index.
2. **Compare** a later run against the rolling baseline (per-stat median of recent stored
   runs) — perf gates from `tdm.policy.json` decide pass/fail:

```bash
--8<-- "performance-testing/01-perf-pipeline.sh:gate"
```

```text
Benchmark comparison (p95Ms) — baseline: median of last 1 stored run(s) (perf/perf-demo)
operation                            baseline      current     change
persist:Product                        52.012       49.771      -4.3%
gate PASS: persist:Product p95Ms 49.771 ms vs baseline 52.012 ms (-4.3%, max +300%)
```

Gates live in the policy file under the environment's `benchmarks.gates`
(`operation`, `stat`, `maxRegressionPct`). `tdm bench compare` exits `2` on a breach, so
the pipeline fails *before* the manifest is published as a new baseline. For noisy shared
runners, `--quarantine` reports breaches without failing the pipeline.

### The full pipeline

Seed → publish baseline → seed again → gate → publish. This is CI-executed against an
isolated 5k-row workspace on every push:

```bash
--8<-- "performance-testing/01-perf-pipeline.sh"
```

### Dashboards

TDM exports OTLP metrics when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. The in-repo
[Grafana pack](https://github.com/chrisw000/test-data-manager/tree/main/grafana)
(`tdm-dashboard.json` + `tdm-alerts.yaml`) charts created/updated/deleted/failed totals
and p95 step/persist durations by verb, route and entity — import instead of rebuilding.
Set the alert's persist-p95 budget from your `bench tune` / trend history, and **mirror
it as a policy perf gate** so CI fails before the dashboard turns red.

## Where next

- [Statistical generation](statistical-generation.md) — make those bulk rows *shaped*
  like production, so the perf numbers mean something.
- [CD & environments](cd-environments.md) — the publish/verify steps in a deploy.
- [Reports & the manifest](../reference/reports-and-manifest.md) — benchmark stats and
  sparklines in detail.

**Guided tour:** next stop → [Statistical generation](statistical-generation.md)
