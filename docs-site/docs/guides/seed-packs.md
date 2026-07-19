---
tour_prev: guides/api-seeding.md
tour_next: guides/testcontainers.md
---

# Seed packs

**Personas:** developer, platform. A seed pack is a versioned, reusable package of feature
files, entity-config fragments and key-registry entries — so teams stop copy-pasting base
data. "EU reference customers v2" becomes a dependency, not a snippet, and two repos that
depend on it seed the *same* customers, agreeing by the [identity contract](multi-domain-identity.md).

## Consuming a pack

Reference it in `tdm.settings.json` — a NuGet package (riding the plugin feeds) or a local
folder for development:

```jsonc
"seedPacks": [
  { "package": "Acme.SeedPacks.EuCustomers", "version": "2.*" },
  { "path": "../shared-packs/eu-customers" }   // local wins over package when both given
]
```

- **Order** — packs run **before** local features (pack list order, alphabetical within),
  so shared base data exists before your scenarios reference it.
- **Pinning** — NuGet packs resolve through the feeds and pin in `tdm.plugins.lock.json`
  (commit it); `tdm run --update-plugins` re-resolves.
- **The manifest** records every applied pack and its resolved version under
  `run.seedPacks`, so a run's provenance names the shared data it stood on.

## Authoring a pack

A pack is a folder (packed as NuGet) with a fixed layout:

```text
features/*.feature      # the shared scenarios
tdm.entities.json       # optional entity-config fragment (naturalKey, properties, …)
tdm.keys.json           # optional key-registry entries the pack owns
datasets/*.csv          # optional correlated datasets the features use
```

Versioning discipline — **packs are contracts**:

- **Semver.** A pack's natural keys are part of the identity contract; changing or
  removing a key changes ids downstream. Add keys in minor versions; treat removals/renames
  as breaking (major).
- **Review the key registry** like an API surface — every key in `tdm.keys.json` is a
  promise other repos derive ids from.
- **Publish** to the same feed as your domain plugins; consumers pin and lock.

This is what makes the two-repos-same-identities guarantee hold: both depend on
`EuCustomers 2.x`, both get the same keys, both derive the same ids — without ever
coordinating at runtime.

## Anti-patterns (and the loud failures that catch them)

- **A pack that fights local config.** Local `entities.{X}` overrides a pack's fragment;
  if that is a surprise, you have two sources of truth for one entity. Keep pack fragments
  authoritative for the entities the pack owns, and don't shadow them locally.
- **Two packs claiming one entity.** Conflicting `naturalKey`/config for the same entity
  from two packs is ambiguous — TDM fails loudly rather than silently picking one. Split
  ownership so each entity is configured by exactly one pack.
- **Unversioned "just copy this folder" sharing.** That is the copy-paste economy seed
  packs exist to kill — the moment two copies drift, their ids can diverge.

!!! info "Engineering record"
    [seed-packs.md](https://github.com/chrisw000/test-data-manager/blob/main/docs/seed-packs.md).

## Where next

- [Multi-domain identity alignment](multi-domain-identity.md) — why shared keys must agree.
- [TestContainers & the provider matrix](testcontainers.md) — running shared packs against
  real database engines.
- [Configuration → seedPacks](../reference/configuration.md#seedpacks).

**Guided tour:** next stop → [TestContainers & the provider matrix](testcontainers.md)
