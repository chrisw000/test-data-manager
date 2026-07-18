# TDM messaging spine

**Purpose:** the single source the docs-site home page (W5-P1) and the Engineering Forum
deck (W5-P2) are both written from, so the deck and the docs cannot tell diverging stories
(wave risk table). When product messaging changes, change it *here first*, then propagate.

---

## The one-sentence

> **TDM seeds relational test data from plain-language Gherkin — deterministically,
> across every domain database your application owns, with an audit manifest for every row
> it touches.**

Short form (page subtitles, deck title slide):
*Test data in the test's language — deterministic, multi-domain, auditable.*

## The problem sentence

Every team hand-rolls test data: SQL scripts that rot, shared "golden" databases that
drift, copies of production nobody should have. The data a test needs is a *business*
statement — but today it is expressed as infrastructure.

## The domain reality (draw this early, always)

An application is **many domains**. Each domain lives in **its own database**, typically
fronted by **its own access API**, with **its own `DbContext`**. Any test that spans two
domains needs their data to *agree* without a shared transaction or a coordination
service. TDM's multi-domain features — the identity contract, external references,
projections, API seeding, seed packs — only make sense against this picture, so every
multi-domain page and slide draws it before explaining anything.

## Three benefit pillars, per persona

### QA / test author
1. **Write data in the test's language** — `Given an Order exists for Customer "Acme Ltd"`
   is the test data. No SQL, no builders, no fixtures.
2. **Deterministic by seed** — `@seed:42` gives the same rows every run, on every machine;
   failures reproduce.
3. **Verify steps close the loop** — `Then 1 Order should exist …` reads back through the
   same pipeline; the seed data asserts itself.

### Developer / domain owner
1. **Your domain plugs in as-is** — ship the `DbContext` + repositories you already have;
   conventions resolve entities, keys, fakers and repositories with zero TDM code.
2. **`tdm explain` shows every decision** — grammar match, resolution, faker, persistence
   route, identity — before anything touches a database.
3. **Realistic values for free** — convention fakers, locales, and declarative statistical
   distributions (`weights`, `lognormal`) drawn from the scenario seed.

### Platform / DevEx
1. **`tdm validate` is a CI gate that needs no database** — grammar, resolution and policy
   checked before any data exists; SARIF annotations on the PR.
2. **Policy as code for environments** — write-repository rules, environment policy files,
   approval tokens, run registry and locks: golden paths, not tribal knowledge.
3. **Every run is an audit artifact** — the manifest records final values, seeds, routes
   and ids; checksummed, optionally signed; renders to a self-contained HTML report with
   trends.

### Agentic coder / tester
1. **A machine-legible loop** — `validate → explain → run → read the manifest` is a tight,
   deterministic feedback cycle an agent can drive unaided.
2. **Structured outputs everywhere** — SARIF, JUnit, JSON manifests: no screen-scraping.
3. **Guardrails by default** — policy gates and locks mean an agent cannot accidentally
   write where it shouldn't.

## The canonical example feature

Used on the home page, in Getting Started, and on deck slides — always this one, so
readers meet the same example everywhere:

```gherkin
@seed:42
Feature: Orders regression seed
  Scenario: Customer places an order
    Given a Customer exists with name "Acme Ltd" and tier "Gold"
    And an Order exists for Customer "Acme Ltd" with status "Pending"
    Then 1 Order should exist with status "Pending"
```

And the canonical single step (for `tdm explain`, the pipeline walkthrough, the deck):

```
an Order exists for Customer "Acme Ltd" with status "Pending"
```

## The conceptual centrepiece

**The identity contract:** `UUIDv5("{domain}|{Entity}|{naturalKey}")`. Two teams that
have never spoken derive the same id for the same business object. This is the idea that
makes multi-domain data agree without coordination — it gets its own slide, its own
interactive (the identity explorer), and early placement in every concepts discussion.

## Phrases we always use

| Phrase | Why |
|---|---|
| "no production data by default" | the safety posture, stated positively |
| "deterministic" / "reproducible by seed" | the core promise; never "random test data" |
| "the manifest" (definite article) | it is *the* audit artifact, not *a* log |
| "the identity contract" | a named, teachable idea — not "the GUID scheme" |
| "domains" (plural, own database/API/DbContext) | keeps the multi-domain reality in frame |
| "in the test's language" | the QA pitch in four words |
| "validate persists nothing" | the CI gate's whole appeal |

## Phrases we never use

| Phrase | Instead |
|---|---|
| "mocking" / "fake data" | TDM seeds *real rows in real databases*; say "generated data" |
| "random" | "deterministic under the scenario seed" |
| "synthetic data platform" | overclaims; say "test data seeding" |
| "replaces your test framework" | it feeds any framework; say "framework-agnostic" |
| "anonymised production copy" | the thing TDM exists to make unnecessary |
| "simple" / "just" | let the 15-minute Getting Started prove it instead |

## Proof points (keep current)

- ≤15 minutes from clone to a green `tdm validate` and a seeded demo run (Getting Started
  is CI-executed on every push — the docs cannot drift).
- Providers: SQLite, SQL Server, PostgreSQL (plugin) — same features, CI matrix-tested.
- One composite GitHub Action wires validate/report/gate into any pipeline.
- The HTML report and the forum deck are single files that open from `file://` with zero
  network access — the artifact posture is itself a feature.
