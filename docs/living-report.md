# The living-doc HTML report (W4-D1)

## `tdm report`

```bash
tdm report --manifest output/orders-seed-20260717-140302.tdm.json
tdm report --manifest output/current.tdm.json --html report.html \
    --store /mnt/tdm-trends --env ci --trend-runs 10 --stat p95Ms
```

Renders any run manifest as **one self-contained HTML file** — inline CSS, inline SVG, no
scripts, no external requests — so it works as a CI build artifact, an email attachment, or
a file share drop. No server, no portal (W4-D1). The default output path sits next to the
manifest (`.html` for `.tdm.json`).

The renderer is a pure function over `RunManifest`
(`Tdm.Observability.Reports.HtmlReport.Render`), the same posture as the W1-D3 SARIF/JUnit
emitters. It is also available inline on every run: `tdm run --report html=out/report.html`
(and the GitHub action's `report-html` input, which includes the file in the uploaded
manifest artifact).

## What the report shows

- **Run header** — outcome, environment, versions (TDM/Bogus/EF/identity contract),
  attribution (runner, host, git SHA/dirty, settings hash, resumed-from), plugin package
  versions, attestation, policy violations (with the override state), and the manifest
  file's **checksum/signature status**. `tdm report` verifies the side files the way
  `tdm manifest verify` does (pass `--cert` to verify a signature fully); the inline
  `--report html=…` emitter has no file yet, so it reports "not verified".
- **Scenario drill-down** — each scenario expands to its warnings, unmatched steps, bulk
  operation summaries (counts, sampling mode, value hash), entity rows, and per-entity
  **final values with applied overrides marked** — the manifest's audit detail, readable.
- **Reference lineage graph** — entities as nodes (bordered by domain), references as
  edges labelled with `resolvedFrom` (contextBag / database / identityContract). Reference
  targets merge onto the created entity that owns the identity — persisted id first (the
  identity contract makes ids equal across domains, so a Billing invoice's edge lands on
  the Orders customer), entity+natural-key as the fallback. Targets not created in this
  run render as dashed *external* nodes. Bulk creates collapse to one aggregate node
  ("500 × Product") using the W3-D4 bulk summaries, so the graph stays readable at volume.
- **Benchmark charts** — per verb and per verb:entity, p50/p95 bars from the manifest's
  benchmark stats.
- **Trend sparklines** (`--store`, W3-D7) — per-operation sparklines across the stored
  history of this run name + environment, with a Δ against the median of that history —
  the same rolling-baseline posture as `tdm bench compare` (W3-D8).

## Manifest addition

`references[].sourceOrdinal` records which scenario entity a reference was applied to —
the lineage edge's source. It is additive: older manifests still render, but their
references contribute target nodes only, no edges. External-reference declarations keep
`sourceOrdinal: null`; they publish an identity into the context bag rather than applying
it to an entity.

## CI

```yaml
- uses: ./.github/actions/tdm
  with:
    command: run
    report-html: output/report.html
```

The report lands in the `tdm-manifest` build artifact next to the manifest — the
"living documentation generated from real runs" entry point without hosting anything.
