# Policy as code and the natural-key registry (W2-P2)

Part of [Wave 2](wave-2-handoff.md): two independent, validate-time governance checks that
run before any persistence. Both are statically evaluated against the parsed feature plan —
no database connection, no execution.

## Environment policy (`tdm.policy.json`)

Opt-in per invocation via `--env <name>`. Without `--env`, nothing changes — existing
settings files and pipelines are unaffected.

```jsonc
// tdm.policy.json — see docs/schemas/tdm.policy.schema.json
{
  "policyVersion": 1,
  "environments": {
    "shared-dev": {
      "allowedLifecycles": ["Transactional", "TrackedTeardown"],
      "requireFailurePolicyAtLeast": "FailObject",
      "maxBulkRowsPerStep": 10000,
      "maxCreatedRowsPerRun": 100000,
      "connectionStringSources": ["env"],
      "bannedEntities": ["PaymentCard"],
      "requiredTags": ["@seed"],
      "override": { "kind": "approvalToken", "tokenEnv": "TDM_APPROVAL_TOKEN" }
    }
  }
}
```

```bash
tdm validate --env shared-dev                                    # --policy-file defaults to ./tdm.policy.json
tdm run --env shared-dev --policy-file infra/tdm.policy.json
tdm run --env shared-dev --approval "$(cat approval.token)"       # bypasses violations, records the event
```

Every rule is optional — set only what you need. Rules are checked against what's
**statically knowable from the parsed plan**: `run.lifecycle` and any scenario's
`@persistent`/`@ephemeral` override, `run.failurePolicy`'s strictness
(`BestEffort` < `FailObject` < `FailRun`), row counts per bulk-create step and summed across
the run, each domain's connection-string source (`inline` = `connectionString` set, `env` =
`connectionStringName` set), entities targeted anywhere in the plan, and scenario tags
(`"@seed"` matches an exact `@seed` tag or a `@seed:`-prefixed one, e.g. `@seed:42`).

**Violations refuse the run** (exit 2) — logged, written into `run.policyViolations` in the
manifest (even though nothing ran), emitted as SARIF `TDM0004` results, checksummed/signed
like any other manifest. `--env` given with no policy file present is itself an error, not a
silent no-op — you asked for enforcement and there's nothing to enforce against.

**Override**: a per-environment `override` declares an approval-token escape hatch. A
matching `--approval <token>` (checked against the named environment variable — TDM never
stores the expected value) bypasses violations for that environment. The bypass is never
silent: the run's manifest still records exactly which violations were overridden
(`run.policyOverrideApplied: true` + the full `policyViolations` list), so the audit trail
shows what happened even when the run proceeded.

`--env` and `--policy-file` are CLI flags, not `tdm.settings.json` keys — the same settings
file can be validated against different environments without editing it.

## Natural-key registry (`tdm.keys.json`)

Makes the v1 accepted constraint — "natural keys participating in cross-domain identity must
be stable and agreed between domain teams" — machine-checked, and **always on** (no `--env`
needed) whenever the referenced domain has published one.

Each domain ships `tdm.keys.json` alongside its plugin's build output (for NuGet
acquisition: packaged in `lib/{tfm}` next to the DLLs; for folder acquisition: copied to the
output directory like any other build artifact):

```jsonc
{
  "registryVersion": 1,
  "domain": "Orders",
  "entities": {
    "Customer": { "naturalKey": "Name", "keys": ["Acme Ltd", "Globex Corp"] },
    "Product":  { "naturalKey": "Sku",  "keys": [], "keyPattern": "^SKU-\\d{4}-[A-Z]{2}$" }
  }
}
```

`tdm validate`/`tdm run` check every `... from {Domain}` external reference against that
domain's registry: an unknown key is a violation naming the owning team, so a typo or a
genuinely stale reference fails CI immediately rather than seeding a row against an id no
one recognises. Keys are exact-match; `keyPattern` covers a generated key space too large to
enumerate.

**Adoption is incremental**: an entity absent from the registry is *not governed* — no
violation. A domain with no `tdm.keys.json` at all is fully unchecked. This is deliberate —
teams can publish registries entity-by-entity without a big-bang migration.

**Never overridable.** Unlike environment-policy rules, a key-registry mismatch is a
data-integrity contract between teams, not an environment-safety guard — there is no
approval-token bypass for it.

A **central registry service is explicitly deferred** (open item): package-shipped files
give versioning, code review and distribution for free via the existing NuGet flow, and a
registry entry going stale is caught by the *consuming* team's next `validate` against the
new package version — the same mechanism that catches any other breaking change.
