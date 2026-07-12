# Test Data Manager (TDM) — Implementation Handoff

**Status:** Design agreed, ready for implementation
**Audience:** Implementing engineer / AI pair (Fable)
**Owner:** Chris (Engineering Manager)
**Date:** 2026-07-11

---

## 1. Purpose

A standalone, enterprise-grade **Gherkin-driven Test Data Manager**. Feature files describe data needs in business-readable Given/When/Then steps. The TDM parses them at runtime (no recompile per feature change), resolves entities/fakers/repositories **by configurable convention**, generates deterministic synthetic data via Bogus, applies per-scenario overrides, and persists via Repository or DbContext — with full structured logging, OTEL instrumentation, a machine-readable seeding manifest, and benchmark timings for Create/Update/Delete/Load operations.

**Non-goals (this round):** UI, scheduling, data masking/subsetting from production, cross-database referential seeding.

---

## 2. Architectural overview

```
┌────────────────────────────────────────────────────────────┐
│ TDM Host (console/CLI)                                     │
│                                                            │
│  ┌──────────┐  ┌───────────────┐  ┌────────────────────┐  │
│  │ Gherkin   │→│ Step Grammar   │→│ Seeding Plan        │  │
│  │ Parser    │  │ Interpreter    │  │ (verbs + prop bags) │  │
│  └──────────┘  └───────────────┘  └─────────┬──────────┘  │
│                                              ▼             │
│  ┌────────────────────── Execution Engine ─────────────┐  │
│  │  EntityResolver → FakerResolver → OverrideEngine     │  │
│  │       → ReferenceResolver → PersistenceRouter        │  │
│  └───────────────┬───────────────────┬──────────────────┘  │
│                  ▼                   ▼                      │
│  ┌────────────────────┐   ┌─────────────────────────────┐  │
│  │ Domain Plugin(s)    │   │ Observability                │  │
│  │ (runtime-loaded     │   │ ILogger / OTEL traces+metrics│  │
│  │ assemblies:         │   │ / JSON run report + manifest │  │
│  │ DbContext, Entities,│   └─────────────────────────────┘  │
│  │ Repos, Fakers)      │                                    │
│  └────────────────────┘                                    │
└────────────────────────────────────────────────────────────┘
```

The TDM is **one codebase, one deployable tool**. Domain code (DbContexts, entities, repositories, fakers) is **never copied in** — it is loaded at runtime as plugins.

---

## 3. Decoupling from domain DbContexts (Decision D1)

**Requirement:** TDM must not be pinned to any domain build. DbContexts live in production API repos, published as NuGet packages. No TDM code in domain repos.

**Decision: runtime plugin loading via `AssemblyLoadContext`, fed from a plugin folder populated by NuGet restore.**

How it works:

1. Each domain publishes its data package (e.g. `Acme.Orders.Data.Persistence`) to the internal NuGet feed — this already happens for the production API.
2. TDM run configuration names the packages/assemblies it needs. A restore step (`dotnet restore` against a generated project, or direct `NuGet.Protocol` download) drops assemblies into `./plugins/{domain}/`.
3. TDM loads each plugin folder into an isolated collectible `AssemblyLoadContext` (one per domain to avoid version clashes between domains).
4. Within the loaded assemblies, TDM discovers by reflection:
   - all `DbContext` subclasses,
   - all repository services (per convention config, §5),
   - all `Faker<T>` subclasses.
5. DbContext instantiation: locate a constructor accepting `DbContextOptions<TContext>`, build options via `DbContextOptionsBuilder<TContext>` using the **provider and connection string from TDM run config** (provider assemblies — e.g. `Microsoft.EntityFrameworkCore.SqlServer` — ship with the TDM host, version-aligned to the org's EF baseline). This mirrors what `dotnet ef` design-time tooling does.

**Compile-time generic pass remains supported** as a secondary mode: TDM core is also published as a NuGet library (`Tdm.Core`) so a team *can* build a thin domain-specific host (`new TdmRunner<OrdersDbContext>(...)`) if they want IDE-time type safety. But the primary, recommended mode is the reflection/plugin host — zero domain coupling.

**Risks & mitigations:**

| Risk | Mitigation |
|---|---|
| EF version skew between TDM host and domain package | Pin TDM's EF version to the org baseline; validate `Microsoft.EntityFrameworkCore` assembly version at plugin load; fail fast with a clear error naming both versions |
| Transitive dependency conflicts between domains | Isolated `AssemblyLoadContext` per domain |
| DbContext requires services in its ctor (not just options) | Convention config allows naming a factory/`IDesignTimeDbContextFactory<T>` in the plugin; TDM prefers a factory when found |
| Internal/sealed contexts | Require `InternalsVisibleTo` is NOT needed — reflection over public surface only; document that data packages must expose public DbContext |

---

## 4. Convention configuration (Decision D2)

All conventions are externalised in `tdm.settings.json` (per run, overridable via CLI args / env vars). Multiple **convention profiles** support legacy vs modern codebases side by side.

```jsonc
{
  "run": {
    "name": "orders-regression-seed",
    "failurePolicy": "BestEffort",        // BestEffort | FailObject | FailRun (global, per run)
    "lifecycle": "TrackedTeardown",       // Persistent | Transactional | TrackedTeardown
    "defaultSeed": 1,
    "featurePaths": ["./features/**/*.feature"],
    "benchmark": true
  },
  "domains": [
    {
      "name": "Orders",
      "package": "Acme.Orders.Data.Persistence",   // resolved to ./plugins/Orders
      "provider": "SqlServer",
      "connectionStringName": "OrdersDb",           // actual value from env/keyvault
      "conventionProfile": "modern",
      "persistence": "RepositoryFirst"              // RepositoryFirst | DbContextOnly | RepositoryOnly
    },
    {
      "name": "Billing",
      "package": "Acme.Billing.Data.Infrastructure",
      "provider": "SqlServer",
      "connectionStringName": "BillingDb",
      "conventionProfile": "legacy",
      "persistence": "DbContextOnly"
    }
  ],
  "conventionProfiles": {
    "modern": {
      "entityNamespaceSuffix": "Data.Persistence.Domain",
      "entityFolder": "Entity",
      "entityClassPattern": "{Name}Entity",
      "repositoryPattern": "I{Name}Repository",
      "fakerPattern": "{Name}Faker",
      "naturalKeyDefault": "Name"
    },
    "legacy": {
      "entityNamespaceSuffix": "Data.Infrastructure",
      "entityFolder": "Model",
      "entityClassPattern": "{Name}Model",
      "repositoryPattern": "I{Name}Repository",
      "fakerPattern": "{Name}Faker",
      "naturalKeyDefault": "Name"
    }
  },
  "entities": {
    // optional per-entity overrides
    "Customer": { "naturalKey": "Name" },
    "Product":  { "naturalKey": "Sku" }
  }
}
```

Note `entityFolder` is informational/documentation only — resolution is namespace + class-pattern based (folders don't exist in compiled assemblies).

---

## 5. Entity, repository, and faker resolution

### 5.1 Entity resolution — EF model first (Decision D3)

Primary mechanism: **`dbContext.Model.GetEntityTypes()`**. The EF model is the authoritative registry of every mapped entity and its CLR type — no assembly scanning needed and it's convention-agnostic by nature.

Matching a Gherkin entity name ("Customer") to a CLR type:

1. Normalise: case-insensitive, singular/plural tolerant (simple pluraliser).
2. Match against each mapped entity's CLR type name **with profile suffixes stripped** (`CustomerEntity` → `Customer`, `CustomerModel` → `Customer`).
3. If exactly one match → resolved. If zero → fallback to convention-based assembly scan using the profile's `entityClassPattern` + `entityNamespaceSuffix` (covers types deliberately unmapped, e.g. owned/keyless helpers). If still zero → error per failure policy. If more than one (across domains) → require domain qualification in Gherkin (`a Billing Customer exists ...`) or config pin; error otherwise, listing candidates.

### 5.2 Repository resolution (Decision D4)

Repositories resolved by the profile's `repositoryPattern` (`ICustomerRepository`) from the plugin assemblies, instantiated via a per-domain `IServiceCollection` that TDM builds (registering DbContext, discovered repos, and their obvious constructor dependencies; unresolvable dependencies → warning + fall back to DbContext persistence).

**Persistence routing is configurable per domain** (`RepositoryFirst` default — repositories win because they carry domain audit/validation behaviour we want exercised; `DbContextOnly` available for A/B testing both paths, per your requirement).

Repository method matching (in order): exact well-known interface (`IRepository<T>` with `Add/Update/Delete/Get`), else duck-typed method-name conventions (`Add{Name}`, `AddAsync`, `Insert`, `Create`, ...) with single-entity parameter — configurable list. If no persist method matches → warning + DbContext fallback (or fail, per policy).

### 5.3 Faker resolution — convention first, generated fallback (Decision D5)

1. Look for `{Name}Faker` (profile pattern) deriving from `Faker<TEntity>` in plugin assemblies. Instantiate (parameterless ctor preferred; ctor taking `int seed` if present).
2. If none found: **auto-construct a default `Faker<TEntity>`** at runtime with heuristic rules and log a warning:
   - by property type: `string`→`f.Lorem.Word()`, `int/long`→bounded random, `decimal`→`f.Finance.Amount()`, `DateTime`→recent past, `bool`→random, `Guid`→deterministic (see §7), enums→random defined value,
   - by property name heuristics (case-insensitive contains): `email`→`f.Internet.Email()`, `name`→`f.Company.CompanyName()`/`f.Name.FullName()`, `phone`, `address`, `city`, `postcode`, `sku`→pattern, `price/amount/total`→finance, etc. Heuristic table is code-defined but extensible via config.
   - Navigation properties and collections are **skipped** by the auto-faker (relationships are explicit via references, §8).
3. Seed application: `faker.UseSeed(effectiveSeed)` — see §7 for seed scoping. For convention fakers, `UseSeed` is applied after construction; document that fakers must not randomise in their constructors.

### 5.4 Override engine (Decision D6)

Overrides are applied **post-generation** via reflection property-set, not by injecting `RuleFor` — simpler, faker-agnostic, and each property becomes an individually loggable unit for the best-effort policy.

- Type conversion pipeline for the string values from Gherkin: direct assign → `TypeConverter` → `Convert.ChangeType` → enum parse (name or value) → `Guid.Parse` → `DateTime/DateOnly` (ISO 8601 required; also accept relative tokens `today`, `today+3d`, `today-1y` for evergreen data) → nullable unwrap.
- Property name matching: case-insensitive, underscore/space tolerant (`order date` → `OrderDate`).
- Failure of a single property conversion → handled per failure policy (§10): warn-and-skip (BestEffort), reject object (FailObject), abort run (FailRun). Every skip is logged with property, raw value, target type, and exception summary, and recorded in the manifest.

---

## 6. Gherkin grammar specification (Decision D7 — best practice set by TDM)

Parser: official `Gherkin` NuGet package (same parser Reqnroll uses). Full support for Scenario, Scenario Outline + Examples, Background, Rule, tags, DataTables, and DocStrings.

### 6.1 Verb grammar

| Verb | Grammar | Example |
|---|---|---|
| **Create** | `Given a/an [Domain] {Entity} exists [with {prop} "{value}" (and ...)]` | `Given a Customer exists with name "Acme Ltd" and tier "Gold"` |
| **Create (bulk)** | `Given the following [Domain] {Entities} exist:` + DataTable | table columns = property names |
| **Create (n)** | `Given {count} {Entities} exist [with ...]` | `Given 500 Products exist with category "Widgets"` (faker fills the rest; overrides apply to all) |
| **Update** | `When the {Entity} "{key}" is updated with {prop} "{value}" (and ...)` | `When the Customer "Acme Ltd" is updated with tier "Platinum"` |
| **Delete** | `When the {Entity} "{key}" is deleted` / `When all {Entities} [with {prop} "{value}"] are deleted` | |
| **Load (verify)** | `Then a {Entity} "{key}" should exist [with {prop} "{value}"]` / `Then {count} {Entities} should exist [with ...]` | read + assert; part of the benchmark surface |
| **Reference** | `... for {Entity} "{key}"` clause inside Create/Update | `Given an Order exists for Customer "Acme Ltd" with status "Pending"` |

`"{key}"` always means the entity's configured **natural key** (§4 `entities` config; profile default `Name`).

### 6.2 Tags (scenario- or feature-level)

| Tag | Meaning |
|---|---|
| `@seed:42` | Deterministic seed for this scenario (default `run.defaultSeed`, i.e. 1) |
| `@domain:Billing` | Pins entity resolution to one domain for the whole scenario |
| `@benchmark` | Force benchmark timing on even if run-level benchmark=false |
| `@persistent` / `@ephemeral` | Override run-level lifecycle for this scenario |
| `@skip` | Parsed, reported as skipped in manifest, not executed |

Unmatched steps (text that fits no grammar rule): logged as a warning with the step text and location, recorded in the manifest as `unmatched`, and handled per failure policy — **the run does not hard-crash on a typo unless policy is FailRun**.

---

## 7. Determinism (Decision D8)

- **Seed scoping: per scenario.** Effective seed = `@seed:` tag value, else run default (1). Per-scenario seeding means adding/reordering scenarios never changes another scenario's data. The effective seed is logged and written to the manifest per scenario.
- Within a scenario, each generated entity consumes the faker sequence in step order, so identical feature file + seed + faker versions ⇒ identical data. **Bogus version is recorded in the manifest** (Bogus determinism can shift across major versions).
- **Deterministic IDs — the TDM Identity Contract (v1):** where the key property is client-settable:
  - `Guid` keys, entity has a natural key → **UUIDv5** (name-based SHA-1) from the fixed TDM namespace GUID + canonical string `"{owningDomain}|{Entity}|{naturalKey}"` (e.g. `"CRM|Customer|Acme Ltd"`). This is stable across runs, scenarios, databases, and environments — the foundation of cross-domain identity agreement (§8.5). Scenario names, seeds, and ordinals deliberately do **not** participate, so renaming or reordering scenarios never changes an identity.
  - `Guid` keys, no natural key (typically bulk-generated filler entities) → UUIDv5 from `"{owningDomain}|{Entity}|{scenario}|{seed}|{ordinal}"`. By definition nothing external references these.
  - Integer keys → only if the column is not identity; otherwise let the DB generate and **capture the generated ID into the manifest** so playback/debugging can still correlate rows.
  - Config flag per entity `"idStrategy": "Deterministic" | "DbGenerated"` (default: Deterministic where possible, detected from EF metadata `ValueGenerated`).
  - The namespace GUID + canonical string format is **frozen and versioned**, published as a zero-dependency NuGet package **`Tdm.Identity`** so API mocks/stubs (WireMock etc.) and any other tooling compute identical GUIDs. Changes require a contract version bump and are treated as breaking.

---

## 8. Reference resolution — context bag + DB lookup (Decision D9)

Resolution order for `for Customer "Acme Ltd"`:

1. **Scenario context bag** (entities created earlier in this scenario, keyed on natural key) — hit here is fully deterministic.
2. **Database lookup** by natural key via repository/DbContext — supports referencing the well-known base seed data you maintain in each database.
3. Not found → failure policy applies.

**Implications you asked about (recorded as accepted trade-offs):**

- DB lookups introduce **environmental non-determinism**: the same feature file can behave differently against databases whose base data drifts. Mitigation: every DB-resolved reference is flagged `resolvedFrom: "database"` in the manifest with the row's PK, so a playback of the manifest is still exact even if the feature file alone isn't.
- Natural keys in the DB may be **non-unique** → multiple matches is an error (listed candidates logged), per failure policy.
- DB lookup requires **read access** and adds latency — lookups are counted in benchmark timings under a separate `Resolve` operation so they don't pollute Create timings.
- FK assignment: TDM sets the FK property if present (`CustomerId`), else the navigation property; EF metadata tells us which exists.

### 8.5 Cross-domain identity & external references (Decision D14)

**Context.** Cross-domain means cross-database. Each domain is tested in isolation first (synthetic data + API mocks); in later environments domains connect via real APIs, and messaging performs cross-domain seeding. External domains are the source of truth for their own entities, accessed via API. Where an external domain's identifiers live inside a local database (FK columns, projection/read-model rows), they must agree with the owning domain's data — **without any cross-database transaction or runtime coordination**.

**Mechanism: coordination without communication.** Because every domain is seeded by the same TDM under the same Identity Contract (§7), any party can *compute* the GUID for `CRM|Customer|Acme Ltd` rather than look it up. The Orders database, the CRM database, and the CRM API mock all independently derive the same value. Distributed transactions are therefore unnecessary and remain **out of scope by design**, not as a limitation.

**Grammar — owned vs external:**

| Grammar | Behaviour |
|---|---|
| `Given a Customer exists ...` | Local entity, seeded into this domain's database (existing Create verb) |
| `Given an external Customer reference "Acme Ltd" from CRM` | Computes the deterministic GUID via `Tdm.Identity`. Persists **nothing** in the owning domain. Locally, one of two configurable per-entity behaviours: (a) `FkOnly` — registers the GUID in the scenario context bag so subsequent `for Customer "Acme Ltd"` clauses populate FK columns; (b) `Projection` — additionally seeds a local read-model/projection row (the eventually-consistent copy messaging would normally produce), using the owning domain's derived PK |

TDM never writes to a database it does not own in the current run.

**Per-environment external-reference mode** (per domain in config, `"externalReferences"`):

| Mode | Environment | Behaviour |
|---|---|---|
| `Synthesize` | Isolation / early | Compute GUIDs locally; seed projections where configured; API mocks aligned via shared `Tdm.Identity` package |
| `Verify` | Integrated | Compute the GUID, then call the owning domain's API to confirm the entity exists; mismatch/absence handled per failure policy and flagged in the manifest |
| `Trust` | Integrated | Messaging/API-driven seeding is assumed to have run; compute the GUID and proceed. Manifest flags the reference `resolvedFrom: "identityContract"` |

**Manifest impact:** external references are recorded with owning domain, natural key, derived GUID, mode, local behaviour (`FkOnly`/`Projection`), and verification outcome where applicable — preserving full playback fidelity across the environment progression.

**Accepted constraint:** natural keys participating in cross-domain identity must be stable and agreed between domain teams (they are, in effect, part of the identity contract). Renaming an entity's natural-key *value* changes its derived identity; the manifest makes any resulting drift visible.

---

## 9. Lifecycle modes (Decision D10)

| Mode | Behaviour | Use case |
|---|---|---|
| `Persistent` | Data committed and left behind | Environment seeding |
| `Transactional` | Whole scenario in one DB transaction, rolled back at scenario end (or on failure) | Pure ephemeral checks, zero residue |
| `TrackedTeardown` | Data committed; every created row recorded in the manifest; at scenario end (or via `tdm teardown --manifest <file>` later) rows deleted in **reverse dependency order** | Ephemeral data that must survive across processes/tools during the test, destroyed per scenario |

Run-level default with per-scenario tag override (`@persistent`/`@ephemeral` → TrackedTeardown). Teardown failures are logged and left in the manifest as `orphaned` — never silently swallowed.

---

## 10. Failure policy (Decision D11)

Global per run (`run.failurePolicy`), three levels exactly as specified:

| Policy | Property conversion failure | Unresolved entity/faker/reference | Persist failure |
|---|---|---|---|
| `BestEffort` | warn, skip property, continue | warn, skip step, continue | warn, skip object, continue |
| `FailObject` | reject that object, continue scenario | skip step as failed, continue | object failed, continue |
| `FailRun` | abort run | abort run | abort run |

Every warning/failure carries: scenario, step text + line number, entity, property, raw value, exception type/message. All routed through `ILogger` **and** the manifest. Exit code reflects outcome: `0` clean, `1` completed-with-warnings, `2` failed.

---

## 11. Observability (Decision D12)

Three sinks, all always-on-capable:

1. **`ILogger`** (Microsoft.Extensions.Logging) — structured logging throughout, scopes per run → feature → scenario → step. Console + optional file sink in the host; consumers can plug Serilog etc.
2. **OpenTelemetry:**
   - `ActivitySource "Tdm"` — spans: `run` → `feature` → `scenario` → `step` → `{resolve|generate|override|persist}`; attributes: entity, verb, domain, seed, policy, outcome.
   - `Meter "Tdm"` — counters (`tdm.entities.created/updated/deleted/failed`), histograms (`tdm.step.duration`, `tdm.persist.duration` tagged by entity+verb+persistence route).
   - OTLP exporter configured via standard env vars.
3. **JSON run report + seeding manifest** (one file, two sections, written to `./output/{run}-{timestamp}.tdm.json`):

```jsonc
{
  "run": { "name": "...", "startedUtc": "...", "durationMs": 0,
           "failurePolicy": "BestEffort", "lifecycle": "TrackedTeardown",
           "tdmVersion": "...", "bogusVersion": "...", "efVersion": "...",
           "outcome": "CompletedWithWarnings" },
  "scenarios": [
    {
      "feature": "OrderProcessing", "scenario": "Customer places an order",
      "seed": 42, "tags": ["@seed:42"],
      "entities": [
        { "ordinal": 1, "entity": "Customer", "verb": "Create",
          "domain": "Orders", "persistedVia": "ICustomerRepository",
          "id": "6fa1...", "idStrategy": "Deterministic",
          "naturalKey": "Acme Ltd",
          "values": { "Name": "Acme Ltd", "Tier": "Gold", "...": "faker-generated values too" },
          "overridesApplied": ["Name", "Tier"],
          "warnings": [], "durationMs": 12 }
      ],
      "references": [ { "step": 3, "target": "Customer:Acme Ltd", "resolvedFrom": "contextBag" } ],
      "unmatchedSteps": [], "warnings": [], "outcome": "Succeeded",
      "benchmark": { "create": { "count": 3, "totalMs": 41, "p50Ms": 12, "p95Ms": 20 },
                     "update": {}, "delete": {}, "load": {}, "resolve": {} }
    }
  ],
  "teardown": { "deleted": 3, "orphaned": [] }
}
```

The manifest records **full final property values** (faker output + overrides) and the effective seed — this is the artefact that makes any run or failure exactly reproducible/playback-able, which also serves your audit-evidence posture.

---

## 12. Benchmarking (Decision D13)

Not BenchmarkDotNet — this is IO-bound integration work, not micro-benchmarking. Instead:

- `Stopwatch`/`ValueStopwatch` instrumentation around each pipeline stage per entity, aggregated per scenario and per run: count, total, mean, p50, p95, max — per **verb** (Create/Update/Delete/Load) and per **entity type**.
- Bulk grammar (`Given 10000 Products exist`) is the intended load-test vehicle; bulk creates use `AddRange` + single `SaveChanges` per step (chunked, chunk size configurable, default 500) with per-chunk timings.
- Results land in all three sinks (ILogger summary table at run end, OTEL histograms, JSON `benchmark` blocks).
- `@benchmark` tag or `run.benchmark=true` toggles the deeper per-stage timing (resolution/generation/override/persist split); coarse per-step timing is always on (negligible cost).

---

## 13. Solution structure

```
Tdm.sln
├── src/
│   ├── Tdm.Core/                 # grammar, pipeline, resolvers, override engine,
│   │                             # manifest, policies (no EF provider refs)
│   ├── Tdm.EfCore/               # EF metadata integration, persistence routing,
│   │                             # provider bootstrap, lifecycle/transaction mgmt
│   ├── Tdm.Plugins/              # AssemblyLoadContext hosting, NuGet acquisition
│   ├── Tdm.Observability/        # OTEL wiring, report/manifest writers
│   └── Tdm.Host/                 # console CLI: run | teardown | validate | list-entities
├── tests/
│   ├── Tdm.Core.Tests/
│   ├── Tdm.Integration.Tests/    # against a sample domain plugin + SQLite/LocalDB
│   └── SampleDomain.Data.Persistence/   # fixture domain: entities, repos, fakers,
│                                          # both convention profiles represented
└── features/                     # example feature files incl. every grammar verb
```

CLI surface: `tdm run --settings tdm.settings.json [--seed n] [--policy p] [--lifecycle l]`, `tdm teardown --manifest file.tdm.json`, `tdm validate` (parse features + resolve everything, persist nothing — CI-friendly dry run), `tdm list-entities --domain X` (prints resolved entity/repo/faker map — the convention debugging tool).

---

## 14. Implementation phases

1. **P1 — Core pipeline, single compiled-in domain:** grammar interpreter, EF entity resolution, faker resolution + auto-faker, override engine, DbContext persistence, ILogger, Create verb only, per-scenario seed, manifest v1. *Proves the spine.*
2. **P2 — Full grammar + policies:** Update/Delete/Load verbs, DataTables/Outlines/bulk-count, reference resolution (bag + DB), failure policies, lifecycle modes incl. TrackedTeardown + `tdm teardown`.
3. **P3 — Plugin decoupling:** AssemblyLoadContext hosting, NuGet plugin acquisition, convention profiles (legacy + modern), repository routing + duck-typed method matching, `validate`/`list-entities`.
4. **P4 — Observability + benchmarking:** OTEL traces/metrics, JSON report finalisation, benchmark aggregation, exit codes, CI examples.

Each phase lands with integration tests against the sample domain and at least one feature file exercising every capability added in the phase.

---

## 15. Decisions log

| # | Decision | Rationale |
|---|---|---|
| D1 | Runtime plugin loading (ALC + NuGet) as primary; `Tdm.Core` NuGet generic host as secondary | Zero coupling to domain builds; no TDM copies in domain repos |
| D2 | Convention profiles in JSON config | Legacy (`*.Data.Infrastructure` / `{Name}Model`) and modern (`*.Data.Persistence.Domain` / `{Name}Entity`) coexist |
| D3 | EF model (`GetEntityTypes`) as primary entity registry; assembly-scan fallback | DbContext is the source of truth; conventions become matching hints, not discovery machinery |
| D4 | RepositoryFirst routing, configurable per domain | Repos carry domain behaviour worth exercising; DbContextOnly enables A/B |
| D5 | Convention faker first, auto-generated `Faker<T>` fallback with warning | Maximum flexibility per your requirement |
| D6 | Post-generation reflection overrides with typed conversion pipeline | Property-granular best-effort policy; faker-agnostic |
| D7 | Fixed verb grammar + tag vocabulary as specified in §6 | Best practice set by TDM per your delegation |
| D8 | Per-scenario seeds (tag `@seed:n`, default 1); UUIDv5 deterministic IDs derived from `domain|entity|naturalKey` (scenario/ordinal only for keyless bulk entities); contract frozen in `Tdm.Identity` NuGet; DB-generated IDs captured to manifest | Identities stable across scenarios, runs, databases, and environments — enables cross-domain agreement without coordination |
| D9 | References: context bag → DB lookup; DB resolutions flagged in manifest | Both supported; non-determinism made visible and replayable |
| D10 | Lifecycle: Persistent / Transactional / TrackedTeardown, tag-overridable | Covers persistent, ephemeral, and destroy-per-scenario use cases |
| D11 | Global failure policy per run: BestEffort / FailObject / FailRun | As specified; everything logged + manifested |
| D12 | ILogger + OTEL + JSON manifest/report | Multiple consumer use cases; manifest doubles as reproducibility + audit artefact |
| D13 | Stopwatch-based instrumentation with percentile aggregation, not BenchmarkDotNet | IO-bound workload; benchmark surface includes CRUD+Load+Resolve |
| D14 | Cross-domain identity via shared Identity Contract: `external ... reference` grammar (FkOnly/Projection local behaviours); per-environment modes Synthesize/Verify/Trust; no distributed transactions by design | Coordination without communication — same TDM + same derivation ⇒ agreeing GUIDs across databases, mocks, and integrated environments |

## 16. Open items (implementer may propose, none are blockers)

- Pluraliser: use Humanizer vs a minimal internal singulariser (Humanizer recommended; tiny dependency, battle-tested).
- Relative date token grammar (`today+3d`) — exact token set to finalise in P2.
- Whether `tdm validate` should also ping the database (connection check) or stay fully offline.
- Chunk size default for bulk persistence (proposed 500) — tune with P4 benchmarks.
- ~~Multi-domain scenarios / cross-domain transactions~~ — **resolved as D14** (identity contract; no distributed transactions by design). Residual sub-items: (a) `Verify` mode needs a per-domain API endpoint convention in config (URL template + auth) — finalise in P3; (b) decide whether `Projection` seeding of read-model rows goes through the projection's own repository or DbContext-only (proposed: DbContext-only, since projections are infrastructure not domain behaviour).
