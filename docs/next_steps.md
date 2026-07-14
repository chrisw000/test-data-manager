# TDM — Next Steps: from v1 to an at-scale, multi-team product

**Status:** Proposed roadmap
**Owner:** Chris (Engineering Manager)
**Date:** 2026-07-13
**Baseline:** v1 (all handoff phases P1–P4 implemented, see [`tdm-handoff.md`](../tdm-handoff.md))

v1 already provides the three foundations a commercial seeder needs, and most of this
roadmap builds on them rather than on new core machinery:

- the **manifest** — the reproducibility and audit artifact,
- the **identity contract** (`Tdm.Identity`) — cross-team agreement without coordination,
- **`tdm validate`** — a persistence-free hook where governance can live.

Every point below is tagged with the wave(s) it feeds into — e.g. **[1]** for Wave 1.
Full handoffs: [Wave 1](wave-1-handoff.md) · [Wave 2](wave-2-handoff.md) ·
[Wave 3](wave-3-handoff.md) · [Wave 4](wave-4-handoff.md)

| Wave | Theme | Why this order |
|---|---|---|
| 1 | Adoption & CI enablement | Nothing else matters if teams can't adopt it in an afternoon |
| 2 | Trust: audit, policy, registry | Required before shared-environment usage |
| 3 | Scale & performance | Needed once Wave 1 succeeds and usage grows |
| 4 | Product depth | Commercial differentiation |

---

## 1. Distribution & onboarding — the adoption blockers

- **Package and version it properly**: publish `Tdm.Identity`, `Tdm.Core`, `Tdm.EfCore` to
  the internal feed, and ship the host as a `dotnet tool` (`dotnet tool install tdm`) plus a
  container image for CI agents. Semantic versioning with an EF-baseline compatibility matrix
  (the handoff's version-skew risk becomes a documented support policy). **[1]**
- **Finish the NuGet plugin acquirer** — the `IPluginAcquirer` extension point exists, but
  at-scale usage means `tdm run` restores `Acme.Orders.Data.Persistence 3.2.1` from the feed
  itself, with a lockfile so runs are reproducible down to the plugin version (recorded in
  the manifest). **[1]**
- **`tdm init` scaffolding**: generates a settings file, a starter feature, and CI snippets,
  so a team's first seed is a 10-minute exercise, not a wiki crawl. **[1]**

## 2. CI/CD tooling

- **First-class pipeline tasks** (GitHub Action + Azure DevOps task) wrapping
  `validate`/`run`/`teardown` — exit codes 0/1/2 already map cleanly to pass/warn/fail. **[1]**
- **PR-native output**: emit `validate` findings as **SARIF** so unmatched steps and
  resolution failures annotate the feature file diff inline; emit run results as
  **JUnit/CTRF** so scenarios render as test results in CI UIs. **[1]**
- **`tdm replay --manifest`** — re-execute exactly what a manifest records (values, not
  fakers), turning the manifest into true playback. **[2]**
- **`tdm verify --manifest`** — re-run only the Load assertions to detect environment drift
  since seeding. **[2]**
- **Resume/checkpointing** for large runs: the manifest already records per-entity outcomes;
  `--resume` skips what's persisted. **[3]**

## 3. Reporting

- **HTML living-doc report** generated from the manifest: scenario outcomes, applied
  overrides, reference lineage graphs (who points at whom, resolved from where), benchmark
  tables. A static artifact every CI run publishes. **[4]**
- **Central manifest store + trends**: push manifests to blob storage with an index;
  dashboards for created-entity volumes and benchmark p50/p95 over time. **[3]**
- **Performance regression gates** — fail the pipeline if `create:Order p95` regresses beyond
  a threshold, making the benchmark surface actionable rather than informational. **[3]**
- Ship a **default Grafana dashboard + alerts** for the existing OTEL metrics so teams don't
  each rebuild one. **[3]**

## 4. Audit & compliance

- **Attribution in the manifest**: runner identity (CI job/user), git SHA of the feature
  files, settings-file hash, plugin package versions. **[2]**
- **Sign the manifest** (checksum + signature) so it's tamper-evident audit evidence, with
  retention policy guidance. **[2]**
- **A run registry service** (small API): every run registers start/finish, environment, and
  manifest location — cross-team visibility of "who seeded what where", plus **environment
  locks** so two teams can't concurrently seed the same shared database. **[2]**
- **Synthetic-data attestation**: classify all generators as synthetic and emit a per-run
  statement that no production data was used — the thing auditors actually ask for. **[2]**

## 5. Policy as code

The highest-leverage multi-team feature; `validate` is the natural enforcement point.
*(First instance shipped: the write-repository policy gate —
[ADR-0001](adr-0001-data-access-via-repositories.md).)*
A `tdm.policy.json` (or OPA/Rego for orgs already invested) evaluated before any persistence:

- **Environment rules**: no `Persistent` lifecycle against shared environments without an
  approval tag; `FailRun` mandatory in prod-like; connection strings must come from vault,
  never inline. **[2]**
- **Volume rules**: max bulk counts per entity/environment. **[2]** (thresholds re-used by the
  Wave 3 perf gates **[3]**)
- **Grammar/hygiene rules**: `@seed` required, banned entities, naming conventions for
  features. **[2]**
- **Identity-contract governance** — the big one: a versioned **registry of natural keys per
  entity, owned by each domain team**. The v1 handoff accepts that natural keys are "in
  effect part of the identity contract"; make that enforceable — CI fails if Billing
  references `Customer "Acme Ltd"` from Orders and Orders' registry doesn't declare it. This
  turns an informal team agreement into a checked contract. **[2]**

## 6. Data capabilities (commercial parity)

- **More providers**: PostgreSQL first (cheap — the provider bootstrap is one switch), behind
  a provider plugin interface so further providers don't touch `Tdm.EfCore`. **[3]**
- **API-based seeding** for domains that forbid direct DB writes — persist through their
  public API instead of the DbContext, which also exercises validation for free. **[4]**
- **Shared seed packs**: versioned, reusable feature-file packages ("EU reference customers
  v2") so teams stop copy-pasting base data. **[4]**
- **Richer generation**: weighted distributions, correlated fields (city↔postcode), locale
  packs, custom generator plugins beyond the heuristic table. **[4]**
- **Scale path**: parallel scenario execution across domains, SqlBulkCopy/COPY for bulk
  verbs, streaming generation for multi-million-row load tests. **[3]**
- **Masking/subsetting from production** — explicitly a v1 non-goal; keep it last. It has a
  different risk profile (touching real data), and half the value of TDM is *not* needing
  production data. Design spike only in Wave 4. **[4]**

## 7. Developer experience & documentation

- **Docs site** (grammar reference, convention-profile cookbook, troubleshooting, decision
  log) — the README scales to one team, not twenty. **[1]**
- **Editor support**: a VS Code extension / language server for the TDM grammar that
  autocompletes entity and property names from `list-entities` output and lints steps as you
  type — kills the #1 friction (typos becoming unmatched steps at runtime). **[4]**
- **`tdm explain "<step>"`**: dry-run a single step and print every resolution decision
  (entity → repo → faker → route → identity) — `list-entities` at step granularity. **[1]**
- **Secrets providers** (Key Vault / AWS Secrets Manager) beyond today's env vars. **[2]**
- **SBOM + signed releases** for the security review a platform tool will face. **[2]**
