---
tour_prev: guides/profiling-production-shapes.md
tour_next: guides/api-seeding.md
---

# Multi-domain identity alignment

**Personas:** developer, architect. An application is many domains; each domain lives in
its own database, usually behind its own API, with its own `DbContext`. Test data that
spans them cannot rely on shared transactions, shared sequences, or runtime lookups across
team boundaries. The **identity contract** is how independent domains' data agrees anyway.

## Tests speak names, not GUIDs

Your test means *"the customer Acme Ltd"* — not a surrogate id it can't know in advance.
Every entity has a **natural key** (the business identifier: `Name`, `OrderNumber`,
`Code`), and steps reference by that key. TDM turns the key into a deterministic id so the
reference resolves the same way in every domain, every run.

## The identity contract

```text
id = UUIDv5("{owningDomain}|{Entity}|{naturalKey}")
```

Try it — type a domain, entity and key, or click a preset (each is one of
`Tdm.Identity`'s own unit-test vectors), and expand the byte-level steps:

<div id="tdm-identity-explorer" data-tdm-identity-explorer>
  <noscript>
    <p><em>The explorer needs JavaScript. The derivation is
    <code>UUIDv5("{domain}|{Entity}|{naturalKey}")</code> under a frozen namespace —
    e.g. <code>Orders|Customer|Acme Ltd</code> →
    <code>e47cf5ae-4475-54d3-8027-e09e3a4a1600</code>.</em></p>
  </noscript>
</div>

!!! note "Parity, not resemblance"
    The explorer runs the same RFC 4122 §4.3 derivation as `Tdm.Identity`, under the same
    frozen namespace. A docs-verify test asserts it reproduces the engine's unit-test
    vectors exactly — so it cannot drift from the real ids. `tdm explain` on any step with
    a natural key prints the same value.

## The domain reality

<div id="tdm-multi-domain-map" data-tdm-multi-domain-map>
  <noscript>
    <p><em>The map needs JavaScript. Three domains — Orders, Billing, Fulfilment — each
    with its own API, DbContext and database, joined by identity edges: a Billing Invoice
    and a Fulfilment Shipment each reference an Orders entity by deriving the same
    UUIDv5 independently, with no shared transaction.</em></p>
  </noscript>
</div>

Hover an edge: both domains derive the same id from the same canonical name, with no
coordination. That is the entire mechanism.

## External references in practice

Bring another domain's entity into scope by natural key:

```gherkin
Given an external Customer reference "Acme Ltd" from Orders
And an Invoice exists for Account "A-1" for Customer "Acme Ltd" with amount "1250.00"
```

The Invoice's `CustomerId` becomes `UUIDv5("Orders|Customer|Acme Ltd")` — the same id the
Orders domain wrote, without either database knowing about the other. Per-domain
`externalReferences` behaviour controls what "bring into scope" does:

| Mode | Behaviour |
|---|---|
| `Synthesize` | Derive the id and use it — no lookup (the default; pure identity contract) |
| `Verify` | Derive, then confirm the row exists at `verifyEndpoint` before trusting it |
| `Trust` | Accept an id supplied out-of-band without deriving |

And per-entity, `externalBehavior` decides whether the reference is FK-only or seeds a
**projection** — a local read-model row for the external entity. The sample Billing domain
projects `Customer` to a `CustomerSummary`:

```jsonc
"Customer": { "externalBehavior": "Projection", "projectionEntity": "CustomerSummary" }
```

## Governance: the key registry

Natural keys are, in effect, part of the contract — so they are governed. Each domain
ships a `tdm.keys.json` inside its plugin output declaring the keys it keeps stable:

```jsonc
{
  "registryVersion": 1,
  "domain": "Orders",
  "entities": { "Customer": { "naturalKey": "Name", "keys": ["Acme Ltd", "Globex Corp"] } }
}
```

Every external reference is checked against the owning domain's registry — **always on, no
`--env` needed, never overridable**. If Billing references `Customer "Acme Ltd"` from
Orders and Orders' registry doesn't declare it, validation fails. This turns an informal
team agreement into a checked contract. To share the *base data* itself (so both repos
seed the same customers), use [seed packs](seed-packs.md).

## Reading the proof

The HTML report's **reference lineage graph** shows who points at whom and where each id
was resolved from — the visual proof that a Billing Invoice and an Orders Customer share
one identity. See [Reports & the manifest](../reference/reports-and-manifest.md).

## Where next

- [API seeding](api-seeding.md) — when a domain is reached only through its HTTP API.
- [Seed packs](seed-packs.md) — sharing base data so domains *mean* the same entities.
- [Testing complex domains](complex-domains.md) — the three-domain identity chain, walked
  in living code.

**Guided tour:** next stop → [API seeding](api-seeding.md)
