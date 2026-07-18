---
tour_next: start/concepts.md
---

# Getting started

**Personas:** all · **Time:** ≤15 minutes to a green `tdm validate` and a seeded demo run.

This page is the proof guide: every command below is an executable snippet that CI runs
against this repository's sample workspace on every push — if a checkpoint here breaks,
the build breaks first.

## 1 · Install

```bash
dotnet tool install --global Tdm.Tool
```

Or work inside this repository — clone it, build once, and every `tdm` command below is
`dotnet run --project src/Tdm.Host --no-build -- <command>`:

```bash
git clone https://github.com/chrisw000/test-data-manager && cd test-data-manager
dotnet build
```

The rest of this guide uses the repository route: the sample workspace
(`tdm.settings.json` + two sample domains, Orders and Billing, on SQLite) is already
here, so you meet a realistic multi-domain setup immediately.

## 2 · Scaffold a workspace

In your own repo, `tdm init` is the starting point. Try it here in a scratch folder:

```bash
--8<-- "getting-started/01-init.sh"
```

Look inside `demo-init/`: an annotated `tdm.settings.json`, a starter feature, a
`.gitignore`, and a CI workflow (`.github/workflows/tdm-validate.yml`). Nothing is
overwritten if already present. Delete the folder when you've had a look.

**Checkpoint:** `demo-init/tdm.settings.json` exists and every setting has a comment
explaining it.

## 3 · Meet convention resolution

TDM finds your entities, keys, fakers and repositories by convention — no TDM code in
the domain. Ask what resolved:

```bash
--8<-- "getting-started/02-list-entities.sh"
```

Expected output (timestamps and load logs trimmed):

```text
Domain: Orders (persistence: RepositoryFirst, profile: modern)
  entity     clr type                                            key                     natural key  faker         persist route             read repo
  Customer   Acme.Orders.Data.Persistence.Domain.CustomerEntity  Id:Guid (Deterministic) Name         CustomerFaker ICustomerWriteRepository.Add  ICustomerReadRepository → CustomerReadRepository
  Order      Acme.Orders.Data.Persistence.Domain.OrderEntity     Id:Guid (Deterministic) OrderNumber  auto          IOrderRepository.AddOrder     IOrderRepository → OrderRepository
  Product    Acme.Orders.Domain.Catalog.ProductEntity            Id:Guid (Deterministic) Sku          ProductFaker  DbContext (no repository persist method)  IProductReadRepository → ProductReadRepository
  ...
Domain: Billing (persistence: DbContextOnly, profile: legacy)
  ...
```

Each `!` warning under a domain is a convention gap (a missing faker, a missing write
repository) with the exact probe names tried — the resolution is never a mystery.

**Checkpoint:** you can see both domains, and for each entity its CLR type, key
strategy, natural key, faker and persistence route.

## 4 · Write a feature and ask TDM to explain it

Save this as `features/getting-started.feature`:

```gherkin
--8<-- "getting-started/first-feature.feature"
```

Three steps: **create** a customer, create an order that **references** it by natural
key, **verify** what exists. Now ask the pipeline what it will do with the middle step —
no database involved:

```bash
--8<-- "getting-started/04-explain.sh"
```

Expected output (plugin load logs trimmed):

```text
Step        : Given an Order exists for Customer "Bluebird Books" with order number "ORD-GS-1" and status "Pending"
Grammar     : Create — entity "Order", count 1
  overrides : order number = "ORD-GS-1", status = "Pending"
  references: Customer "Bluebird Books"
Resolution  : Orders.Order → Acme.Orders.Data.Persistence.Domain.OrderEntity
  natural key : OrderNumber
  key         : Id:Guid (Deterministic)
  faker       : auto
  persist via : IOrderRepository.AddOrder
  read repo   : IOrderRepository → OrderRepository
Identity    : Orders|Order|ORD-GS-1
  uuid v5     : f27689f8-22e7-5df9-9d0d-1ec9c74de46e
```

That last pair of lines is the [identity contract](concepts.md#the-identity-contract) at
work: the step names the natural key (`order number "ORD-GS-1"`), so the row's id is
already derivable — any other domain referencing this order computes the same GUID.

**Checkpoint:** `tdm explain` shows the grammar match, the entity resolution, the faker
and the persistence route for your step. When a step ever surprises you, this is the
first command to reach for.

## 5 · Validate — the no-database gate

```bash
--8<-- "getting-started/05-validate.sh"
```

Expected: `Validating 3 feature file(s), 10 scenario(s)` … `finished: Succeeded`, exit
code `0`. Validate parses every feature, resolves every entity, and runs the policy
gates — but **persists nothing**. This is the command your CI runs on every PR (with
`--report sarif=…` for inline annotations — see the [CI guide](../guides/ci.md)).

**Checkpoint:** exit code 0. Break a step on purpose (misspell an entity) and validate
tells you exactly what didn't resolve — before any data exists.

## 6 · Run — seed, then read the evidence

```bash
--8<-- "getting-started/06-run.sh"
```

The console summary lists every scenario with its seed and row counts, then a benchmark
table. Two artifacts land in `./output/`:

- **The manifest** (`sample-domains-demo-<stamp>.tdm.json`) — full final values, seeds,
  persistence routes and ids for every row. This is the reproducibility and audit
  artifact; [Reports & the manifest](../reference/reports-and-manifest.md) tours it.
- **The HTML report** (`getting-started-report.html`) — the same evidence as a single
  self-contained file: run header, scenario drill-down, reference lineage, benchmark
  charts. Open it in a browser — it works from `file://` with zero network access.

Done with the demo rows? Teardown reverses the latest manifest, deleting in reverse
dependency order:

```bash
--8<-- "getting-started/07-teardown.sh"
```

**Checkpoint:** a manifest and an HTML report exist in `./output/`, and the report opens
offline. You have gone from clone to seeded, audited, torn-down data.

## Where next

Route by persona:

- **QA / test author** → [Daily use for QAs](../guides/daily-use-qa.md), then the
  [grammar reference](../reference/grammar.md) for every verb.
- **Developer / domain owner** → [Daily use for developers](../guides/daily-use-dev.md)
  and [convention profiles](../reference/profiles.md).
- **Platform / DevEx** → [CI — validate, report, gate](../guides/ci.md), then
  [CD & environments](../guides/cd-environments.md).
- Want the mental model first? → [Concepts](concepts.md) walks one step through the
  whole pipeline, interactively.

**Guided tour:** next stop → [Concepts](concepts.md)
