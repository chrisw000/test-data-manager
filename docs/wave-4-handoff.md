# TDM Wave 4 — Product Depth: Implementation Handoff

**Status:** Design proposed, ready for review
**Audience:** Implementing engineer / AI pair
**Owner:** Chris (Engineering Manager)
**Date:** 2026-07-13
**Depends on:** Waves 1–3 (packaging, policy engine, provider seam, manifest store); v1 baseline

---

## 1. Purpose

The differentiation wave: the features that make the TDM feel like a commercial product
rather than an internal tool — living documentation generated from real runs, editor
tooling that removes the last grammar friction, richer statistical data generation,
seeding through public APIs for domains that forbid direct database writes, shared seed
packs, and a scoped exploration of production subsetting.

**Non-goals (this wave):** GA production-data masking/subsetting (design spike only, §2.6);
a hosted web UI/portal (revisit after this wave ships).

---

## 2. Deliverables

### 2.1 Living-doc HTML report (Decision W4-D1)

- **`tdm report --manifest <file> [--html out.html]`**: renders a **self-contained** HTML
  file (inlined assets, no server) from any manifest:
  - run header (attribution, versions, policy outcome, signature status),
  - scenario table with outcomes, seeds, warnings, drill-down to per-entity final values
    and applied overrides,
  - **reference lineage graph**: entities as nodes, references as edges labelled with
    `resolvedFrom` (contextBag / database / identityContract) — the cross-domain identity
    story becomes visible,
  - benchmark charts (per verb/entity, p50/p95) and, when a Wave 3 trend store is
    configured, sparklines against the baseline.
- Pure function over `RunManifest` in `Tdm.Observability` (same pattern as the Wave 1
  emitters); the Wave 1 CI tasks gain an option to publish it as a build artifact.

### 2.2 Editor support: language server + VS Code extension (Decisions W4-D2, W4-D3)

- **`tdm export-model --settings ... --out tdm.model.json`**: serialises the resolved
  entity map (logical names, properties + types, natural keys, domains, verbs) — produced
  the same way `list-entities` is, checked into the repo or generated in CI.
- **`tdm lsp`** — a language server (stdio) shipped inside the existing dotnet tool:
  - diagnostics: live `StepGrammar` parse of feature files (the *actual* parser — no
    reimplementation), unmatched steps and unknown entities/properties squiggled against
    `tdm.model.json`,
  - completion: entity names after `a/an`, property names inside `with` clauses, tag
    vocabulary, `for <Entity> "` reference targets,
  - hover: verb documentation with examples (sourced from the Wave 1 docs content).
- **VS Code extension** (thin client, ~200 lines): activates on `*.feature` files whose
  workspace has a `tdm.settings.json`, launches `tdm lsp`. Coexists with Reqnroll/Cucumber
  extensions by only claiming files under configured `featurePaths`.

### 2.3 Statistical generation (Decisions W4-D4, W4-D5)

- **Generator plugin API**: `IValueGeneratorPlugin` (property-match predicate + seeded
  value factory) discovered from plugin assemblies — teams extend the auto-faker heuristic
  table (v1 §5.3 anticipated "extensible via config") with code, not forks.
- **Config-declared distributions** (no code) in the entities section:

```jsonc
"Order": { "properties": {
  "Total":  { "distribution": "lognormal", "mean": 120, "sigma": 1.2 },
  "Status": { "weights": { "Pending": 0.6, "Shipped": 0.3, "Cancelled": 0.1 } }
} }
```

- **Correlated fields** via dataset packs (city↔postcode↔country tuples, locale-aware
  names/addresses): `"locale": "en_GB"` per domain; correlation groups sample one tuple
  row seeded per entity. All of it flows through the existing per-scenario `Randomizer`,
  so determinism guarantees (v1 D8) are unchanged — same seed, same distribution draws.

### 2.4 API-based seeding (Decision W4-D6)

For domains that forbid direct database writes, persistence routes through their public API
— which also exercises validation, auth and side-effects for free.

- New persistence mode `"persistence": "Api"` with a per-domain endpoint map:

```jsonc
"api": {
  "baseUrl": "https://orders.acme.internal",
  "auth": { "provider": "AzureAd", "scope": "api://orders/.default" },   // W2 secrets chain
  "entities": {
    "Customer": { "create": "POST /api/customers", "update": "PUT /api/customers/{id}",
                   "delete": "DELETE /api/customers/{id}", "getByKey": "GET /api/customers?name={key}" }
  }
}
```

- Implemented as an `ApiDomainRuntime : IDomainRuntime` — the v1 engine/runtime seam was
  built for exactly this: the engine doesn't change. Entity shape comes from the same
  plugin-loaded CLR types (serialised to JSON payloads); the identity contract still
  applies (client-set ids in payloads where the API accepts them; server-assigned ids
  captured from responses into the manifest, like DB-generated ints).
- Load/verify uses `getByKey`; lifecycle support: `Persistent` and `TrackedTeardown`
  (deletes via API, reverse order); `Transactional` is unsupported and fails validation
  with a clear message.

### 2.5 Seed packs (Decision W4-D7)

- Versioned NuGet packages containing feature files + entity config fragments + (Wave 2)
  key-registry entries: `"seedPacks": [ { "package": "Acme.SeedPacks.EuReferenceCustomers", "version": "2.1.0" } ]`.
- Pack features execute **before** local features (deterministic order: pack list order,
  then alphabetical within a pack); pack entity-config fragments merge under local
  settings (local wins). Resolved pack versions are recorded in the manifest and lockfile.
- Kills the copy-paste economy around shared base data: "EU reference customers v2" is a
  dependency, not a snippet.

### 2.6 Masking/subsetting — design spike only (Decision W4-D8)

- Scoped **spike, not GA**: prototype a `tdm profile` command that connects read-only to a
  production-like source and emits a **statistics pack** (per-column distributions,
  cardinalities, correlation hints — never row values), which §2.3 consumes as
  distribution config. Synthetic-but-realistic without copying data — and the Wave 2
  attestation stays truthful (the stats pack is declared in attribution).
- Deliverable is a design doc + prototype + risk review (data-protection sign-off
  required before any GA decision). Direct row subsetting/masking remains out of scope.

---

## 3. Decisions log

| # | Decision | Rationale |
|---|---|---|
| W4-D1 | Report = self-contained HTML from the manifest, no server | Zero infrastructure; works as a CI artifact and an email attachment |
| W4-D2 | LSP diagnostics reuse StepGrammar + an exported model file | One parser, no drift; works offline without DB access |
| W4-D3 | LSP ships inside the dotnet tool; extension is a thin client | One install; other editors (Rider, Neovim) get LSP for free |
| W4-D4 | Generation extends via plugin API + declarative distributions | Code for the complex 5%, config for the common 95% |
| W4-D5 | All new generators draw from the per-scenario Randomizer | Determinism (v1 D8) survives every new capability |
| W4-D6 | API seeding = ApiDomainRuntime behind the existing IDomainRuntime seam | Engine untouched; proves the v1 abstraction earns its keep |
| W4-D7 | Seed packs are NuGet packages riding the existing acquisition/lockfile flow | Versioned, reviewable, reproducible shared data |
| W4-D8 | Subsetting = statistics profiling spike, never raw rows, GA gated on risk review | Keeps TDM's "no production data" promise intact by default |

## 4. Phases

1. **W4-P1 — Report:** HTML renderer + lineage graph + CI artifact publishing.
2. **W4-P2 — Editor:** `export-model`, `tdm lsp` (diagnostics → completion → hover), VS Code extension.
3. **W4-P3 — Generation:** plugin API, distributions, locale/correlation packs.
4. **W4-P4 — API seeding + seed packs:** ApiDomainRuntime + endpoint config; pack acquisition/merge.
5. **W4-P5 — Subsetting spike:** `tdm profile` prototype + risk review doc.

## 5. Acceptance criteria

- A manifest from the sample domains renders to a single HTML file showing the Billing
  invoice's lineage back to the Orders customer via the identity contract.
- Typing an unknown entity in VS Code squiggles within a second, and `with ` completion
  offers that entity's real properties.
- A weighted `Status` distribution over 10k generated orders lands within 2% of configured
  weights — and identically across two same-seed runs.
- The Orders sample domain seeds successfully through a stub HTTP API (WireMock) with
  TrackedTeardown deleting via API in reverse order, engine code unchanged.
- Two repos consuming the same seed pack version produce identical customer identities.

## 6. Risks & mitigations

| Risk | Mitigation |
|---|---|
| Lineage graph unusable at bulk volumes | Aggregate bulk nodes ("500 Products") using the W3 manifest sampling |
| LSP model file goes stale vs actual schema | CI regenerates `tdm.model.json` and fails on diff; extension surfaces staleness banner |
| API seeding hits rate limits / slow endpoints on bulk | Bulk-count grammar through API requires explicit policy opt-in; concurrency + retry config per domain |
| Distribution config drifts from real production shape | That's the §2.6 profiling spike's exact job — feed real statistics in, safely |
| Seed pack version conflicts between packs | Lockfile + deterministic merge order; conflicting entity config keys fail validation loudly |

## 7. Open items

- Hosted portal (browse runs/reports/trends in one place) — revisit after this wave; the
  registry (W2) + trend store (W3) + HTML reports may be enough.
- Rider/Visual Studio plugin demand once the LSP exists.
- Whether seed packs may also carry compiled generator plugins (proposed: yes, same
  assembly-loading rules as domain plugins).
