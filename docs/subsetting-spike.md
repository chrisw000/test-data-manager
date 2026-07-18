# Masking/subsetting design spike (W4-D8) — `tdm profile`

**Status: spike, not GA.** This document is the phase deliverable: the design, the
prototype's scope and limits, and the risk review. Direct row subsetting/masking remains
out of scope, and any GA decision is **gated on data-protection sign-off** (see §Risk
review). TDM's default promise — *no production data, synthetic by construction* — is
unchanged; this spike explores the one sanctioned crack in that wall: production *shapes*,
never production *rows*.

## The idea

The W4-P3 statistical layer made generated data configurable
(`entities.{X}.properties` — distributions, weights, correlated datasets). Its declared
risk (§6): *"distribution config drifts from real production shape."* This spike closes
that loop from the safe side: instead of copying production rows into test environments,
`tdm profile` connects **read-only** to a production-like source and emits a
**statistics pack** — per-column distributions, cardinalities and correlation hints —
which the existing distribution config consumes.

```bash
tdm profile --settings tdm.settings.json --domain Orders \
    --sample 10000 --out tdm.stats.json --fragment stats-fragment.json
```

- `tdm.stats.json` — the statistics pack (shape data, auditable, reviewable).
- `stats-fragment.json` — a ready-to-merge `entities.{X}.properties` fragment (the same
  shape a seed pack fragment uses, W4-D7): lognormal/normal fits for numeric columns,
  weights for low-cardinality categoricals. Synthetic-but-realistic, no data copied.

## What the prototype captures — and refuses to

| Column kind | Captured | Never captured |
|---|---|---|
| Numeric | count, nulls, distinct, min/max/mean/median/stddev + a skew-aware fit (lognormal for non-negative right-skewed, else normal) | individual values |
| Low-cardinality categorical (≤ `--categorical-max`, default 10) | value → relative frequency (**the only literals in a pack**) | anything above the threshold |
| High-cardinality string / free text | cardinality + null count only | values |
| Keys, foreign keys, `Guid`, `DateTime`, blobs | nothing / cardinality only | values — ids are identity, timestamps are recency, both row-level |
| Correlation | column-pair hints (near-functional dependency between categoricals) — *names* only, candidates for a W4-D5 dataset | the correlated tuples themselves |

`--no-values` suppresses category labels too (cardinalities and numeric shapes only) for
sources where even enum-like labels are sensitive. Sampling is bounded (`--sample`,
default 10k rows/entity) and the only SQL issued is per-table bounded reads with
no-tracking; run it against a **replica**, never the primary.

## Audit posture (W2-D1 stays truthful)

- Teams that adopt a profile-derived fragment declare the pack in settings:
  `"statsPacks": ["./tdm.stats.json"]`. Every run manifest then records
  `attribution.statsPacks: ["tdm.stats.json:<sha256-12>"]` — production-derived shapes are
  visible in the audit trail, and the HTML report (W4-D1) surfaces them in the run header.
- The attestation remains `syntheticOnly: true`: generated values come from seeded
  draws over declared shapes, not from rows. The `Distribution` source marker (W4-P3)
  already shows *that* distributions ran; `statsPacks` shows *where the shapes came from*.
- The stats pack itself is a reviewable JSON artifact — it can (and should) go through
  code review before entering a repo, exactly like a seed pack.

## Risk review

| Risk | Assessment | Mitigation in the prototype |
|---|---|---|
| Category labels leak sensitive values (e.g. a low-cardinality `Diagnosis` column) | **Real — the principal residual risk.** Cardinality alone does not imply insensitivity | Threshold is conservative (10) and configurable down; `--no-values` removes labels entirely; packs are reviewable JSON; **human review of every pack before commit is mandatory** |
| Small-population inference (a weight of 0.001 over a known row count narrows to individuals) | Real for rare categories | Weights are rounded (4 dp) and sample-bounded; document that packs from small tables need review; GA would add k-anonymity-style suppression of rare categories (see §GA gate) |
| Numeric extremes identify individuals (max salary = the CEO) | Real — min/max are values | Spike accepts and *documents* it: min/max feed clamps. GA would offer percentile clamps (p1/p99) instead of true extremes |
| Profiling load on production | Bounded reads, but still reads | Replica-only guidance; bounded `--sample`; no joins, no ORDER BY |
| Pack mistaken for synthetic config later | Provenance loss | `statsPacks` attribution + content hash in every consuming run's manifest |
| Scope creep toward row copying | The commercial pull is real | Hard scope line in this doc; the profiler API returns aggregates only — there is no code path that emits a row |

## GA gate

GA requires, in order: (1) **data-protection/DPO sign-off** on the capture rules above,
per organisation; (2) rare-category suppression (drop categories below a configurable
support count) and percentile clamps for numeric extremes; (3) an allowlist mode
(profile only named entities/columns) for regulated schemas; (4) documented retention
guidance for packs (they are derived data under GDPR). Until then, `tdm profile` is a
prototype for use against non-production or already-approved sources.

## Explicitly out of scope

Row subsetting, row masking/pseudonymisation, and any transfer of row values between
environments. Teams needing referential production rows should look at the external
reference + projection mechanisms (§8.5) — identity without data movement.
