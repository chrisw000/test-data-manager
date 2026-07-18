# W5-P4 — Performance & data-shape guides

**Parent:** [wave-5-handoff.md](../wave-5-handoff.md)
**Depends on:** P1. Ships the distribution playground (W5-D5).

## 1. `guides/performance-testing.md` — Performance testing & tracking

Frame: *seed at scale, measure honestly, gate regressions.*

- Volume: count-bulk grammar, chunking (`bulkChunkSize`), provider-native bulk paths,
  `tdm bench tune` to measure the right chunk size against *your* database.
- Manifest at scale: sampling modes (`manifestBulkValues`), value hashes, what teardown
  can and cannot remove afterwards.
- Parallelism: `maxParallelScenarios`, per-domain caps, when serial is correct.
- Resilience: the journal, `--resume` after a mid-run kill.
- Measurement: per-verb/per-entity benchmark stats in the manifest; `--benchmark` deep
  timings; reading p50/p95 in the HTML report.
- Tracking: publish to the trend store; rolling-baseline `bench compare`; perf gates in
  `tdm.policy.json`; `--quarantine` for noisy agents; sparklines in the report;
  the Grafana pack for OTEL metrics.
- End-to-end walkthrough: a perf pipeline from seed → measure → compare → publish, as one
  copy-paste workflow (snippet-verified).

## 2. `guides/statistical-generation.md` — Realistic, deterministic synthetic data

Frame: *production-shaped data with test-grade determinism.*

- Why shape matters for perf tests (index selectivity, hot partitions, cache behaviour) —
  the motivating page for the playground.
- **Distribution playground** (interactive): weights editor + lognormal/normal/uniform/
  exponential sliders rendering a live histogram; a "same seed" toggle that replays the
  identical sequence — determinism made visible. Parity: median-equals-mean convention
  for lognormal; the histogram math mirrors `Distributions.cs` (documented as a mirror,
  not the same RNG).
- Config walkthrough: weights, distributions, clamps/rounding; correlated datasets
  (city↔postcode) and per-domain locale; the layering rule (faker < plugins <
  distributions < step overrides).
- Code extension: `IValueGeneratorPlugin` with a worked SKU example.
- Verification patterns: asserting proportions in tests (the 10k/2% acceptance test as
  the worked example), fakerSource markers and attestation.

## 3. `guides/profiling-production-shapes.md` — The `tdm profile` spike, safely

Frame: *shapes from production, never rows — and the paperwork that keeps it that way.*

- What it captures vs refuses (the table from the design doc, user-voiced).
- The workflow: replica → `tdm profile` → review the stats pack in PR → merge fragment →
  declare `statsPacks` → attribution in every manifest.
- The risk conversation to have with your DPO before pointing it at anything real; the
  GA gate status (spike, not GA), linked to the engineering record.

## Acceptance

- Playground renders identically in light/dark, no external requests; its lognormal
  median matches the `mean` input across the slider range (spot-assert in a docs test or
  documented vectors).
- The perf pipeline walkthrough runs green in `docs-verify` (bounded sizes: 5k rows).
- Profiling guide's every command runs against the demo databases in `docs-verify`.
