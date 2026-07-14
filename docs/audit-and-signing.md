# Manifest audit hardening (W2-P1)

Part of [Wave 2](wave-2-handoff.md): makes the run manifest safe to treat as evidence, not
just a log. Three additions, all opt-in beyond the checksum which is always written.

## Attribution

Every manifest's `run.attribution` block records who/what produced it:

```jsonc
"attribution": {
  "runnerId": "github-actions:https://github.com/org/repo/actions/runs/12345#alice",
  "hostname": "gh-runner-abc123",
  "gitSha": "f4861d44f28c5231ea2927a329051457157d011d",
  "gitDirty": false,
  "settingsFileSha256": "5971c635485e69d733826c33fbe8e06fc95be7975b5a08d6ba27f6ec0ea1bf5"
}
```

- **runnerId**: `github-actions:{server}/{repo}/actions/runs/{id}#{actor}` when
  `GITHUB_ACTIONS=true`; a generic `ci:{jobUrl}#{actor}` when a bare `CI` env var is set
  (covers most other CI systems without provider-specific parsing); otherwise
  `local:{username}`.
- **gitSha** / **gitDirty**: HEAD commit and dirty-tree flag of the repository containing
  `tdm.settings.json`, via `git`. Both null if `git` isn't on PATH or the directory isn't a
  repository — attribution degrades gracefully rather than failing the run.
- **settingsFileSha256**: SHA-256 of the settings file *as loaded* — tamper-evidence for the
  configuration, not just the seeded data.

Collected by `Tdm.Observability.Audit.AttributionCollector` — a host concern; `Tdm.Core`
itself never touches the filesystem or environment beyond what settings already require.

## Checksum and signing

`run` and `validate` always write `<manifest>.sha256` next to the manifest
(sha256sum-compatible: `"{hex}  {filename}"`). This catches accidental corruption but proves
nothing about *who* wrote it — anyone can regenerate a checksum.

For real tamper-evidence, configure detached-signature signing:

```jsonc
"run": {
  "signing": {
    "certificatePath": "./keys/tdm-signing.pfx",
    "certificatePasswordEnv": "TDM_SIGNING_CERT_PASSWORD"
  }
}
```

The private key never touches disk-backed key storage (loaded with
`X509KeyStorageFlags.EphemeralKeySet`), so this works unmodified in containers and CI agents
with no writable user profile. Without `run.signing` configured, TDM operates in
**checksum-only mode** — a documented, acceptable posture for orgs without PKI (see the risk
table in [wave-2-handoff.md](wave-2-handoff.md)).

## `tdm manifest verify`

```bash
tdm manifest verify ./output/run.tdm.json                  # checksum only
tdm manifest verify ./output/run.tdm.json --cert pub.cer   # + signature, if present
```

| Outcome | Exit | Meaning |
|---|---|---|
| Verified | 0 | Checksum OK, and signature OK (or absent — checksum-only mode) |
| Partially verified | 1 | Checksum OK, but a signature file exists and no `--cert` was given |
| Tampered / Error | 2 | Checksum mismatch, signature verification failed, or the manifest/checksum file is missing |

`--cert` takes the **public** certificate (`.cer`/`.pem`) exported alongside the private
signing key — distribute it to whoever needs to verify, never the `.pfx`.

## Synthetic-data attestation

`run.attestation` classifies every generator source that contributed to the run:

```jsonc
"attestation": {
  "syntheticOnly": true,
  "sources": ["AutoFaker", "ConventionFaker", "IdentityContract", "Override"]
}
```

`syntheticOnly` is `true` by construction in v1 — every source (convention fakers, the
heuristic auto-faker, explicit step overrides, identity-contract-derived ids) is synthetic.
This becomes a real, falsifiable claim once Wave 4 explores subsetting from production data;
until then it's a standing statement the manifest makes about itself.

## Retention (guidance, not enforcement)

TDM does not manage manifest retention — that's your CI/artifact-storage policy. Suggested
starting point: upload `<manifest>`, `<manifest>.sha256` and `<manifest>.sig` (if present) as
build artifacts; retain longer for environments closer to production (e.g. 30 days for
`shared-dev`, a year or more for anything prod-adjacent). The manifest's `attribution` block
gives you enough to answer "who ran this, against what config, from what commit" without any
TDM-side storage.
