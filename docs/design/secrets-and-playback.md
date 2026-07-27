# Secrets providers and manifest playback (W2-P4)

The final Wave 2 phase: how TDM resolves secrets (W2-D8), and the two commands that prove
the manifest is a reproducibility contract (W2-D9).

## Secrets (`ISecretProvider` chain)

Secret lookups — connection strings named via `connectionStringName`, the manifest-signing
certificate password, the registry API key — resolve through a provider chain:

**inline** (dev only, when configured) → **environment** → *optionally one named adapter*.

```jsonc
// tdm.settings.json
"secrets": {
  "provider": "Environment",            // default; or a host-registered adapter name
  // "vaultUri": "https://acme.vault.azure.net/",   // passed through to the adapter
  "inline": {                           // development only — tried first when present
    "OrdersDb": "Data Source=./output/orders.db"
  }
}
```

Connection-string names try three spellings in order, so existing environment conventions
keep working unchanged: `TDM_CONNECTIONSTRINGS__{NAME}`, `ConnectionStrings__{Name}`, then
the bare name (which is how `secrets.inline` and vault entries are usually keyed).

**Cloud adapters are an extension point, not a dependency.** TDM ships no cloud SDKs.
Naming `"provider": "AzureKeyVault"` (or anything else) requires the embedding host to
supply a matching implementation of `Tdm.Core.Secrets.ISecretProvider` to
`SecretChainFactory.Create` — using ambient credentials (`DefaultAzureCredential`, the
default AWS chain); TDM stores nothing and fails fast with guidance when the adapter is
missing. Policy note: `secrets.inline` is a development convenience — for shared
environments, `tdm.policy.json`'s `connectionStringSources` can require `env`-declared
connection strings.

## `tdm replay` — reproduce a manifest exactly

```bash
tdm replay --manifest ./output/run.tdm.json [--settings tdm.settings.json]
```

Re-creates exactly the rows the manifest records — **final values, not fakers** — including
DB-resolved reference ids (FK columns are part of each row's recorded value snapshot). No
feature files needed; replay of a manifest is exact even where the feature file alone isn't
(database-lookup nondeterminism).

- Entries play back in their original order: creates, projections, updates and
  single-row deletes. Idempotent: an existing row (same id/natural key) has the recorded
  values re-applied rather than colliding.
- Only **Persistent** scenarios replay — Transactional/TrackedTeardown scenarios
  deliberately left no rows behind.
- Delete-all steps record only a count, not the affected ids — those deletions can't be
  replayed exactly and are reported as warnings.
- Exit codes: 0 clean, 1 warnings, 2 failures.

## `tdm verify` — the drift check

```bash
tdm verify --manifest ./output/run.tdm.json
```

Asserts every manifest-recorded row still exists with its recorded values, and that rows
the run deleted stayed deleted — the scheduled *"has anyone corrupted the shared
environment"* job. (File integrity — checksum/signature — is the separate
`tdm manifest verify`.)

- The manifest folds to an expected end state first: the last write to a row wins, single
  deletes become tombstones, delete-alls make that entity type unverifiable (warned, never
  false-flagged).
- Value comparison is exact (the same stringification that recorded the values), with one
  normalisation: UTC timestamps recorded from in-memory instances carry a `Z` kind marker
  that databases don't round-trip — identical instants are not drift.
- Exit codes: 0 no drift, 1 drift (each finding names the row, property, recorded and
  actual value), 2 errors.

Typical CI shape: a scheduled job runs `tdm verify` against the environment's last-known
manifest and pages the owning team on exit 1; `tdm replay` restores the environment from
that same manifest when drift is confirmed.
