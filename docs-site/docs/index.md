# TDM — Gherkin-driven Test Data Manager

**TDM seeds relational test data from plain-language Gherkin — deterministically, across
every domain database your application owns, with an audit manifest for every row it
touches.**

Every team hand-rolls test data: SQL scripts that rot, shared "golden" databases that
drift, copies of production nobody should have. The data a test needs is a *business*
statement — but today it is expressed as infrastructure. TDM turns the business statement
itself into the seeding instruction:

```gherkin
@seed:42
Feature: Orders regression seed
  Scenario: Customer places an order
    Given a Customer exists with name "Acme Ltd" and tier "Gold"
    And an Order exists for Customer "Acme Ltd" with status "Pending"
    Then 1 Order should exist with status "Pending"
```

Domain teams ship the EF Core `DbContext` + repositories they already have as **plugins**;
anyone can then declare data in the test's language and get **deterministic, reproducible,
auditable** rows — no production data by default, generated values under a pinned seed.

## Find your path

<div class="grid cards" markdown>

- **QA / test author** — you want test data in the test's language, reproducible by seed,
  with verify steps that close the loop.
  Start: [Getting started](start/getting-started.md) → [Daily use for QAs](guides/daily-use-qa.md)

- **Developer / domain owner** — you want your domain seedable as-is: conventions resolve
  entities, keys, fakers and repositories with zero TDM code, and `tdm explain` shows
  every decision.
  Start: [Getting started](start/getting-started.md) → [Daily use for developers](guides/daily-use-dev.md)

- **Platform / DevEx** — you want golden paths: a no-database `validate` gate in CI,
  policy as code for environments, locks, and an audit manifest for every run.
  Start: [CI — validate, report, gate](guides/ci.md) → [CD & environments](guides/cd-environments.md)

- **Agentic coder / tester** — you want a machine-legible loop:
  `validate → explain → run → read the manifest`, with structured outputs (SARIF, JUnit,
  JSON) and guardrails by default.
  Start: [Getting started](start/getting-started.md) → the agent kit (arrives in W5-P6)

</div>

Prefer to walk everything in order? Take the [guided tour](start/tour.md).

## The three foundations

- **The manifest** — every run writes a JSON manifest with full final values, seeds,
  persistence routes and ids: *the* reproducibility and audit artifact. It renders to a
  [self-contained HTML report](reference/reports-and-manifest.md), checksummed and
  optionally signed.
- **The identity contract** — deterministic UUIDv5 ids derived from
  `{domain}|{Entity}|{naturalKey}`, so independent teams' data references agree without
  coordination. This is what makes multi-domain data line up — see
  [Concepts](start/concepts.md).
- **`tdm validate` persists nothing** — grammar, entity resolution and policy (e.g.
  [write repositories required](reference/decisions.md)) are checked in CI before any
  data exists.

## One application, many domains

An application is many domains; each domain lives in its own database, typically fronted
by its own access API, with its own `DbContext`. TDM seeds each domain through its own
plugin, and the identity contract keeps cross-domain references agreeing — no shared
transaction, no coordination service. The [Concepts](start/concepts.md) page walks a
single step through the whole pipeline, stage by stage.

## Proof, not promises

- ≤15 minutes from clone to a green `tdm validate` and a seeded demo run — that's the
  [Getting started](start/getting-started.md) path, and CI executes every command on it
  against this repository's sample workspace on every push. The docs cannot drift.
- Providers: SQLite, SQL Server, PostgreSQL (plugin) — the same features, matrix-tested
  in CI.
- A composite GitHub Action wires validate/report/gate into any pipeline —
  see [CI](guides/ci.md).

!!! info "Engineering record"
    The design docs behind every feature live in
    [`/docs`](https://github.com/chrisw000/test-data-manager/tree/main/docs) — each guide
    links the decisions it stands on.
