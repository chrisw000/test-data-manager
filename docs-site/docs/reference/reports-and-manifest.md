# Reports & the manifest

Every run — including `validate` dry runs — writes **the manifest**: a JSON file
recording full final values, seeds, persistence routes and ids. It is the
reproducibility and audit artifact everything else reads: `tdm replay` re-creates its
rows, `tdm verify` drift-checks it, `tdm teardown` reverses it, `tdm report` renders it,
`tdm publish` stores it for trends.

Files land in `run.outputPath` (default `./output`):

```text
sample-domains-demo-20260717-204321.tdm.json          ← the manifest
sample-domains-demo-20260717-204321.tdm.json.sha256   ← checksum (always)
sample-domains-demo-20260717-204321.tdm.json.sig      ← detached signature (if signing configured)
sample-domains-demo-20260717-204321.tdm.journal.jsonl ← crash-safe journal (tdm run --resume)
sample-domains-demo-20260717-204321.html              ← HTML report (tdm report / --report html=…)
```

## Manifest schema tour

Top level: `run`, `scenarios[]`, `teardown`.

### `run` — the run header

```jsonc
"run": {
  "name": "sample-domains-demo",
  "startedUtc": "2026-07-17T20:23:39Z",
  "durationMs": 22.3,
  "failurePolicy": "BestEffort",
  "lifecycle": "Persistent",
  "tdmVersion": "0.1.0+b59e867e…",           // + bogusVersion, efVersion
  "identityContractVersion": "1",
  "dryRun": false,                            // true for validate
  "outcome": "Succeeded",                     // Succeeded | CompletedWithWarnings | Failed
  "pluginPackages": {},                       // resolved plugin versions (NuGet acquisition)
  "seedPacks": {},                            // applied seed packs + versions
  "benchmark": {},                            // run-level operation stats when enabled
  "attribution": {
    "runnerId": "local:chris",                // or the CI runner identity
    "hostname": "…",
    "gitSha": "…", "gitDirty": true,
    "settingsFileSha256": "…",
    "resumedFrom": null,                      // journal path when --resume was used
    "statsPacks": []                          // statistics packs the workspace declares
  },
  "attestation": { "syntheticOnly": true, "sources": [] },
  "environment": null,                        // --env value, if given
  "policyViolations": [],                     // rule + message when a run was refused
  "policyOverrideApplied": false,             // true when --approval overrode them
  "registryRunId": null                       // run registry id, when configured
}
```

The run's **exit code** derives from `outcome`: `0` Succeeded, `1`
CompletedWithWarnings, `2` Failed. `attestation.syntheticOnly` stays `true` unless the
workspace declares production-derived statistics packs — the manifest is honest about
where shapes came from.

### `scenarios[]` — one entry per scenario, in plan order

```jsonc
{
  "feature": "Orders regression seed",
  "featureFile": "…/features/orders-seeding.feature",
  "scenario": "Customer places an order",
  "line": 8,
  "seed": 42,                                 // the effective deterministic seed
  "tags": ["@seed:42"],
  "lifecycle": "Persistent",
  "entities": [ /* every row created/updated/deleted — see below */ ],
  "bulkOperations": [],                       // count-bulk creates (sampled per run.manifestBulkValues)
  "references": [],                           // resolved references incl. cross-domain ids
  "unmatchedSteps": [],                       // steps the grammar didn't match (warnings)
  "warnings": [],
  "outcome": "Succeeded",                     // Succeeded | CompletedWithWarnings | Failed | Skipped
  "benchmark": {},                            // per-operation stats when enabled
  "teardown": null                            // per-scenario teardown record (@ephemeral)
}
```

Each entity entry is the full evidence for one row:

```jsonc
{
  "ordinal": 2,
  "entity": "Order",
  "verb": "Create",
  "domain": "Orders",
  "persistedVia": "IOrderRepository.AddOrder",
  "id": "a8eae15f-913e-5e14-b95a-735a8c3fc9c5",
  "idStrategy": "Deterministic",              // the identity contract at work
  "naturalKey": "ORD-1001",
  "fakerSource": "auto",                      // which faker generated the base values
  "values": { "Id": "…", "OrderNumber": "ORD-1001", "Status": "Pending", "Total": "199.99", "…": "…" },
  "overridesApplied": ["OrderNumber", "Status", "Total"],
  "warnings": [],
  "durationMs": 24.7
}
```

`values` holds **final** values — what actually went to the database — which is what
makes `tdm replay` exact and `tdm verify` meaningful. For count-bulk creates,
`run.manifestBulkValues` controls detail: `All`, `Sample` (head/tail rows + count +
value hash — manifests stay usable at a million rows), or `None`.

## Checksum & signing

A SHA-256 checksum is always written next to the manifest. Configure `run.signing`
(certificate + password env var) and each manifest also gets a detached signature.
Verify either way:

```bash
tdm manifest verify output/run.tdm.json --cert tdm-signing.cer
# exit 0 = fully verified · 1 = signature present but no --cert given · 2 = tampered
```

## Report formats

`--report <format>=<path>` on `run`, `validate` and `bench compare` (repeatable), plus
the standalone `tdm report` command:

| Format | Consumer | What it carries |
|---|---|---|
| `sarif` | GitHub code scanning → inline PR annotations | validation findings, policy violations, located at the feature file/line |
| `junit` | any CI test UI | one test case per scenario, failures with messages |
| `html` | humans | the full manifest as a single self-contained page: run header, scenario drill-down, reference lineage graph, benchmark charts, trend sparklines (with `--store`) |

The HTML report opens from `file://` with zero network requests — no server, no CDN, no
external assets. Attach it to a CI run and it works forever.

!!! info "Engineering record"
    Design docs:
    [living-report.md](https://github.com/chrisw000/test-data-manager/blob/main/docs/living-report.md),
    [audit-and-signing.md](https://github.com/chrisw000/test-data-manager/blob/main/docs/audit-and-signing.md),
    [resume-and-trends.md](https://github.com/chrisw000/test-data-manager/blob/main/docs/resume-and-trends.md).

## Where next

- [CLI reference](cli.md) — every command that reads or writes these artifacts.
- [Getting started](../start/getting-started.md) produces a manifest and report you can
  open right now.
- CI wiring for SARIF/JUnit → the [CI guide](../guides/ci.md).
