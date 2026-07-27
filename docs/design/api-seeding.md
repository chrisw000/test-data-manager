# API-based seeding (W4-D6)

For domains that forbid direct database writes, `"persistence": "Api"` routes persistence
through the domain's **public HTTP API** — which also exercises its validation, auth and
side-effects for free. The engine does not change at all: `ApiDomainRuntime` is just
another `IDomainRuntime` behind the v1 seam — the abstraction earning its keep.

```jsonc
{
  "name": "Orders",
  "package": "Acme.Orders.Data.Persistence",     // same plugin-loaded CLR types
  "persistence": "Api",
  "api": {
    "baseUrl": "https://orders.acme.internal",
    "auth": { "tokenSecret": "ORDERS_API_TOKEN" },          // W2-D8 secret chain
    "entities": {
      "Customer": {
        "create":   "POST /api/customers",
        "update":   "PUT /api/customers/{id}",
        "delete":   "DELETE /api/customers/{id}",
        "getByKey": "GET /api/customers?name={key}",
        "getById":  "GET /api/customers/{id}"               // optional: enables replay/verify
      }
    }
  }
}
```

## How it maps

- **Entity shape** comes from the same plugin-loaded CLR types: the `api.entities` map *is*
  the domain's entity list; each name resolves to a CLR type via the profile's
  `entityClassPattern` (`Customer` → `CustomerEntity`). Generation — convention fakers,
  auto-faker, generator plugins, distributions, locale — is the exact shared machinery of
  the EF runtime, so same seed ⇒ same values regardless of transport.
- **Payloads** are the entity's scalar properties (camelCase, enums as strings);
  navigations are never serialised. **Client-set ids ride the payload** (identity-contract
  uuid-v5); **server-assigned ids are captured from the create response** into the manifest,
  exactly like DB-generated ints. Server-assigned keys are omitted from the payload.
- **References** set the `{Entity}Id` convention property (or a navigation); `getByKey`
  drives reference lookups, load/verify steps and idempotent create-or-reuse.
- **Lifecycles**: `Persistent` and `TrackedTeardown` (deletes via the API in reverse
  creation order; a 404 counts as already-deleted). `Transactional` is unsupported — it
  fails `tdm validate` / refuses `tdm run` with a clear message before any HTTP traffic.
- **No query surface**: `delete all …` and count-verification steps fail actionably —
  delete/verify by natural key instead.
- **Auth**: the shipped mode resolves a token via the W2-D8 secret chain
  (`auth.tokenSecret`) and sends `"{scheme} {token}"` in `auth.headerName`
  (default `Authorization: Bearer …`). Cloud token flows (AzureAd client credentials)
  belong host-side, same posture as `ISecretProvider` — TDM ships no cloud SDKs.
- **Volume**: bulk creates go per-row (there is no cross-API batch) with bounded retries on
  5xx/connection failures (`api.maxRetries`, `api.retryDelayMs`). Use the W2 policy gate
  (`maxBulkRowsPerStep`) to require explicit opt-in for bulk-through-API (§6 risk table).

The acceptance criterion is a real test (`tests/Tdm.Api.Tests`): the Orders sample domain
seeds through a stub HTTP API with TrackedTeardown deleting in reverse order — driven by
the unmodified `TdmEngine`.
