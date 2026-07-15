# Decision log

Full details live in the repository:
[`tdm-handoff.md`](https://github.com/chrisw000/test-data-manager/blob/main/tdm-handoff.md) (v1, D1–D14),
[`docs/adr-0001`](https://github.com/chrisw000/test-data-manager/blob/main/docs/adr-0001-data-access-via-repositories.md),
and the wave handoffs under
[`docs/`](https://github.com/chrisw000/test-data-manager/tree/main/docs).

## The ones you'll feel day-to-day

| Decision | Summary |
|---|---|
| **Identity contract** (D14) | Cross-domain ids are `UUIDv5("{domain}\|{Entity}\|{naturalKey}")` under a fixed namespace GUID. Frozen: any derivation change is a major version bump everywhere. No distributed transactions — teams agree by derivation, not coordination. |
| **Plugins, not references** (D‑series) | Domain code loads at runtime into an isolated `AssemblyLoadContext` per domain; framework/EF/`Tdm.*`/`Bogus` are host-shared so type identity unifies. |
| **EF-model-first discovery** | Entities come from the compiled `DbContext.Model` — your `IEntityTypeConfiguration<T>` classes are the source of truth; namespaces don't matter. |
| **W2-D1/W2-D2: attribution + tamper-evident manifests** | Every manifest records who/what ran it (CI job or local user), git state, and a settings-file hash, and always carries a SHA-256 checksum. Optional detached signing (`run.signing`) adds real tamper-evidence; `tdm manifest verify` checks both. See [audit-and-signing](https://github.com/chrisw000/test-data-manager/blob/main/docs/audit-and-signing.md). |
| **W2-D3/D4/D6: policy as code + key registry** | `tdm.policy.json` (`--env`-scoped) enforces lifecycle/failure-policy/row-count/connection-source/banned-entity/tag rules before persistence; violations refuse the run (exit 2) with an audited `--approval` token escape hatch. `tdm.keys.json`, shipped inside a domain's package, checks external references against the owning team's declared natural keys — always on, never overridable. See [policy-and-key-registry](https://github.com/chrisw000/test-data-manager/blob/main/docs/policy-and-key-registry.md). |
| **ADR-0001: writes go through write repositories** | `IEntityTypeConfiguration` answers *discovery*, not *access*. Write invariants (audit stamps, validation, events) live in write repositories, so TDM persists through them — "external code uses a repository service" applied to TDM itself. Enforced by the `validate` policy gate (modern profile); exemptions are explicit settings, not silent fallbacks. Reads stay on the DbContext: verification cannot violate write invariants, and read-repo shapes are too varied to duck-type. |
| **Deterministic generation** (D‑series) | Bogus fakers seeded per scenario (`@seed`); same seed → same data, cross-machine. The Bogus version is recorded per manifest because determinism can shift across majors. |
| **Manifest as the audit artifact** (D11) | Full final values, ids, routes, seeds, plugin package versions. Teardown replays it in reverse. Report formats (SARIF/JUnit) are pure projections of it (W1-D4). |
| **W1-D1** | Host ships as dotnet tool `Tdm.Tool` + container image — no installer maintenance. |
| **W1-D2** | NuGet.Protocol acquirer with a hash-pinned lockfile; auth via standard NuGet credential chain only. |
| **W1-D3** | SARIF for findings, JUnit for run results — native CI rendering, no custom formats. |
| **W1-D5** | Docs examples are the executable `features/` files run by CI — documentation cannot rot. |
| **W1-D6** | `tdm explain` reuses the real resolution pipeline verbatim — its output is guaranteed truthful. |
