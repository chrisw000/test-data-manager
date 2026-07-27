# ADR-0001: TDM writes through your write repositories

**Status:** Accepted · 2026-07-14
**Applies to:** domains using the `modern` convention profile
**Enforced by:** `tdm validate` / `tdm run` (policy gate, exit code 2)

## Context

The modern persistence style has evolved to (usually) two repositories per entity — one
read, one write — with no generic `IRepositoryRead<T>`/`IRepositoryWrite<T>` marker
interfaces, and entity models sometimes defined outside the conventional
`*.Data.Persistence.Domain` namespace. Two positions emerged for how TDM should bind to a
domain:

1. *"There is always an `IEntityTypeConfiguration<T>` per table — TDM should use that."*
2. *"Code external to the persistence layer must use a repository service — TDM is external
   code, so TDM must use a repository per entity."*

## Decision

Both — because they answer different questions.

**Discovery (which entities exist, keys, FKs, schema): your `IEntityTypeConfiguration<T>`
classes win — and TDM already consumes them.** TDM's entity discovery is EF-model-first:
it reads `DbContext.Model`, which *is* the compiled output of your configuration classes.
That's why models "defined elsewhere" just work — the EF model doesn't care about
namespaces (`ProductEntity` in the Orders sample lives in `Acme.Orders.Domain.Catalog` to
prove it). Parsing the configuration classes directly would mean reimplementing EF's model
builder for zero gain. We went one step further and adopted your convention as a
cross-check: a type with an `IEntityTypeConfiguration<T>` that is **not** in any context
model is almost always a missed `ApplyConfiguration`/`ApplyConfigurationsFromAssembly` —
TDM now finds those and warns (see `WarehouseEntity` in the Orders sample).

**Access (how rows are written): the repository rule wins — your own rule, applied to
TDM.** `IEntityTypeConfiguration<T>` maps schema; it cannot express what happens *on
write*: audit stamps, validation, domain events, outbox entries. Those invariants live in
your write repositories. A row seeded through a raw `DbContext` never passed through that
logic — it is not production-shaped data, and tests running against it are subtly lying.
The Orders sample makes this concrete: `CustomerWriteRepository.Add` stamps `CreatedUtc`;
seed via DbContext and every customer has `CreatedUtc = default`.

So: **every entity TDM persists in a modern-profile domain requires a write repository.**
TDM discovers them with ordered probe patterns — `I{Name}WriteRepository`, then plain
`I{Name}Repository` — no generic marker interface needed (though `IRepository<T>`,
`IWriteRepository<T>` and `IRepositoryWrite<T>` are recognised if you ever add one).
An entity whose repository defies the patterns can be pinned explicitly:
`entities.{Name}.writeRepository: "IWhateverItsCalled"`.

## Enforcement — policy as code, not runtime surprises

`tdm validate` (and `tdm run`, before touching any data) fails with exit code 2 when a
modern-profile entity has no write repository with a recognised persist method, naming the
probed interfaces and the fix. This is CI-checkable: the rule is enforced where you already
gate merges, not discovered mid-seed.

Exemptions are **visible policy, not silent fallbacks**. An aggregate child or projection
persisted via its root is declared in `tdm.settings.json`:

```jsonc
"entities": {
  "Product": { "naturalKey": "Sku", "requireRepository": false }
}
```

`tdm list-entities` shows the resolved write and read repository per entity, so the binding
is never a mystery.

## Deliberate concessions

- **Reads stay on the DbContext.** The repository rule exists to protect *write*
  invariants; verification reads (`should exist`, counts, natural-key lookups) cannot
  violate them, and read-repository shapes vary too much to duck-type reliably. TDM writes
  like your application writes, and verifies like a test asserts. Read repositories are
  still discovered and reported in `list-entities`.
- **Bulk creates go through the DbContext** (`AddRange` + chunked `SaveChanges`) for
  throughput — documented, deliberate, and confined to the explicit `N Products exist`
  bulk grammar.
- **Legacy-profile domains are unaffected**: the policy defaults off; `DbContextOnly`
  persistence remains a declared per-domain choice (and skips the policy, since the domain
  has opted out of repository routing entirely).

## Consequences

- Teams asserting "external code uses repositories" get that rule *checked*, not assumed.
- Seeded data is production-shaped: whatever the write repository does to real writes
  happens to seeded rows too.
- A new entity without a write repository fails validate in CI until the repository exists
  or the exemption is declared — the policy conversation happens in code review, where it
  belongs.
