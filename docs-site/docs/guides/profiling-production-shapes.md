---
tour_prev: guides/statistical-generation.md
tour_next: guides/multi-domain-identity.md
---

# Profiling production shapes

**Persona:** platform / DevEx. The realistic distributions in the
[statistical generation guide](statistical-generation.md) are best derived *from*
production — but half of TDM's value is never needing production data. `tdm profile` is
the sanctioned route: it reads a production-like source and emits **shapes, never rows**.

!!! warning "Spike, not GA"
    `tdm profile` is a **prototype** (W4-D8), not a generally available feature. It only
    reads what a connection can see, but it is still pointed at real systems — treat it
    accordingly, and read the [risk conversation](#the-risk-conversation) before using it
    on anything real. Engineering record:
    [subsetting-spike.md](https://github.com/chrisw000/test-data-manager/blob/main/docs/subsetting-spike.md).

## What it captures — and refuses

| Captures | Refuses |
|---|---|
| Per-column distributions (mean, σ, min/max, shape) | Row values — ever |
| Cardinalities and null rates | Identifiers, free text |
| Categorical weights for low-cardinality columns (`--categorical-max`) | Category labels too, with `--no-values` |
| Correlation hints | Anything without a read-only connection |

The output is a **statistics pack** — a summary object, plus (with `--fragment`) an
`entities.{X}.properties` fragment you can paste straight into config. From the demo
catalogue this guide profiles, the fragment captures the category split as weights:

```jsonc
"Category": { "weights": { "Books": 0.6667, "Gadgets": 0.3333 } }
```

No SKU, no product name, no row — just the shape.

## The workflow

Point `--settings` at a **read replica** (never a primary), profile, review the pack in a
PR, then adopt it. Here it runs against an isolated, seeded demo database so the whole
flow is CI-verified:

```bash
--8<-- "profiling-production-shapes/01-profile.sh:profile"
```

1. **Replica → `tdm profile`** — emit `tdm.stats.json` (+ `tdm.fragment.json`).
2. **Review the pack in a PR** — it contains only shapes; a reviewer can confirm no
   values leaked (and `--no-values` removes category labels if even those are sensitive).
3. **Merge the fragment** into `entities.{X}.properties`
   ([statistical generation](statistical-generation.md)).
4. **Declare `statsPacks`** in `tdm.settings.json` — every run's attribution then records
   the pack (name + content hash), so the run's provenance is truthful about
   production-derived shapes:

```jsonc
"statsPacks": ["./tdm.stats.json"]
```

This keeps the synthetic-data **attestation** honest: a run using production-derived
shapes says so, in the manifest, by content hash.

## The risk conversation

Before pointing `tdm profile` at anything real, have this conversation with your data
protection officer / DPO:

- **What does the connection expose?** Profiling reads columns to summarise them. Even
  though only aggregates are written, the *process* reads real data — so the connection
  must be to a sanctioned replica, read-only, and scoped.
- **Could a shape leak a value?** A near-unique low-cardinality column can approximate
  identifying data. Use `--no-values` to drop category labels, and review packs before
  merge.
- **Where do packs live?** They are config artifacts committed to the repo — treat their
  review like any change touching production-derived information.
- **GA gate.** Because these questions are unsettled, profiling ships as a spike. Do not
  build load-bearing process on it until it graduates; the engineering record tracks the
  status.

## Where next

- [Statistical generation](statistical-generation.md) — where a profiled fragment lands.
- [CD & environments](cd-environments.md) — attestation and manifest custody.
- [CLI: `tdm profile`](../reference/cli.md#tdm-profile) — every option.

**Guided tour:** next stop → [Multi-domain identity alignment](multi-domain-identity.md)
