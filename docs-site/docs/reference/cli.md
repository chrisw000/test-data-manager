# CLI reference

Every `tdm` command, its options and exit codes. This page cannot drift: a docs-CI
snippet compares the commands documented here against the live `tdm --help` output on
every push, and fails the build on any mismatch.

Common conventions:

- `-s, --settings <path>` defaults to `tdm.settings.json` everywhere it appears.
- **Exit codes** unless stated otherwise: `0` succeeded, `1` completed with warnings,
  `2` failed (including policy refusals and configuration errors).
- `--report <format>=<path>` is repeatable; formats: `sarif`, `junit`, `html`.
- `--env <name>` switches on environment-policy enforcement from `--policy-file`
  (default `tdm.policy.json`); `--approval <token>` overrides violations where the
  environment allows it.

## `tdm run`

Parse feature files, seed data, write the run manifest (and a crash-safe
`.tdm.journal.jsonl`).

| Option | Meaning |
|---|---|
| `-s, --settings <path>` | Path to `tdm.settings.json` |
| `--seed <n>` | Override `run.defaultSeed` |
| `--policy <BestEffort\|FailObject\|FailRun>` | Override `run.failurePolicy` |
| `--lifecycle <Persistent\|TrackedTeardown\|Transactional>` | Override `run.lifecycle` |
| `--benchmark` | Override `run.benchmark` |
| `--update-plugins` | Re-resolve plugin package versions, ignoring `tdm.plugins.lock.json` |
| `--report <format>=<path>` | Additionally emit the manifest as sarif / junit / html (repeatable) |
| `--env <name>` / `--policy-file <path>` / `--approval <token>` | Environment policy enforcement |
| `--resume <journal>` | Skip scenarios/rows a previous run's journal records as persisted; plan and seeds must match |

## `tdm validate`

Parse features and resolve entities/fakers/repositories; **persist nothing** (the CI dry
run). Takes the same options as `run` except seed/lifecycle/benchmark/resume — there is
nothing to seed.

| Option | Meaning |
|---|---|
| `-s, --settings <path>` | Path to `tdm.settings.json` |
| `--policy <BestEffort\|FailObject\|FailRun>` | Override `run.failurePolicy` |
| `--update-plugins` | Re-resolve plugin packages, ignoring the lockfile |
| `--report <format>=<path>` | sarif for PR annotations, junit for CI test UIs |
| `--env` / `--policy-file` / `--approval` | Environment policy enforcement |

## `tdm teardown`

Delete rows recorded in a manifest, in reverse dependency order.

| Option | Meaning |
|---|---|
| `--manifest <file>` *(required)* | Path to a `.tdm.json` manifest |
| `-s, --settings <path>` | Path to `tdm.settings.json` |

## `tdm list-entities`

Print the resolved entity/repository/faker map (convention debugging).

| Option | Meaning |
|---|---|
| `-s, --settings <path>` | Path to `tdm.settings.json` |
| `--domain <name>` | Restrict to one domain |

## `tdm init`

Scaffold `tdm.settings.json`, a starter feature, `.gitignore` and a CI validate
workflow. Existing files are never overwritten.

| Option | Meaning |
|---|---|
| `--domain <name>` | Domain name to pre-fill (default: `MyDomain`) |
| `--package <id>` | NuGet package id of the domain data assembly to pre-fill |
| `--dir <path>` | Target directory (default: `.`) |

## `tdm explain`

Explain every pipeline decision for a single step: grammar rule, entity resolution,
faker, persistence route, identity. No database connection.

```bash
tdm explain 'an Order exists for Customer "Acme Ltd" with status "Pending"'
```

| Option | Meaning |
|---|---|
| `<step>` *(argument)* | The step text |
| `-s, --settings <path>` | Path to `tdm.settings.json` |
| `--keyword <kw>` | Gherkin keyword context (default: `Given`) |

## `tdm manifest verify`

Verify a manifest's checksum and, if present, its detached signature.
**Exit codes:** `0` fully verified, `1` partially (signature present but `--cert` not
given), `2` failed/tampered.

| Option | Meaning |
|---|---|
| `<file>` *(argument)* | Path to a `.tdm.json` manifest |
| `--cert <path>` | Public certificate (`.cer`/`.pem`) to verify a detached signature |

## `tdm replay`

Re-create exactly the rows a manifest records — final values, not fakers. Idempotent;
only Persistent scenarios play back.

| Option | Meaning |
|---|---|
| `--manifest <file>` *(required)* | Path to a `.tdm.json` manifest |
| `-s, --settings <path>` | Path to `tdm.settings.json` |

## `tdm verify`

Drift check: assert every manifest-recorded row still exists with its recorded values.
**Exit codes:** `0` no drift, `1` drift. (File integrity is `tdm manifest verify`.)

| Option | Meaning |
|---|---|
| `--manifest <file>` *(required)* | Path to a `.tdm.json` manifest |
| `-s, --settings <path>` | Path to `tdm.settings.json` |

## `tdm bench tune`

Measure bulk-insert throughput across a matrix of chunk sizes against the target
database and write the best into `run.bulkChunkSize`. Inserts and then deletes `--rows`
rows per chunk size — point it at a dev database.

| Option | Meaning |
|---|---|
| `-s, --settings <path>` | Path to `tdm.settings.json` |
| `--domain <name>` | Restrict to one domain |
| `--entity <name>` | Entity to bulk-insert (default: first with a client-set single-column key) |
| `--rows <n>` | Rows inserted per measurement (default: 2000) |
| `--chunk-sizes <list>` | Comma-separated chunk sizes (default: `100,250,500,1000,2000`) |
| `--no-write` | Report the best chunk size without updating settings |

## `tdm bench compare`

Compare a run's benchmark stats against a baseline and evaluate the policy file's perf
gates. **Exit codes:** `0` every gate holds, `2` regression. Compare *before* publishing
the current manifest to the store.

| Option | Meaning |
|---|---|
| `--manifest <file>` *(required)* | The run to judge |
| `--baseline <file>` | Pinned baseline manifest (mutually exclusive with `--store`) |
| `--store <root>` | Trend store root — baseline is the per-stat rolling median of recent runs |
| `--baseline-runs <n>` | Runs in the rolling-median baseline (default: 5) |
| `--stat <name>` | `meanMs` \| `p50Ms` \| `p95Ms` (default) \| `maxMs` \| `totalMs` |
| `--quarantine` | Report gate failures without failing the pipeline |
| `--env` / `--policy-file` | Which environment's perf gates apply |
| `--report <format>=<path>` | Emit the comparison as sarif / junit / html |

## `tdm publish`

Push a manifest to the trend store under `{env}/{run-name}/{timestamp}`, maintaining
`index.json`. Baselines for `tdm bench compare` and report sparklines read from here.

| Option | Meaning |
|---|---|
| `--manifest <file>` *(required)* | Manifest to publish |
| `--store <root>` *(required)* | Trend store root: a directory — local, network share, or blob storage mounted/synced by CI |
| `--env <name>` | Environment folder (default: the manifest's environment, else `default`) |

## `tdm report`

Render a manifest as a single self-contained HTML file: run header, scenario
drill-down, reference lineage graph, benchmark charts and (with `--store`) trend
sparklines. No server, no external assets — opens from `file://`.

| Option | Meaning |
|---|---|
| `--manifest <file>` *(required)* | Manifest to render |
| `--html <path>` | Output path (default: next to the manifest, `.html` for `.tdm.json`) |
| `--store <root>` | Add trend sparklines from this run name + environment's history |
| `--trend-runs <n>` | Stored runs the sparklines cover (default: 10) |
| `--stat <name>` | Stat charted (default: `p95Ms`) |
| `--env <name>` | Environment whose history is charted |
| `--cert <path>` | Verify a detached signature and show the result in the header |

## `tdm export-model`

Serialise the resolved entity map (logical names, properties, natural keys, domains) to
`tdm.model.json` — the offline model `tdm lsp` validates against. Deterministic output:
regenerate in CI and fail on diff to catch staleness.

| Option | Meaning |
|---|---|
| `-s, --settings <path>` | Path to `tdm.settings.json` |
| `--out <path>` | Output path (default: `tdm.model.json`) |

## `tdm lsp`

Run the TDM language server on stdio: live StepGrammar diagnostics, entity/property
completion and verb hover for feature files, validated against `tdm.model.json` — no
database connection. Launched by editor clients (the VS Code extension in
`editors/vscode`), not interactively.

| Option | Meaning |
|---|---|
| `--model <path>` | Exported model file (default: `tdm.model.json`); reloaded automatically on change |

## `tdm profile`

**Spike (prototype, not GA):** connect read-only to a production-like source and emit a
statistics pack — per-column distributions, cardinalities, correlation hints, **never
row values**. Data-protection review required before use on real production data; see
the [profiling guide](../guides/profiling-production-shapes.md).

| Option | Meaning |
|---|---|
| `-s, --settings <path>` | Path to `tdm.settings.json` |
| `--domain <name>` | Restrict to one domain |
| `--sample <n>` | Rows sampled per entity, upper bound (default: 10000) |
| `--categorical-max <n>` | Columns with ≤ n distinct values are categorical — weights captured (default: 10) |
| `--no-values` | Suppress category labels entirely — cardinalities and numeric shapes only |
| `--out <path>` | Statistics pack output (default: `tdm.stats.json`) |
| `--fragment <path>` | Also emit an entities-config fragment with suggested distributions/weights |

## Where next

- [Getting started](../start/getting-started.md) exercises the core loop end to end.
- [Configuration reference](configuration.md) — everything the options above override.
- [Reports & the manifest](reports-and-manifest.md) — the artifacts these commands
  produce and consume.
