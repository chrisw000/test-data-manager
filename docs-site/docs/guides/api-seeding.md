---
tour_prev: guides/multi-domain-identity.md
tour_next: guides/seed-packs.md
---

# API seeding

**Personas:** developer, QA. Some domains forbid direct database writes — the only
sanctioned way in is the public API. TDM can seed through it, which also exercises the
domain's validation, auth and side-effects for free.

## When to reach for it — and what you give up

Use API seeding when direct DB access is not allowed, or when you *want* the API's
behaviour (validation, computed fields, events) to run as it would in production. In
exchange:

- **No `Transactional` lifecycle** — there is no shared DB transaction to roll back.
  `Persistent` and `TrackedTeardown` (deletes via the API, reverse order) are supported;
  `Transactional` fails validation.
- **No query-surface steps against the DB** — verification goes through the API's read
  endpoints, not SQL.

## Configuration

Set the domain's `persistence` to `Api` and give it an endpoint map. The map *is* the
domain's entity list in API mode — only entities named here are seedable:

```jsonc
"domains": [{
  "name": "Fulfilment",
  "persistence": "Api",
  "api": {
    "baseUrl": "https://fulfilment.internal",
    "auth": { "scheme": "Bearer", "tokenEnv": "FULFILMENT_API_TOKEN" },
    "timeoutSeconds": 30,
    "maxRetries": 2,          // per request, on 5xx / connection failure
    "retryDelayMs": 200,
    "entities": {
      "Shipment": {
        "create": "POST /api/shipments",
        "read":   "GET /api/shipments?number={key}",  // {key} = URL-escaped natural key
        "delete": "DELETE /api/shipments/{id}"         // {id} = the (server-assigned) key value
      }
    }
  }
}]
```

- **Auth** resolves through the [secret chain](cd-environments.md#secrets) — the token env
  var, never an inline secret in the file.
- **Retries** cushion bulk seeding against transient 5xx / connection failures.
- **`{id}` vs `{key}`** — `{key}` is the URL-escaped natural key (for reads/lookups);
  `{id}` is the key *value* the create returned (for deletes).

## Server-assigned vs client-set ids

If the API assigns ids (a server-generated shipment number or numeric id), TDM records the
returned id in the manifest and uses it for teardown — exactly as the
[complex-domains](complex-domains.md) Shipment does with its server-assigned `long` key.
When the id is client-set (the identity contract), TDM sends the derived id and the API is
expected to honour it.

## Testing the wiring: the stub-API template

You do not need the real API running to test your endpoint map. The repository's
`Tdm.Api.Tests` uses a dependency-free in-process stub — copy it as a template for your
own contract checks:

```csharp
using var server = new StubApiServer();           // binds a free localhost port
server.OnRequest = req => req.Method == "POST"     // answer creates with a 201 + body
    ? (201, """{ "id": 12345 }""")
    : (404, null);

// point a Fulfilment domain's api.baseUrl at server.BaseUrl, run a feature, then assert:
server.Requests.Should().Contain(r => r.PathAndQuery == "/api/shipments");
```

`StubApiServer` records every request (method, path, body, `Authorization`), so you can
assert the exact calls TDM makes — including that teardown deletes in reverse order.

!!! info "Engineering record"
    [api-seeding.md](https://github.com/chrisw000/test-data-manager/blob/main/docs/api-seeding.md).

## Where next

- [Testing complex domains](complex-domains.md) — Fulfilment, whose demo variant seeds via
  API (and whose test variant seeds via DbContext).
- [CD & environments](cd-environments.md) — auth tokens through the secret chain.
- [Configuration → domains.api](../reference/configuration.md#domainsapi-api-seeding-w4-d6).

**Guided tour:** next stop → [Seed packs](seed-packs.md)
