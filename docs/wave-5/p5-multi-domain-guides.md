# W5-P5 — Multi-domain & integration guides + Acme.Fulfilment

**Parent:** [wave-5-handoff.md](../wave-5-handoff.md) · Decisions W5-D5, W5-D7
**Depends on:** P1. The heaviest phase: two interactives, one new example domain, five guides.

The framing every page in this phase opens with: **an application is many domains; each
domain lives in its own database, usually behind its own API, with its own `DbContext`.**
Test data that spans them cannot rely on shared transactions, shared sequences, or
runtime lookups across team boundaries — that is the problem the identity contract solves.

## 1. `guides/multi-domain-identity.md` — Aligning domains with the ID

- The natural-key idea recapped for users: tests speak names ("Acme Ltd"), not GUIDs.
- **Identity explorer** (interactive): type `domain`, `entity`, `key` → watch
  `UUIDv5("{domain}|{Entity}|{key}")` derive, with the byte-level steps expandable.
  Parity: reproduces the `Tdm.Identity` unit-test vectors (embed them as preset examples);
  a note shows the same answer from `tdm explain`.
- **Multi-domain map** (interactive): the Orders/Billing/Fulfilment picture — domains,
  databases, APIs, DbContexts as swim-lanes; identity edges animate on hover showing both
  sides deriving the same GUID independently. (This is the one component allowed to vendor
  D3 if hand-rolled layout gets unwieldy.)
- External references in practice: `an external Customer reference "Acme Ltd" from Orders`;
  Synthesize/Verify/Trust per domain; FK-only vs projection behaviour, with the
  CustomerSummary projection as the worked example.
- Governance: the key registry (`tdm.keys.json`) — publishing, validating, why violations
  are never overridable; sharing base data via seed packs so both repos *mean* the same
  customers.
- Reading lineage: the HTML report's lineage graph as the proof artifact.

## 2. `guides/api-seeding.md` — Seeding through a domain's public API

- When you need it (no direct DB writes allowed; API-side effects wanted) and what you
  give up (no transactional lifecycle, no query-surface steps).
- Full worked config for the Fulfilment API variant; auth via the secret chain; retries;
  server-assigned vs client-set ids and how each lands in the manifest.
- Testing pattern: the stub-API approach from `Tdm.Api.Tests` shown as a template teams
  copy for their own contract checks.

## 3. `guides/seed-packs.md` — Consuming & authoring seed packs

- Consuming: settings, lockfile pinning, `--update-plugins`, what the manifest records.
- Authoring: pack layout, versioning discipline (packs are contracts — semver + reviewed
  key-registry entries), publishing to the feed, the two-repos-same-identities guarantee.
- Anti-patterns: packs that fight local config; two packs claiming one entity (and the
  loud failure that follows).

## 4. `guides/testcontainers.md` — TestContainers & the provider matrix

- The repo's own matrix as the template: SQLite default leg; SqlServer/PostgreSql via
  Testcontainers under `TDM_TEST_PROVIDER`; the `ProviderMatrix`/`TestDomains` harness
  pattern annotated for reuse in consuming repos.
- Wiring TDM runs (not just unit tests) against a container: connection strings via the
  secret chain, `ensureCreated` vs migrations, per-provider bulk routes.
- CI recipes: the provider-matrix job from `ci.yml` explained line by line.

## 5. Acme.Fulfilment — the complex-example domain (W5-D7)

New sample under `tests/Acme.Fulfilment.Data.Api/` (modern profile), sized to teach.
**Capped edge-case list** (anything more is a new wave item):

| Edge case | Vehicle |
|---|---|
| Own database + own API + own DbContext | the domain itself; API persistence in the demo variant, DB mode in tests |
| Self-referencing hierarchy | `LocationEntity` (site → aisle → bin, parent FK) |
| Server-assigned `long` key | `ShipmentEntity` |
| Enum-heavy state flow | `ShipmentStatus` with weighted statistical config |
| `DateOnly`/`TimeOnly` | delivery windows |
| Correlated dataset | carrier ↔ service-level CSV |
| Three-domain identity chain | Fulfilment ships an Orders order billed by Billing — external references both directions |

CI: built + seeded on the SQLite leg; the demo `tdm.settings.json` gains a commented-out
Fulfilment section (kept out of the default run to hold demo runtime down).

## 6. `guides/testing-complex-domains.md`

Walks Fulfilment edge case by edge case: the feature that exercises it (**the same
feature CI runs** — the guide cannot lie), the config that makes it work, the manifest/
report evidence to look for, and the general rule to take away. Ends with a "mapping your
own domain" checklist (keys, navigations, projections, API constraints).

## Acceptance

- Identity explorer output equals the `Tdm.Identity` test vectors; map renders without
  network; both embedded in their guide pages and reachable from the tour.
- Fulfilment builds, seeds and tears down in CI; the complex-domains guide references
  only CI-executed features (lint: feature paths named in the guide must exist under the
  domain's `features/`).
- All five guides carry snippets through `docs-verify`, "Where next" footers, tour metadata.
