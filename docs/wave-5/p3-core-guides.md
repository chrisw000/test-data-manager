# W5-P3 — Core usage guides

**Parent:** [wave-5-handoff.md](../wave-5-handoff.md)
**Depends on:** P1 (IA, snippet harness, footer/tour conventions).

Five guides. Every command is a harness snippet; every guide ends with "Where next" and
tour metadata.

## 1. `guides/daily-use-qa.md` — Daily use for QAs

Frame: *you think in test cases; TDM makes the test's data part of the test.*

- The verb cookbook by intent: "I need a customer that…" → create with overrides; "…that
  belongs to…" → references; "…lots of…" → count/table bulk; "prove it worked" → verify
  steps; relative dates (`today-3d`).
- Scenario design patterns: Background for base data; Scenario Outline for tiers/variants;
  tags (`@seed:`, `@skip`, `@persistent`/`@ephemeral`) and when each is right.
- The feedback loop: editor squiggles → `tdm explain` → `tdm validate` — never guess.
- Reading a failed run: console summary → manifest → warnings → unmatched steps.
- Determinism etiquette: when to pin seeds, why overriding beats hoping.

## 2. `guides/daily-use-dev.md` — Daily use for developers

Frame: *seed your own domain's database through its own model.*

- What convention resolution found for *your* domain: `tdm list-entities` as the daily
  mirror; fixing resolution warnings (fakers, repositories, natural keys).
- Writing a `{Name}Faker`; when the auto-faker is enough; the statistical layer as the
  no-code alternative; `IValueGeneratorPlugin` for the last 5%.
- Local loops: `run` against a dev SQLite/container DB; `teardown --manifest`; lifecycles
  during development.
- Entity config (`entities.{X}`): natural keys, id strategy, projections, requireRepository
  exemptions (ADR-0001, plain-language version).
- Manifest as a debugging tool: values, overrides applied, persistence routes.

## 3. `guides/editor-setup.md` — VS Code and any LSP editor

- VS Code: install, `tdm export-model`, what each squiggle/completion/hover means, the
  staleness banner, coexisting with Reqnroll/Cucumber extensions.
- Any LSP editor (Rider, Neovim, Helix): `tdm lsp --model tdm.model.json` stdio wiring,
  with copy-paste configs for each.
- Keeping the model honest: the CI drift check (`export-model` + `git diff --exit-code`).

## 4. `guides/ci.md` — CI: validate, report, gate

Frame: *the golden path a platform team stamps out.*

- The three CI jobs and what each catches: **validate** (grammar, resolution, policy — no
  database), **model drift** (editor schema), **run** (real seeding where CI has a DB).
- The GitHub composite action end-to-end: inputs, exit-code mapping, SARIF → PR
  annotations, JUnit → test tab, manifest + HTML report artifacts. Azure DevOps
  equivalent shown as raw CLI steps.
- Policy as code in PRs: `tdm.policy.json` + key registry violations annotating the diff.
- Copy-paste starter workflows: PR gate; nightly seed+verify; the report as build artifact.
- Mermaid flow: PR → validate/drift → merge → environment runs.

## 5. `guides/cd-environments.md` — CD & environments

Frame: *running seeding safely where other people's data lives.*

- The environment model: `--env` + `tdm.policy.json` (allowed lifecycles, bulk caps,
  connection-source rules); approval tokens for sanctioned exceptions — and how the
  override lands in the manifest audit trail.
- Shared-environment coordination: run registry + environment locks; heartbeats; what a
  lock conflict looks like and how to read who holds it.
- Secrets: the chain (inline → environment → cloud adapter), connection strings by name,
  signing cert passwords, registry API keys — TDM never stores secrets.
- Post-deploy verification: `tdm verify` for drift, `tdm replay` for reconstruction,
  `teardown` discipline, resumable runs (`--resume`) after interrupted deployments.
- Manifest custody: checksums, optional signing, `tdm manifest verify` in the pipeline;
  publishing to the trend store as the run's flight record.
- Mermaid flow: deploy → lease locks → seed → publish manifest/report → verify → release locks.

## Acceptance

- All five guides live under `Guides/` with snippets executed by `docs-verify`.
- The QA and dev guides share zero pages but cross-link at every hand-off point
  (e.g. "ask your domain owner to add a faker → daily-use-dev#fakers").
- CI guide's starter workflows lint (actionlint or equivalent) and mirror what this repo's
  own `ci.yml` does — the repo is the reference implementation.
