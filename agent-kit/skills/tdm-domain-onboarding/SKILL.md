---
name: tdm-domain-onboarding
description: >-
  Wire a new domain (its EF Core DbContext + repositories) into TDM so its entities are
  seedable. Use when asked to "add a domain to TDM", "make <domain> seedable", "onboard a
  new plugin", or when `tdm list-entities` shows unresolved entities for a domain.
---

# Onboarding a domain into TDM

A domain ships its existing EF Core `DbContext` + repositories as a **plugin**; TDM's
conventions resolve entities, keys, fakers and repositories with (ideally) zero TDM code.
The job is to get a clean `tdm list-entities` and a first passing feature.

## Loop

1. **Register the domain** in `tdm.settings.json` (`domains[]`): `name`, the plugin
   `package`/`pluginPath`, `provider`, `connectionString`, `conventionProfile`
   (`modern`/`legacy`), and `persistence`
   (`RepositoryFirst`/`DbContextOnly`/`RepositoryOnly`).
2. **Run `tdm list-entities --domain <D>`** — the resolution map. Every warning here is a
   thing to fix *before* authoring features. Read it as a checklist:
   - **No natural key** → declare `entities.<E>.naturalKey` (the business key, e.g. `Sku`,
     `OrderNumber`). This drives identity — pick deliberately.
   - **No write repository** → either the convention isn't finding it (fix naming /
     profile) or the entity legitimately has none → `requireRepository: false` (an ADR-0001
     exemption; document why).
   - **No faker** → conventions synthesise one for simple types; declare
     `entities.<E>.properties` for shaped/correlated fields.
   - **Server-assigned key** → `idStrategy: DbGenerated`; persist via `DbContext` and
     reference the row by its natural key, never the surrogate.
3. **Iterate config until `list-entities` is clean** for the entities you need. Resolution
   warnings are cheaper to fix than run failures.
4. **`tdm export-model`** to regenerate `tdm.model.json` (the schema oracle editors and
   agents read). Commit it; CI's drift check keeps it honest.
5. **Author one feature** (hand to `tdm-feature-author`) and reach green `tdm validate`.
   That proves the wiring end to end.

## Convention gotchas

- **Profiles:** `modern` and `legacy` differ in naming expectations (e.g. `Id` vs
  `<Entity>Id`, repository suffixes). If everything resolves as "not found," you likely
  have the wrong profile — switch and re-list before writing custom config.
- **Self-referencing / cross-entity FKs** are ordinary references; create principals
  before dependents (`Background`). No special config.
- **External references:** if the domain cites another domain's entity, set the domain's
  `externalReferences` behaviour (`Synthesize`/`Verify`/`Trust`) rather than creating a
  fake local row.
- **Projections:** if the domain holds a read-model of another's entity, use
  `externalBehavior: Projection` — not a duplicate seed.

## Definition of done

`tdm list-entities --domain <D>` clean (or every remaining warning consciously configured
away), `tdm.model.json` regenerated and committed, and one feature at green `tdm validate`.

Depth: <https://chrisw000.github.io/test-data-manager/guides/daily-use-dev/>,
[multi-domain identity](https://chrisw000.github.io/test-data-manager/guides/multi-domain-identity/),
and [complex domains](https://chrisw000.github.io/test-data-manager/guides/complex-domains/).
