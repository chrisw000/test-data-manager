# TDM — Gherkin-driven Test Data Manager

A standalone, enterprise-grade test data manager. Feature files describe data needs in
business-readable Given/When/Then steps; the TDM parses them **at runtime** (no recompile per
feature change), resolves entities/fakers/repositories by configurable convention, generates
deterministic synthetic data via Bogus, applies per-scenario overrides, and persists via
repository or DbContext — with structured logging, OpenTelemetry instrumentation, a
machine-readable seeding manifest, and benchmark timings.

Built to the design in [`tdm-handoff.md`](tdm-handoff.md) (all phases P1–P4).

## Quick start

```powershell
dotnet build Tdm.slnx

# Explore what the conventions resolved (the convention debugging tool)
dotnet run --project src/Tdm.Host -- list-entities --settings tdm.settings.json

# CI-friendly dry run: parse + resolve everything, persist nothing
dotnet run --project src/Tdm.Host -- validate --settings tdm.settings.json

# Seed the sample domains (SQLite databases under ./output)
dotnet run --project src/Tdm.Host -- run --settings tdm.settings.json

# Destroy everything a run created, in reverse dependency order
dotnet run --project src/Tdm.Host -- teardown --settings tdm.settings.json --manifest output/<file>.tdm.json
```

Exit codes: `0` clean, `1` completed-with-warnings, `2` failed.
CLI overrides: `--seed n`, `--policy BestEffort|FailObject|FailRun`,
`--lifecycle Persistent|Transactional|TrackedTeardown`, `--benchmark true|false`.

## Solution layout

| Project | Purpose |
|---|---|
| `src/Tdm.Identity` | **Frozen identity contract v1** — zero-dependency UUIDv5 derivation (`netstandard2.0` + `net10.0`) so API mocks and other tooling compute identical GUIDs |
| `src/Tdm.Core` | Grammar, seeding pipeline, override engine, failure policies, manifest model, benchmark aggregation, statistical generation (distributions/weights/correlated datasets + `IValueGeneratorPlugin`). No EF references |
| `src/Tdm.EfCore` | EF-model-first entity resolution, repository/faker resolution, persistence routing, lifecycle management, provider registry (Sqlite/SqlServer in-box; `IProviderBootstrap` plugin seam) |
| `src/Tdm.Api` | API-based seeding (`"persistence": "Api"`): an `IDomainRuntime` that persists through a domain's public HTTP API — same CLR types and generation, engine untouched |
| `src/Tdm.Providers.PostgreSql` | PostgreSQL provider plugin: Npgsql bootstrap + binary `COPY` bulk inserter, discovered from a domain's plugin assemblies |
| `src/Tdm.Plugins` | Isolated `AssemblyLoadContext` per domain, folder-based acquisition (NuGet-feed acquisition is a documented extension point: `IPluginAcquirer`) |
| `src/Tdm.Observability` | OTLP exporters for the `Tdm` ActivitySource/Meter, manifest writer, run summary, report emitters (SARIF/JUnit/living-doc HTML) |
| `src/Tdm.Host` | The `tdm` console CLI: `run` (`--resume`) \| `teardown` \| `validate` \| `list-entities` \| `explain` \| `replay` \| `verify` \| `publish` \| `report` \| `export-model` \| `lsp` \| `profile` \| `bench tune`/`compare` |
| `src/Tdm.Lsp` | Language server (stdio): live `StepGrammar` diagnostics, entity/property completion and verb hover against the exported `tdm.model.json` — hand-rolled LSP framing, no server framework |
| `editors/vscode` | Thin VS Code client (~100 lines) that launches `tdm lsp` for workspaces with a `tdm.settings.json`, claiming only configured `featurePaths` |
| `grafana/` | Importable dashboard + starter alert rules for the `Tdm` OTEL metrics |
| `tests/Acme.Orders.Data.Persistence` | Sample domain plugin, **modern** profile (`{Name}Entity`, repositories, fakers) |
| `tests/Acme.Billing.Data.Infrastructure` | Sample domain plugin, **legacy** profile (`{Name}Model`, int identity keys, projection read-model, no repositories) |
| `tests/Tdm.Core.Tests`, `tests/Tdm.EfCore.Tests` | xUnit v3 unit tests |
| `tests/Tdm.Integration.Tests` | Reqnroll + xUnit v3 — feature files that drive the full engine against both sample domains on SQLite |
| `features/` | Example TDM feature files exercising every grammar verb |

Stack: .NET 10, **EF Core 8** (pinned to the org baseline per the handoff), Gherkin 41,
Bogus 35, Reqnroll 3.3 (xUnit v3 adapter), OpenTelemetry 1.16, System.CommandLine 2.0.

## The grammar

```gherkin
@seed:42 @domain:Orders
Feature: Anything

  Scenario: Every verb
    # Create — single, with property overrides
    Given a Customer exists with name "Acme Ltd" and tier "Gold"
    # Create — domain-qualified when a name is ambiguous across domains
    Given a Billing Account exists with name "Acme Billing"
    # Create — bulk from a DataTable (shared "for"/"with" clauses apply to every row)
    Given the following Invoices exist for Account "Acme Billing":
      | InvoiceNumber | Amount | Status | IssuedDate |
      | INV-001       | 10.00  | Draft  | today-3d   |
    # Create — n generated entities (the load-test vehicle; chunked AddRange persistence)
    Given 500 Products exist with category "Widgets"
    # Reference — context bag first, then DB lookup by natural key
    Given an Order exists for Customer "Acme Ltd" with status "Pending"
    # External reference — computes the owning domain's GUID via the identity contract;
    # persists nothing remotely; FkOnly or Projection behaviour locally
    Given an external Customer reference "Acme Ltd" from CRM
    # Update / Delete
    When the Customer "Acme Ltd" is updated with tier "Platinum"
    When the Product "SKU-1" is deleted
    When all Orders with status "Draft" are deleted
    # Load (verify) — read + assert, part of the benchmark surface
    Then a Customer "Acme Ltd" should exist with tier "Platinum"
    Then 500 Products should exist with category "Widgets"
```

`"{key}"` always means the entity's configured **natural key** (profile default `Name`,
overridable per entity). Values accept ISO 8601 dates plus evergreen relative tokens
(`today`, `now`, `today+3d`, `today-1y`; units `d/w/m/y`).

Tags: `@seed:n` (per-scenario deterministic seed, default 1), `@domain:X` (pins resolution),
`@benchmark` (deep per-stage timing), `@persistent` / `@ephemeral` (lifecycle override),
`@skip`. Scenario Outlines, Backgrounds, Rules, DataTables are fully supported.

Unmatched steps never hard-crash a run (unless the policy is FailRun) — they are logged,
recorded in the manifest, and handled per failure policy.

## Key behaviours

- **Deterministic identity** — client-settable Guid keys derive from
  `UUIDv5(namespace, "{domain}|{Entity}|{naturalKey}")` via `Tdm.Identity`. Stable across
  runs, scenarios, databases and environments; the foundation of cross-domain agreement
  without any distributed coordination. Bulk filler entities derive from
  `{domain}|{Entity}|{scenario}|{seed}|{ordinal}`. DB-generated int keys are captured into
  the manifest after insert.
- **Idempotent creates** — a create whose natural key already exists reuses the row (same
  key ⇒ same identity) and re-applies the step's explicit overrides as an update, so
  re-running a `Persistent` environment seed converges instead of exploding.
- **Persistence routing** — `RepositoryFirst` (default; repositories carry domain behaviour
  worth exercising), `DbContextOnly`, or `RepositoryOnly` per domain. Repository methods are
  matched via `IRepository<T>` first, then duck-typed name conventions (`Add`, `AddAsync`,
  `Add{Name}`, `Insert`, `Create`, … — configurable per profile). Bulk creates always use
  chunked `AddRange` (default 500/chunk).
- **Fakers** — `{Name}Faker : Faker<TEntity>` resolved by convention; otherwise a heuristic
  auto-faker (property-name rules first — email/phone/sku/price/…, then type rules) with a
  warning. Seeded per scenario; sequence consumed in step order.
- **Lifecycles** — `Persistent`, `Transactional` (rollback at scenario end),
  `TrackedTeardown` (rows recorded, deleted in reverse dependency order at scenario end or
  later via `tdm teardown --manifest`). Teardown failures are recorded as `orphaned`, never
  swallowed.
- **Failure policies** — `BestEffort` (warn + skip property/object/step),
  `FailObject` (reject object, continue), `FailRun` (abort). Every warning carries scenario,
  step line, entity, property, raw value and exception summary — logged **and** manifested.
- **Observability** — `ILogger` scopes per run→feature→scenario→step; `Tdm` ActivitySource
  spans and Meter counters/histograms exported via OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT`
  is set; JSON manifest `./output/{run}-{timestamp}.tdm.json` with full final property
  values, seeds, versions (TDM/Bogus/EF), reference resolutions and benchmark stats
  (count/total/mean/p50/p95/max per verb and entity).

## Configuration (`tdm.settings.json`)

See the annotated demo file at the repo root. Highlights: convention profiles (`modern` /
`legacy` built in, fully overridable), per-domain provider + connection string
(`connectionStringName` resolves from `TDM_CONNECTIONSTRINGS__{NAME}` env var), per-entity
natural key / id strategy / external-reference behaviour (`FkOnly` or `Projection` with
`projectionEntity`), per-domain external-reference mode (`Synthesize` / `Verify` with
`verifyEndpoint` URL template / `Trust`).

## Consuming from your own test suite

The plugin CLI host is the primary mode (zero domain coupling). The compile-time mode also
works — see `tests/Tdm.Integration.Tests`, which builds runtimes directly:

```csharp
var runtime = DomainRuntimeBuilder.Build(domainSettings, settings, [typeof(OrdersDbContext).Assembly]);
var engine = new TdmEngine(settings, [runtime]);
var manifest = await engine.RunAsync(new GherkinPlanParser().ParseText(featureText).AsPlan());
```

## Tests

```powershell
dotnet test Tdm.slnx
```
