# Seed packs (W4-D7)

Shared base data as a **versioned dependency, not a snippet**: a seed pack is a NuGet
package (or local folder) of feature files + entity-config fragments + key-registry
entries, executed before your local features.

```jsonc
"seedPacks": [
  { "package": "Acme.SeedPacks.EuReferenceCustomers", "version": "2.1.0" },
  { "path": "../shared-packs/smoke-data" }              // dev / CI-restored folder
]
```

## Pack layout

```
features/            *.feature files, run before local features
tdm.entities.json    optional config fragment: { "entities": { … }, "datasets": { … } }
tdm.keys.json        optional W2-D6 key registry the pack's data satisfies
datasets/            optional CSVs referenced by the fragment's datasets
```

In a .nupkg the same files live under `content/` (or `contentFiles/any/any/`, or the
package root).

## Rules

- **Ordering is deterministic** (§5 acceptance): pack features run first, in `seedPacks`
  list order, alphabetical within each pack — then local features. Combined with the
  identity contract, two repos consuming the same pack version produce identical
  identities.
- **Config merges under local settings**: a pack's `entities.{X}` / `datasets.{X}` entries
  apply only where the consuming repo hasn't configured that key — local always wins. Two
  *packs* configuring the same key fail loudly (§6 risk table). Pack dataset paths anchor
  at the pack root.
- **Key registries**: a pack may publish `tdm.keys.json` for a domain (e.g. the reference
  customers it seeds); the domain's own plugin-published registry stays authoritative, and
  two packs publishing for one domain fail loudly.
- **Acquisition rides the plugin flow** (W4-D7): NuGet packs resolve from `plugins.feeds`,
  pin their exact version + content hash in a `packs` section of `tdm.plugins.lock.json`
  (refresh with `--update-plugins`), and extract to `./packs/{id}`. Resolved versions are
  recorded in the run manifest next to plugin packages.

Open item (per the wave-4 handoff): packs carrying compiled generator plugins — proposed to
follow the same assembly-loading rules as domain plugins, not yet implemented.
