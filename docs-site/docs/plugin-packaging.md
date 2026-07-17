# Plugin packaging guide (for domain teams)

TDM never compiles against your domain code. Your data layer is loaded at runtime as a
**plugin**: an isolated, collectible `AssemblyLoadContext` per domain, so two domains'
transitive dependencies can never clash.

## What a domain package must contain

Your existing data-layer assembly is usually already enough:

- a **public `DbContext`** with your entities mapped (via `IEntityTypeConfiguration<T>`
  or `OnModelCreating` — TDM reads the compiled model either way);
- your **write repositories** — under the `modern` profile every persistable entity needs
  one (`I{Name}WriteRepository` or `I{Name}Repository`), because TDM writes through them so
  seeded rows carry your audit/validation behaviour (ADR-0001);
- optionally **fakers** (`{Name}Faker`, a Bogus `Faker<T>`) for realistic generated values —
  entities without one get a deterministic heuristic auto-faker.

The `DbContext` must be constructible by TDM: a constructor taking
`DbContextOptions<TContext>` (or a parameterless one).

## Rules

1. **EF baseline**: build against the same `Microsoft.EntityFrameworkCore` major/minor as
   the TDM host ships (see the
   [compatibility matrix](https://github.com/chrisw000/test-data-manager/blob/main/docs/compatibility.md)).
   Version skew fails fast at plugin load, naming both versions.
2. **Don't pack host-shared dependencies**: framework assemblies, EF Core,
   `Microsoft.Extensions.*`, `Microsoft.Data.*`, `SQLitePCLRaw`, `Bogus` and `Tdm.*` are
   provided by the host and are excluded from feed restore automatically. Everything else
   your assembly needs must be a declared NuGet dependency (feed acquisition brings
   transitive dependencies along). One exception: `Tdm.Providers.*` provider packages
   (e.g. `Tdm.Providers.PostgreSql` for `"provider": "PostgreSql"`) are **not**
   host-provided — declare the one your domain targets as a dependency and it loads from
   your plugin folder
   ([providers](https://github.com/chrisw000/test-data-manager/blob/main/docs/providers.md)).
3. **Natural keys are contract**: the property TDM uses as the business key
   (`entities.{Name}.naturalKey`) feeds the cross-team identity contract
   `UUIDv5("{domain}|{Entity}|{naturalKey}")`. Renaming it is a breaking change for every
   team that references your entities.

## Publishing

Pack your data project as an ordinary NuGet package (`dotnet pack`) and push it to the
internal feed. Consumers reference it in `tdm.settings.json`:

```jsonc
"plugins": { "acquisition": "NuGet", "feeds": [{ "url": "https://nuget.acme.internal/v3/index.json" }] },
"domains": [{ "name": "Orders", "package": "Acme.Orders.Data.Persistence", "packageVersion": "3.2.*" }]
```

The consumer's `tdm.plugins.lock.json` pins the exact resolved version + content hash;
their manifests record it (`run.pluginPackages`), so any seeding run is attributable to an
exact package version of your domain.

## Checking your package works

```bash
tdm list-entities --domain Orders   # every entity resolved? write repo bound? faker found?
tdm explain 'a Customer exists with name "X"'
tdm validate                        # includes the write-repository policy gate
```

Common warnings and what they mean: [Troubleshooting](troubleshooting.md).
