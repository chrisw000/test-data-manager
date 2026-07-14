# TDM — Gherkin-driven Test Data Manager

TDM seeds relational test data from plain Gherkin feature files. Domain teams ship their
EF Core `DbContext` + repositories as **plugins**; anyone can then declare data in
business language and get **deterministic, reproducible, auditable** rows:

```gherkin
@seed:42
Feature: Orders regression seed
  Scenario: Customer places an order
    Given a Customer exists with name "Acme Ltd" and tier "Gold"
    And an Order exists for Customer "Acme Ltd" with status "Pending"
    Then 1 Order should exist with status "Pending"
```

Three foundations make it safe at scale:

- **The run manifest** — every run writes a JSON manifest with full final values, seeds,
  persistence routes and ids: the reproducibility and audit artifact.
- **The identity contract** — deterministic UUIDv5 ids derived from
  `{domain}|{Entity}|{naturalKey}`, so independent teams' data references agree without
  coordination.
- **`tdm validate`** — the persistence-free gate: grammar, entity resolution and policy
  (e.g. [write repositories required](decisions.md)) are checked in CI before any data exists.

## Quick start (aiming for green CI in under 30 minutes)

1. **Install the tool**

    ```bash
    dotnet tool install --global Tdm.Tool
    # or, in CI without a .NET toolchain:
    docker run --rm -v $PWD:/work -w /work ghcr.io/chrisw000/tdm:latest validate
    ```

2. **Scaffold a project**

    ```bash
    tdm init --domain Orders --package Acme.Orders.Data.Persistence
    ```

    This writes an annotated `tdm.settings.json`, a starter feature, a `.gitignore`, and a
    CI workflow (`.github/workflows/tdm-validate.yml`) — nothing is overwritten if present.

3. **Provide the domain assemblies** — either drop your data-layer build output into
   `./plugins/Orders`, or switch to feed acquisition
   (see [Configuration → plugins](configuration.md#plugins)).

4. **Check what resolved**

    ```bash
    tdm list-entities        # entity → CLR type, keys, faker, write/read repository
    tdm explain 'a Customer exists with name "Acme Ltd"'   # every pipeline decision for one step
    ```

5. **Validate, then run**

    ```bash
    tdm validate             # exit 0 = CI-green; persists nothing
    tdm run                  # seeds, writes ./output/{name}-{stamp}.tdm.json
    tdm teardown --manifest ./output/<manifest>.tdm.json   # exact reverse-order cleanup
    ```

!!! tip "The examples cannot rot"
    Every snippet in the [grammar reference](grammar.md) comes from the repository's
    `features/` files, which CI executes on every push.
