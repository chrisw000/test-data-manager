---
name: tdm-perf-analyst
description: >-
  Check or gate TDM seeding performance from benchmark stats and the trend store. Use when
  asked to "check performance", "is seeding slower?", "gate the seed time", "tune bulk
  insert", or when a perf gate fails in CI. Proposes gate/quarantine changes with evidence.
---

# Analysing TDM seeding performance

Performance is measured, stored and gated by the tool — reason from its numbers, not from
wall-clock impressions.

## Loop

1. **Get benchmark stats.** Seed with `run.benchmark` on (or `tdm run --benchmark`); the
   manifest records per-stage timings and the bulk route each entity took
   (`Sqlite(batch)`, `SqlBulkCopy`, `Npgsql(COPY)`). This is the raw signal.
2. **Consult the trend store.** `tdm report --store <trends> --trend-runs N` shows the
   recent series for each measured stage — is this run an outlier or a trend?
3. **Compare against a baseline.** `tdm bench compare --store <trends>
   --baseline-runs N --stat <p50|p95|mean>` computes the current run against a rolling
   baseline and exits `2` on regression. Use `p95` for gates (tail latency), `p50` to
   describe typical cost.
4. **Tune bulk insert** if a specific entity dominates: `tdm bench tune --entity <E>
   --rows <n> --chunk-sizes 500,1000,2000` measures chunk sizes and writes the best back
   to `tdm.settings.json` (`--no-write` to only report).
5. **Propose a change with evidence.** A gate threshold, or a `--quarantine` for a flaky
   measurement, is justified by the trend — quote the p-stat and the run count, never a
   single sample.

## Reading a regression

- **A real regression** shows up across runs in the trend store, on a stable stat (p95),
  not just one noisy sample. Attribute it: which stage grew — generation, bulk insert, or
  persistence? The manifest's per-stage timings localise it.
- **Noise** is a single run above baseline with the series flat. Don't gate on it;
  `--quarantine` the measurement if it is structurally flaky, and say so.
- **A route change** (e.g. an entity fell off `SqlBulkCopy` back to row-by-row) is a
  common, fixable cause — visible in the manifest's bulk-route field.

## Definition of done

A recommendation naming: the stage, the stat and run-count of evidence, and the concrete
change (gate threshold / tune result / quarantine) with the number it is based on. Never
propose a gate from one sample.

Depth: <https://chrisw000.github.io/test-data-manager/guides/performance-testing/> and
[profiling production shapes](https://chrisw000.github.io/test-data-manager/guides/profiling-production-shapes/).
