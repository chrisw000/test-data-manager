# TDM Wave 1 — Adoption & CI Enablement: Implementation Handoff

**Status:** Design proposed, ready for review
**Audience:** Implementing engineer / AI pair
**Owner:** Chris (Engineering Manager)
**Date:** 2026-07-13
**Depends on:** v1 baseline ([`tdm-handoff.md`](../tdm-handoff.md)); roadmap context in [`next_steps.md`](next_steps.md)

---

## 1. Purpose

Make the TDM adoptable by any team in an afternoon: installable tooling, real NuGet plugin
acquisition, first-class CI integration with PR-native output, and documentation that scales
beyond one team. No new seeding semantics in this wave — everything is distribution,
integration and onboarding around the existing engine.

**Non-goals (this wave):** policy enforcement, audit hardening, run registry (Wave 2);
performance work (Wave 3); HTML reporting and IDE tooling (Wave 4).

---

## 2. Deliverables

### 2.1 Packaging & release pipeline (Decision W1-D1)

- Publish NuGet packages: `Tdm.Identity`, `Tdm.Core`, `Tdm.EfCore`, `Tdm.Plugins`,
  `Tdm.Observability`, plus the host as **`dotnet tool` `tdm`** and a **container image**
  (`tdm:{version}`, based on `mcr.microsoft.com/dotnet/runtime`) for CI agents without a
  .NET toolchain.
- Release workflow (GitHub Actions): tag `v*` → build → full test suite → pack → push to the
  internal feed + container registry. Version stamped via `Directory.Build.props` from the
  tag; `AssemblyInformationalVersion` flows into the manifest's `tdmVersion` (already read).
- **Compatibility matrix** published in docs: TDM version × EF baseline (v1 pins EF 8.0.x).
  The plugin loader's fail-fast EF validation error message links to this matrix.
- SemVer discipline: `Tdm.Identity` is contract-frozen — any derivation change is a **major**
  bump everywhere (identity contract version is already recorded per manifest).

### 2.2 NuGet plugin acquirer (Decision W1-D2)

Implements the documented `IPluginAcquirer` extension point for real feed acquisition.

- `NuGetPluginAcquirer` using `NuGet.Protocol`: resolves `domains[].package` (+ optional
  `packageVersion`, floating allowed) from configured feeds, downloads the package and its
  transitive dependencies **excluding shared assemblies** (same prefix list as
  `PluginLoadContext.IsShared`), extracts `lib/{bestTfm}` into `./plugins/{domain}`.
- Settings additions:

```jsonc
{
  "plugins": {
    "acquisition": "NuGet",              // Folder (default, current behaviour) | NuGet
    "feeds": [ { "url": "https://nuget.acme.internal/v3/index.json" } ],  // auth via standard NuGet credential providers
    "cachePath": "~/.tdm/cache"
  },
  "domains": [ { "name": "Orders", "package": "Acme.Orders.Data.Persistence", "packageVersion": "3.2.*" } ]
}
```

- **Lockfile `tdm.plugins.lock.json`**: exact resolved package versions + content hashes,
  written on first resolve, honoured thereafter (`--update-plugins` to refresh). Resolved
  versions are recorded in the manifest run info — a run is reproducible down to the plugin
  version.
- Feed auth: standard NuGet credential provider chain (env vars, `nuget.config`,
  Azure Artifacts credential provider). TDM implements **no custom secret handling**.

### 2.3 CI tasks & machine-readable outputs (Decisions W1-D3, W1-D4)

- **GitHub Action** (composite, in this repo under `.github/actions/tdm`) and an
  **Azure DevOps task**: inputs `command` (validate|run|teardown), `settings`, `policy`,
  `lifecycle`, `seed`; uploads the manifest as a build artifact; maps exit codes 0/1/2 to
  success / success-with-issues / failure.
- New host option `--report <format>=<path>` (repeatable):
  - **SARIF** (validate + run): each warning/unmatched step becomes a SARIF result with
    `physicalLocation` = feature file + line (line numbers already flow through
    `StepPlan.Line` into the manifest). PRs get inline annotations via the standard SARIF
    upload actions.
  - **JUnit XML** (run): scenario = `<testcase>` (classname = feature), `Failed` →
    `<failure>`, `CompletedWithWarnings` → passed with warnings in `<system-out>`,
    `Skipped` → `<skipped/>`. CI UIs render seeding runs as test results.
  - Emitters live in `Tdm.Observability` (pure functions over `RunManifest` — no engine
    changes needed).

### 2.4 Onboarding (Decisions W1-D5, W1-D6)

- **`tdm init`**: scaffolds `tdm.settings.json` (annotated), a starter feature exercising
  create/load, a `.gitignore` snippet, and a CI workflow snippet. `tdm init --domain X
  --package Y` pre-fills the domain block.
- **`tdm explain "<step text>"`**: parses a single step and prints every pipeline decision —
  grammar rule matched, entity resolution (domain, CLR type, natural key), faker source,
  persistence route, identity strategy and the GUID that would be derived. Reuses
  `StepGrammar` + `DescribeEntities`; persists nothing, no DB connection.
- **Docs site** under `/docs-site`, published to GitHub Pages on release:
  grammar reference (every verb with examples — source of truth: the `features/` examples,
  which CI executes so docs can't rot), configuration reference, convention-profile
  cookbook (modern/legacy/custom), plugin packaging guide for domain teams,
  troubleshooting (every warning message → cause → fix), and the decision log (D1–D14 +
  wave decisions).

---

## 3. Decisions log

| # | Decision | Rationale |
|---|---|---|
| W1-D1 | dotnet tool + container image as the two supported host distributions | Covers local dev and toolchain-less CI agents; no MSI/installer maintenance |
| W1-D2 | NuGet.Protocol acquirer with lockfile; auth via standard NuGet credential providers | Reproducible runs; zero custom secret handling |
| W1-D3 | SARIF for findings, JUnit for run results — no custom CI formats | Native rendering in GitHub/AzDO without bespoke UI work |
| W1-D4 | Report emitters are pure functions over RunManifest in Tdm.Observability | Manifest stays the single source of truth; engine untouched |
| W1-D5 | Docs examples are the executable `features/` files run by CI | Documentation cannot drift from behaviour |
| W1-D6 | `tdm explain` reuses the resolution pipeline verbatim (no parallel implementation) | One resolution behaviour; explain output is guaranteed truthful |

## 4. Phases

1. **W1-P1 — Packaging:** csproj pack metadata, tool manifest, container image, release
   workflow, compatibility matrix page.
2. **W1-P2 — NuGet acquirer:** acquirer + lockfile + manifest recording; integration test
   against a local folder-based NuGet feed fixture.
3. **W1-P3 — CI surface:** `--report` emitters (SARIF, JUnit) + GitHub Action + AzDO task;
   dogfood on this repo's own pipeline.
4. **W1-P4 — Onboarding:** `tdm init`, `tdm explain`, docs site scaffold + first-pass content.

Each phase lands with tests (emitters get golden-file tests; acquirer gets a feed fixture).

## 5. Acceptance criteria

- A new team goes from nothing to a green `tdm validate` in CI in under 30 minutes using
  only the docs site.
- `tdm run` in a pipeline restores plugins from the feed, honours the lockfile, uploads a
  manifest artifact, and annotates the PR with any validate findings.
- Two consecutive CI runs with an unchanged lockfile record identical plugin versions in
  their manifests.

## 6. Risks & mitigations

| Risk | Mitigation |
|---|---|
| Transitive dependency resolution pulls a shared-prefix assembly variant | Exclusion uses the same list as `PluginLoadContext.IsShared` — single source of truth; test asserts parity |
| SARIF line numbers wrong for Scenario Outline expansions | Line numbers come from the original AST steps (already preserved through expansion); golden tests per outline |
| Feed auth differs per org (GitHub/AzDO/Artifactory) | Standard credential provider chain only; document the three common setups |
| Docs rot | Executable examples (W1-D5) + docs build in CI |

## 7. Open items

- Whether the AzDO task ships in-repo or in a separate extension repo (marketplace publishing
  needs its own pipeline).
- CTRF JSON as a third report format — cheap to add, decide on demand.
- `tdm init` interactive vs flags-only (proposed: flags-only first, interactive later).
