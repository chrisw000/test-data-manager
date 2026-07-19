---
tour_prev: guides/testcontainers.md
---

# Testing complex domains

**Personas:** QA, developer. The sample Orders and Billing domains are deliberately tidy.
Real domains have self-referencing hierarchies, server-assigned keys, enum state machines,
date/time columns, correlated fields, and references across team boundaries. **Acme.Fulfilment**
exists to have all of them — CI builds and seeds it on the SQLite leg, so this guide can
only describe features that actually run. Everything below is one CI-executed feature
(`fulfilment.feature`) walked case by case.

```bash
--8<-- "complex-domains/01-run-fulfilment.sh"
```

## Self-referencing hierarchy

A warehouse is a tree: Site → Aisle → Bin. `LocationEntity` has a nullable self-FK
(`ParentId`), and a step references its parent by natural key — exercised by
**`A shipment for an externally-owned order`**, whose `Background` builds the chain:

```gherkin
Given a Location exists with code "SITE-1" and name "Primary Site" and kind "Site"
And a Location exists with code "AISLE-A" and name "Aisle A" and kind "Aisle" for Location "SITE-1"
And a Location exists with code "BIN-A1" and name "Bin A1" and kind "Bin" for Location "AISLE-A"
```

**Config:** nothing special — the self-reference is just a `HasOne(l => l.Parent)` in the
`DbContext`, and `for Location "SITE-1"` resolves to the parent's deterministic id.
**Take-away:** a self-FK is an ordinary reference; parents must be created before children
(a `Background` is the natural place).

## Server-assigned `long` key + enum state flow

`ShipmentEntity.Id` is a database-assigned `long`. In config the entity declares
`idStrategy: DbGenerated` and is exempt from the write-repository policy so it persists via
`DbContext` (which captures the assigned key). Its `Status` is a seven-value enum, shaped
by weights — exercised by **`A bulk of shipments with realistic status, carrier and delivery windows`**:

```jsonc
"Shipment": {
  "naturalKey": "ShipmentNumber",
  "idStrategy": "DbGenerated",
  "requireRepository": false,
  "properties": {
    "Status": { "weights": { "Delivered": 0.5, "InTransit": 0.25, "Dispatched": 0.15, "Exception": 0.1 } }
  }
}
```

**Manifest evidence** — the DB assigned the key, and TDM recorded it:

```jsonc
"id": "1", "idStrategy": "DbGenerated", "naturalKey": "SHP-00001", "persistedVia": "DbContext"
```

**Take-away:** for server-assigned keys, let the DB assign and TDM record; reference such
rows by their natural key, never the surrogate.

## `DateOnly` / `TimeOnly` and a correlated dataset

Delivery windows use `DateOnly` (the date) and two `TimeOnly` columns. Carrier and service
level must **agree** (RoyalMail·Tracked24, DPD·NextDay, …), so they come from one sampled
row of a correlated dataset:

```jsonc
"Carrier":      { "dataset": "carriers", "column": "Carrier" },
"ServiceLevel": { "dataset": "carriers", "column": "ServiceLevel" }
```

**Manifest evidence** — a coherent row: the window is a real date + two times, and the
carrier/service pair is one the CSV actually contains:

```jsonc
"DeliveryDate": "2026-07-29", "WindowStart": "14:00", "WindowEnd": "16:00",
"Carrier": "Evri", "ServiceLevel": "Standard",
"fakerSource": "ShipmentFaker+distributions+datasets"
```

**Take-away:** generate correlated fields from a dataset, not independently — otherwise you
get impossible combinations. `DateOnly`/`TimeOnly` are first-class; a small faker fills the
window while the statistical layer handles the shaped columns.

## The three-domain identity chain

Fulfilment ships an Orders order that Billing invoices. The Shipment references the Orders
`Order` by number — a third domain joining the chain with **no shared transaction**:

```gherkin
Given an external Order reference "ORD-1001" from Orders
And a Shipment exists for Order "ORD-1001" for Location "BIN-A1" with shipment number "SHP-00001" and status "Dispatched"
```

**Manifest evidence** — the Shipment's `OrderId` is the identity-contract id of the Orders
order, derived independently:

```jsonc
"OrderId": "a8eae15f-913e-5e14-b95a-735a8c3fc9c5"   // = UUIDv5("Orders|Order|ORD-1001")
```

That is the exact id the Orders domain writes for `ORD-1001` — confirm it yourself in the
[identity explorer](multi-domain-identity.md#the-identity-contract). **Take-away:** a
domain three hops from the data's origin still agrees on identity, because the contract
needs only the name.

## Mapping your own domain

A checklist when onboarding a domain with these shapes:

- **Keys** — pick the natural key per entity (`naturalKey`); use `DbGenerated` for
  server-assigned ids and reference those rows by their business key.
- **Navigations** — self-FKs and cross-entity FKs are ordinary references; order creation
  so principals exist first (`Background`).
- **Projections** — if your domain holds a read-model of another's entity, set
  `externalBehavior: Projection` ([multi-domain guide](multi-domain-identity.md)).
- **Correlated fields** — a dataset per group of fields that must agree.
- **API constraints** — if writes only go through the API, see
  [API seeding](api-seeding.md); remember `Transactional` isn't available there.

## Where next

- [Multi-domain identity alignment](multi-domain-identity.md) — the contract this domain's
  chain relies on.
- [Statistical generation](statistical-generation.md) — the weights and dataset config
  used above.
- Back to [the guided tour](../start/tour.md) — you've reached the last stop.

**Guided tour:** this is the final stop — back to [the tour index](../start/tour.md).
