---
name: tdm-run-triage
description: >-
  Diagnose a failed or wrong TDM seeding run from its manifest, journal and SARIF. Use when
  told "the seeding run failed", "tdm run exited non-zero", "the wrong data got seeded", or
  a CI seed step is red. Distinguishes config, data and environment causes.
---

# Triaging a TDM run

Diagnose from artifacts, not stdout. A run leaves a **manifest** (`output/*.tdm.json`), a
crash-safe **journal** (`*.tdm.journal.jsonl`), and — if requested — **SARIF**. Ground
truth is in the files.

## Loop

1. **Classify by exit code.** `2` = failed (grammar, unresolved entity, policy refusal,
   config error) — usually a *config* fault, caught before/at persistence. `1` = completed
   with warnings — data landed but something is off. `0` but "wrong data" — a semantic
   issue, go to the manifest values.
2. **Open the manifest.** Read, in order:
   - `unmatchedSteps` — a step no grammar rule caught (silent no-op). A prime cause of
     "green but missing rows."
   - per-scenario `warnings` — resolution fell back (generated a natural key, skipped a
     property, synthesised a reference).
   - the failing entity's `values` / `persistedVia` / `idStrategy` — did it persist the
     way you expected, with the id you expected?
   - `lineage` — which step produced which row and how references resolved; follow it to
     the first row that is wrong.
3. **Check the journal** for a crash mid-run: the last recorded scenario/row tells you how
   far it got. A partial run can be resumed (`tdm run --resume <journal>`) once the cause
   is fixed — the plan and seeds must match.
4. **Check policy** if exit `2` names a refusal: a write-repository policy violation, an
   environment policy under `--env`, or a **key-registry** violation (a cross-team id
   contract — never force past it; fix the key, don't bypass).
5. **Build a minimal repro** with `tdm explain "<the offending step>"` — it shows the
   rule, entity, route and id in isolation, no database. This separates a wording problem
   from a data/environment problem fast.

## Cause taxonomy

| Signal | Likely cause | Fix |
|---|---|---|
| Unresolved entity / unknown property / unmatched step | **Config/authoring** | correct the step or `tdm.settings.json`; re-`explain` |
| Natural-key collision, duplicate row, constraint violation | **Data** | make the step idempotent (delete-first) or fix the key |
| Write-repository / `--env` policy / key-registry refusal | **Policy** | satisfy the policy; escalate key-registry, never bypass |
| Connection/timeout/auth failure, schema missing | **Environment** | target/secret/`ensureCreated`/migrations — not the feature |

## Definition of done

State the **cause class** (config / data / policy / environment), the specific evidence
from the manifest/SARIF (quote the field), and the minimal fix. If the fix is a feature
change, verify it with `tdm validate` before re-running.

Depth: <https://chrisw000.github.io/test-data-manager/reference/reports-and-manifest/>
and [troubleshooting](https://chrisw000.github.io/test-data-manager/reference/troubleshooting/).
