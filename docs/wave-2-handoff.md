# TDM Wave 2 — Trust: Audit, Policy as Code, Key Registry: Implementation Handoff

**Status:** Design proposed, ready for review
**Audience:** Implementing engineer / AI pair
**Owner:** Chris (Engineering Manager)
**Date:** 2026-07-13
**Depends on:** Wave 1 (packaging, CI surface); v1 baseline ([`tdm-handoff.md`](../tdm-handoff.md))

---

## 1. Purpose

Make the TDM safe to point at **shared environments** used by many teams: tamper-evident
audit artifacts, machine-enforced policy at validate time, a governed registry for the
natural keys that participate in cross-domain identity, environment locking, and proper
secrets handling. This wave is what turns "a tool a team runs" into "a platform an
organisation trusts".

**Non-goals (this wave):** performance/parallelism (Wave 3); reporting UI, IDE tooling,
new generation capabilities (Wave 4).

---

## 2. Deliverables

### 2.1 Manifest audit hardening (Decisions W2-D1, W2-D2)

- New manifest `attribution` block: runner identity (CI job URL + triggering user, or local
  username), git SHA + dirty flag of the feature files' repo, SHA-256 of the settings file,
  resolved plugin package versions (from the Wave 1 lockfile), hostname, TDM/EF/Bogus
  versions (already present).
- **Manifest signing:** `run` writes `<manifest>.sha256`; optionally signs it (detached
  signature) with a key from the configured secrets provider (X.509 or KMS-backed).
  New command **`tdm manifest verify <file>`** checks integrity + signature — the audit
  artifact becomes tamper-evident.
- **Synthetic-data attestation:** every generator source is classified (`ConventionFaker`,
  `AutoFaker`, `Override`, `IdentityContract` — all synthetic by construction in v1). The
  manifest gains an `attestation` block stating no production-derived values were used;
  becomes meaningful (falsifiable) when Wave 4 explores subsetting.
- Retention: documented guidance only (manifests to artifact storage, retention per env
  class); enforcement is the org's CI, not TDM.

### 2.2 Policy as code (Decisions W2-D3, W2-D4, W2-D5)

`tdm.policy.json` (versioned schema, `$schema` published), evaluated by a new
`Tdm.Policy` library **before any persistence** — in `validate` always, and at `run` start.

```jsonc
{
  "policyVersion": 1,
  "environments": {
    "shared-dev": {
      "allowedLifecycles": ["Transactional", "TrackedTeardown"],   // no Persistent without approval
      "requireFailurePolicyAtLeast": "FailObject",
      "maxBulkRowsPerStep": 10000,
      "maxCreatedRowsPerRun": 100000,
      "connectionStringSources": ["env", "keyvault"],              // never inline
      "bannedEntities": [],
      "requiredTags": ["@seed"]
    }
  }
}
```

- The run's environment comes from a new `--env <name>` flag (also recorded in manifest and
  used by the run registry, §2.4).
- Violations are **errors**: run refuses to start, exit code 2, violations listed in a
  `policyViolations` manifest section and in the SARIF report (Wave 1 emitter extended).
- Escape hatch: a policy rule may declare `"override": "approvalToken"` — an
  `--approval <token>` flag whose value is validated against an env-provided secret; the
  override event is recorded in the manifest attribution block.
- Engine hook: policy evaluation consumes the parsed `SeedingPlan` + settings (bulk counts,
  lifecycles, tags are all statically known pre-run — no execution needed).
- OPA/Rego is deliberately **not** the v1 policy engine (JSON schema covers the concrete rule
  set with zero new runtime); an OPA adapter is an open item for orgs already invested.

### 2.3 Identity-contract key registry (Decision W2-D6)

Makes the v1 accepted constraint — "natural keys participating in cross-domain identity must
be stable and agreed between domain teams" — machine-checked.

- Each domain publishes `tdm.keys.json` **inside its data package** (so it versions and ships
  with the schema it describes):

```jsonc
{
  "registryVersion": 1,
  "domain": "Orders",
  "entities": {
    "Customer": { "naturalKey": "Name", "keys": ["Acme Ltd", "Globex Corp"], "keyPattern": null },
    "Product":  { "naturalKey": "Sku",  "keys": [], "keyPattern": "^SKU-\\d{4}-[A-Z]{2}$" }
  }
}
```

- `tdm validate` checks every **external reference** (`... from Orders`) against the owning
  domain's registry: unknown key → policy violation naming the owning team. Enumerated keys
  are exact-match; `keyPattern` covers generated key spaces.
- Registry changes ride the domain package's own versioning — removing a key is a breaking
  change for consumers, surfaced by their next `validate` against the new package.
- Central registry *service* is explicitly deferred: package-shipped files give versioning,
  review and distribution for free via the existing NuGet flow.

### 2.4 Run registry + environment locks (Decision W2-D7)

- Minimal ASP.NET Core service (`Tdm.Registry`, containerised, one small DB):
  `POST /runs` (start: env, run name, settings hash, attribution), `PATCH /runs/{id}`
  (finish: outcome, manifest URL), `POST /locks` (acquire lease on
  `(environment, domain, database)` with TTL + heartbeat), `DELETE /locks/{id}`.
- Host integration: when `registry.url` is configured, `run` acquires locks for every
  domain's target database before seeding and heartbeats during; lock conflict → clear error
  naming the holding run/team. Registry unreachable → behaviour per policy
  (`"registryUnavailable": "fail" | "warn"`).
- Gives the org the cross-team answer to "who seeded shared-dev, when, with what" — the
  registry links to manifests, it does not duplicate them.

### 2.5 Secrets providers (Decision W2-D8)

- `ISecretProvider` chain replacing today's env-var-only lookup: inline (dev only, policy
  can ban it) → environment → **Azure Key Vault** → **AWS Secrets Manager**. Configured per
  settings file (`"secrets": { "provider": "AzureKeyVault", "vaultUri": "..." }`), using
  ambient credentials (`DefaultAzureCredential` / default AWS chain) — TDM stores nothing.
- Used for connection strings, manifest-signing keys, and registry auth.

### 2.6 Manifest playback commands (Decision W2-D9)

- **`tdm replay --manifest <file>`**: re-creates exactly the rows the manifest records —
  final values, not fakers — including DB-resolved reference ids. The manifest already stores
  full final property values (v1 §11) precisely to make this possible. Replay of a manifest
  is exact even where the feature file alone isn't (DB-lookup nondeterminism).
- **`tdm verify --manifest <file>`**: asserts every created row still exists with its
  recorded values and re-runs the manifest's Load assertions → drift report, exit 0/1.
  This is the scheduled "has anyone corrupted the shared environment" job.

### 2.7 Supply-chain hygiene

- SBOM (CycloneDX) attached to every release; release artifacts signed; dependency and
  vulnerability scanning gates in the release workflow (the SQLitePCLRaw CVE pin from v1
  becomes an automated check instead of a manual catch).

---

## 3. Decisions log

| # | Decision | Rationale |
|---|---|---|
| W2-D1 | Attribution captured into the manifest, not a side file | One audit artifact; signing covers everything at once |
| W2-D2 | Detached signature + `tdm manifest verify` | Tamper-evidence without changing manifest shape for existing consumers |
| W2-D3 | Policy = versioned JSON schema, evaluated pre-persistence on the parsed plan | All current rules are statically checkable; zero new runtime dependency |
| W2-D4 | Policy violations are exit-2 errors with an audited approval-token escape hatch | Safe default, pragmatic override, everything recorded |
| W2-D5 | OPA/Rego deferred to an adapter | Don't force a policy runtime on orgs that don't run one |
| W2-D6 | Key registry ships inside each domain's data package | Versioning, review and distribution reuse the existing NuGet flow; no new service |
| W2-D7 | Run registry is a thin lease + index service linking to manifests | Locks and visibility without duplicating audit data |
| W2-D8 | Secrets via provider chain with ambient credentials | TDM never holds secrets; policy can ban weak sources |
| W2-D9 | Replay/verify consume the manifest only (no feature files needed) | The manifest is the reproducibility contract — prove it |

## 4. Phases

1. **W2-P1 — Audit:** attribution block, checksum/signing, `manifest verify`, attestation.
2. **W2-P2 — Policy:** `Tdm.Policy` + schema, `--env`, violations in manifest/SARIF, approval tokens.
3. **W2-P3 — Registry & locks:** `Tdm.Registry` service + host client + policy for unavailability.
4. **W2-P4 — Secrets + playback:** provider chain; `replay` and `verify` commands.

## 5. Acceptance criteria

- A `Persistent` run against `shared-dev` without an approval token is refused with exit 2
  and a SARIF-annotated violation.
- Two teams targeting the same database concurrently: second run fails fast naming the first.
- `tdm manifest verify` detects a single-byte edit to a signed manifest.
- `tdm replay` of a week-old manifest reproduces byte-identical rows in a fresh database.
- An external reference to a key absent from the owning domain's registry fails `validate` in CI.

## 6. Risks & mitigations

| Risk | Mitigation |
|---|---|
| Policy file sprawl / per-team drift | Org-level base policy + per-repo overlay (deep-merge, restrictive wins); schema-validated |
| Registry becomes a single point of failure for all seeding | Lease-only design, policy-controlled degradation, aggressive timeouts |
| Key registries go stale vs actual seed data | `tdm validate` also checks the *local* domain's registry against its own feature files (declared keys actually seeded) |
| Signing key management burden | KMS/Key Vault-backed keys via the same secrets chain; checksum-only mode for orgs without PKI |

## 7. Open items

- OPA adapter demand check before building.
- Whether `verify` drift results should feed the run registry as first-class events.
- Approval-token lifetime/rotation conventions (org-specific; document patterns).
