# Troubleshooting

Every TDM warning and error, its cause, and the fix. Warnings surface in the console log,
`tdm list-entities` output, and the run manifest; policy violations fail
`validate`/`run` with exit code 2 before any data is touched.

## Resolution & policy

| Message | Cause | Fix |
|---|---|---|
| `no write repository found (probed: …) — the write-repository policy requires one` | Modern-profile entity has no write repository (ADR-0001) | Add `I{Name}WriteRepository` (or `I{Name}Repository`), or exempt deliberately: `entities.{Name}.requireRepository: false`, or pin an odd name via `entities.{Name}.writeRepository` |
| `no write repository found … — exempted from the write-repository policy` | Informational: the entity is explicitly exempted | Nothing — DbContext persistence is the declared route |
| `{Interface} exposes no recognised persist method` | Repository found, but no method matched the well-known generics or duck-typed names with a single entity-typed parameter | Add an `Add`/`AddAsync`-style method, or extend the profile's `addMethodNames` |
| `Repository {Type} could not be constructed (…)` | Repository constructor needs services TDM doesn't provide (only the DbContext is registered) | Slim the constructor, or accept the DbContext fallback (warned once) |
| `Entity '{X}' is ambiguous within domain` / `AMBIGUOUS — exists in domains: …` | Same logical name resolves twice | Qualify the step (`a Billing Customer …`) or tag the scenario `@domain:Billing` |
| `no public DbContext subclass found in assemblies` | Wrong assemblies in the plugin folder, or the context isn't public | Check `tdm list-entities`; verify the plugin folder contents |
| `{Config} configures {Entity} but it is not mapped in any DbContext model` | An `IEntityTypeConfiguration<T>` was never applied — usually a missed `ApplyConfiguration`/`ApplyConfigurationsFromAssembly` | Register the configuration in the context |
| `resolved by assembly scan … usable for generation only` | Type matched naming conventions but isn't in any context model | Map it, or ignore if generation-only is intended |
| `no {Name}Faker found — heuristic auto-faker will be used` | No convention faker | Fine for smoke data; add a `{Name}Faker : Faker<T>` for realistic values |

## Plugins & feeds

| Message | Cause | Fix |
|---|---|---|
| `Plugin folder for domain '{X}' not found` | Folder acquisition, nothing at `./plugins/{X}` | Drop the assemblies there, set `domains[].pluginPath`, or switch to NuGet acquisition |
| `no loadable assemblies in {folder}` | Folder exists but contains no non-shared .NET assemblies | Check the build output copied; host-shared assemblies are skipped by design |
| `EF version skew for domain '{X}'` | Plugin built against a different EF major than the host ships | Rebuild against the org EF baseline — see the compatibility matrix linked in the error |
| `package '{id}' … not found on any configured feed` | Wrong package id, missing feed, or auth failure | Check `plugins.feeds`; feed credentials come from nuget.config |
| `content hash mismatch for {id} {version}` | The nupkg on disk/feed doesn't match `tdm.plugins.lock.json` | Investigate (this is tamper/republish detection); if intentional, `--update-plugins` |

## Runtime

| Message | Cause | Fix |
|---|---|---|
| Step reported as **unmatched** | Text fits no grammar rule | `tdm explain "<text>"` shows the verbs; check quoting |
| `Natural key '{k}' matches multiple {Entity} rows` | Duplicate business keys in the target database | Natural keys must be unique — clean the data or pick a different `naturalKey` |
| `no foreign key or navigation to '{X}' found …` | Reference clause names an entity the dependent has no FK/nav/`{X}Id` property for | Check the reference target, or add the convention `{X}Id` column |
| `Persistence is RepositoryOnly but no repository … resolved` | Strict mode without a matching repository | Add the repository or relax to `RepositoryFirst` |
| Teardown reports **orphaned rows** | Rows couldn't be deleted (FK constraints, permissions) | Listed with reasons in the manifest — clean up manually; teardown never silently swallows failures |

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Succeeded |
| 1 | Completed with warnings (see manifest) |
| 2 | Failed — including policy violations and configuration errors |
