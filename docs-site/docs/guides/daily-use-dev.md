---
tour_prev: guides/daily-use-qa.md
tour_next: guides/editor-setup.md
---

# Daily use for developers

**Persona:** developer / domain owner. Your domain plugs into TDM as-is — the
`DbContext` and repositories you already ship — and seeds its own database through its
own model. This page is the working loop for keeping that true.

## `tdm list-entities` — your daily mirror

Conventions resolve your entities, keys, fakers and repositories; this command shows
exactly what they found:

```bash
--8<-- "daily-use-dev/01-list-entities-domain.sh"
```

```text
Domain: Orders (persistence: RepositoryFirst, profile: modern)
  entity     clr type                          key                      natural key  faker          persist route                read repo
  Customer   …CustomerEntity                   Id:Guid (Deterministic)  Name         CustomerFaker  ICustomerWriteRepository.Add …
  Order      …OrderEntity                      Id:Guid (Deterministic)  OrderNumber  auto           IOrderRepository.AddOrder    …
  Product    …ProductEntity                    Id:Guid (Deterministic)  Sku          ProductFaker   DbContext (no repository persist method) …
  ! Orders.Order: no OrderFaker found — heuristic auto-faker will be used.
  ! Orders.Product: no write repository found (probed: IProductWriteRepository, IProductRepository) — …
```

Run it after any change to entities, repositories or config. The `!` warnings are your
to-do list, and each names the exact probe patterns tried.

## Fixing resolution warnings

- **"no `{Name}Faker` found"** — the heuristic auto-faker takes over. Fine for scratch
  entities; write a faker (below) when values matter.
- **"no write repository found (probed: …)"** — TDM probed the
  [convention profile's](../reference/profiles.md) patterns
  (`I{Name}WriteRepository`, `I{Name}Repository`, …) and found nothing. Either add the
  repository, name yours explicitly (`entities.{X}.writeRepository`), or exempt the
  entity (`requireRepository: false`) — the policy exists because repository-first
  persistence exercises your production write path
  ([ADR-0001](../reference/decisions.md), in plain language: *seed through the same code
  production uses, or say out loud that you aren't*).
- **"configures X but it is not mapped in any DbContext model"** — an EF configuration
  class without a mapping; the entity is usable for generation only. Check
  `ApplyConfigurationsFromAssembly`.
- **Wrong natural key** — the profile default is `Name`; set
  `entities.{X}.naturalKey` when your business key is `OrderNumber`, `Sku`, an invoice
  number… Natural keys feed the identity contract, so get this right early.

## Fakers & generated values

A convention faker is just a Bogus `Faker<T>` named `{Name}Faker`, discovered in your
plugin assembly — no TDM types needed:

```csharp
public class CustomerFaker : Faker<CustomerEntity>
{
    public CustomerFaker()
    {
        RuleFor(c => c.Name, f => f.Company.CompanyName());
        RuleFor(c => c.Tier, f => f.PickRandom("Standard", "Silver", "Gold", "Platinum"));
        RuleFor(c => c.Email, (f, c) => f.Internet.Email(provider: "example.com"));
    }
}
```

Tips from the sample domains: use `IndexFaker` for natural keys (random-only SKUs
birthday-collide at volume, and identical natural keys derive identical ids); keep rules
deterministic — TDM seeds the faker per scenario.

When is each layer right?

| Need | Reach for |
|---|---|
| Nothing special | the auto-faker (heuristics per property name/type) |
| Domain-shaped values, in code | `{Name}Faker` |
| Distributions/weights/correlated fields, **no code** | [statistical generation](statistical-generation.md) in `entities.{X}.properties` |
| One weird property everywhere (SKU formats, check digits) | an `IValueGeneratorPlugin` in your plugin assembly — consulted before the heuristics; draw only from the supplied `Randomizer` |

Ask the pipeline which faker an entity actually uses:

```bash
--8<-- "daily-use-dev/02-explain-product.sh"
```

## The local loop

```bash
--8<-- "daily-use-dev/03-local-loop.sh"
```

`--lifecycle TrackedTeardown` seeds, proves, and cleans up after itself (the console
ends with `Teardown: N deleted, 0 orphaned`) — ideal while iterating against a dev
SQLite file or a container database. Alternatives:

- `--lifecycle Persistent` + `tdm teardown --manifest <file>` when you want to inspect
  rows between runs (the [Getting started](../start/getting-started.md) flow).
- `--lifecycle Transactional` for rollback-at-scenario-end (SQLite serialises writers —
  expect a warning if you also raised parallelism).
- Containerised SQL Server/PostgreSQL instead of SQLite → [TestContainers](testcontainers.md).

## Entity config (`entities.{X}`)

The per-entity dials, in plain language (full table:
[configuration reference](../reference/configuration.md#entities)):

- `naturalKey` — the business key; feeds the identity contract.
- `idStrategy` — `Deterministic` (contract UUIDv5) is the default when the key is a
  client-set GUID; `DbGenerated` for identity columns (rows are then matched by natural
  key instead).
- `requireRepository: false` — the explicit, auditable exemption from the
  write-repository policy.
- `externalBehavior: Projection` + `projectionEntity` — when *your* domain holds a
  read-model of someone else's entity ([multi-domain guide](multi-domain-identity.md)).
- `properties` — the no-code statistical layer.

## The manifest as a debugging tool

After any run, the manifest answers the questions a debugger would:

- **What value did that column actually get?** → `scenarios[].entities[].values` —
  final values, post-faker, post-overrides.
- **Did the override land?** → `overridesApplied`.
- **Which code path persisted it?** → `persistedVia` (`IOrderRepository.AddOrder` vs
  `DbContext`) — the fastest way to notice conventions picked the wrong route.
- **What shaped the generated values?** → `fakerSource`
  (`CustomerFaker` / `auto` / `auto+plugin:AcmeSkus+distributions`).

Schema tour: [Reports & the manifest](../reference/reports-and-manifest.md).

## Where next

- [Plugin packaging](../reference/plugin-packaging.md) — shipping your domain to other
  teams' workspaces.
- [Convention profiles](../reference/profiles.md) — when your codebase's naming differs.
- [Statistical generation](statistical-generation.md) — realistic shape without code.
- Your QA colleagues' side of the fence: [Daily use for QAs](daily-use-qa.md).

**Guided tour:** next stop → [Editor setup](editor-setup.md)
