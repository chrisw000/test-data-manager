# Resumable runs & benchmark trends (W3-D6 / W3-D7 / W3-D8)

## The run journal (W3-D6)

Every real `tdm run` writes a **JSONL journal** next to the manifest
(`{run}-{timestamp}.tdm.journal.jsonl`): one eagerly-flushed line per scenario boundary and
per persisted entity outcome. Where the manifest is the *end-of-run* audit artifact, the
journal is the *during-the-run* crash record — kill the process at any point and the
journal still says exactly what reached the database (at most the line being written is
lost, and a truncated final line is ignored on read). The journal complements the manifest;
it does not replace it.

## `tdm run --resume <journal>`

```bash
tdm run --settings tdm.settings.json --resume output/orders-seed-20260717-140302.tdm.journal.jsonl
```

Re-runs the same plan, skipping what the journal proves was done:

- **Scenarios recorded complete** are skipped whole (manifest outcome `Skipped`, with the
  journal path in the warning). The resumed run's own journal records them complete too,
  so a resume of a resume works.
- **Within a partially complete scenario**, generation still runs for every ordinal —
  seeded faker sequences must stay aligned — but ordinals recorded persisted skip their
  persist call (`persistedVia: "resumed"`). For count-bulk creates this is the part that
  matters: re-inserting journalled rows through a provider bulk path would collide on
  their deterministic keys.
- `delete all` steps recorded done are **not** re-run — re-deleting would wipe rows whose
  re-creation the same resume then skips.
- The plan and seeds must match the interrupted run. A seed mismatch on a partial scenario
  is detected and that scenario re-runs fully (safe: identity determinism + idempotent
  create-or-reuse converge on the existing rows) with a warning.

Resumed runs record the journal path as `run.attribution.resumedFrom` in the manifest.

Anything *not* recorded persisted is simply retried — the journal is an optimisation and a
proof, not a lock. That's what makes the skip check safe: the worst case of a lost journal
line is one redundant, idempotent write.

## The trend store (W3-D7)

```bash
tdm publish --manifest output/orders-seed-20260717-140302.tdm.json --store /mnt/tdm-trends
```

Pushes a manifest to the trend store under `{env}/{run-name}/{timestamp}.tdm.json`
(environment from the manifest's `--env` recording, overridable with `--env`), maintaining
a small `index.json` at the root. The store is **blob + index, no database or service**:
the shipped implementation is a directory — local, network share, or blob storage
mounted/synced by CI (`azcopy`, `aws s3 sync`, artifact stores). Native Azure Blob / S3
adapters implement `Tdm.Observability.Trends.ITrendStore` host-side; TDM ships no cloud
SDKs (same posture as `ISecretProvider`).

## `tdm bench compare` + perf gates (W3-D8)

```bash
tdm bench compare --manifest output/current.tdm.json --store /mnt/tdm-trends \
    --env ci --report junit=output/bench.junit.xml
```

Compares the run's benchmark stats against a baseline and evaluates the **perf gates in
the policy file** — one enforcement pipeline; perf budgets sit next to the volume caps:

```jsonc
// tdm.policy.json
"environments": {
  "ci": {
    "maxBulkRowsPerStep": 100000,
    "benchmarks": {
      "gates": [ { "operation": "create:Order", "stat": "p95Ms", "maxRegressionPct": 20 } ]
    }
  }
}
```

- **Baseline**: `--store <root>` uses the per-stat **rolling median** of the last
  `--baseline-runs` (default 5) stored runs for this run name + environment — medians
  absorb noisy shared CI agents. `--baseline <manifest>` pins one instead.
- **Gates**: `operation` is a benchmark key (`create`, or `create:Order`); `stat` is
  `meanMs | p50Ms | p95Ms | maxMs | totalMs`. A gate fails when current exceeds baseline
  by more than `maxRegressionPct` — exit 2, so the pipeline fails. Missing data never
  fails a gate (first runs have no baseline).
- **Output**: comparison table on stdout; `--report junit=<path>` renders each gate as a
  test case so regressions show up as failed tests in CI.
- **`--quarantine`** reports failures without failing the pipeline — for known-noisy
  agents while a better baseline accumulates.

Order matters in CI: **compare first, then publish** the current manifest — the baseline
should not include the run being judged (compare drops a same-timestamp entry if it does).

```yaml
- run: tdm run --settings tdm.settings.json --env ci --benchmark true
- run: tdm bench compare --manifest output/*.tdm.json --store $TREND_STORE --env ci --report junit=output/bench.junit.xml
- run: tdm publish --manifest output/*.tdm.json --store $TREND_STORE
```

## Grafana pack

`grafana/` ships a provisioning-ready dashboard and starter alert rules over the existing
`Tdm` OTEL metrics — see [grafana/README.md](../grafana/README.md). The alert thresholds
and the policy perf gates are two views of the same budget: CI gates catch regressions
before merge, the dashboard alerts catch environment drift after.
