---
name: tdm-feature-author
description: >-
  Author or edit a TDM Gherkin feature file and reach a green `tdm validate`. Use when
  asked to "seed test data", "add a scenario", "create a feature", or "make test data for
  X". Works entirely without a database.
---

# Authoring a TDM feature

Goal: a well-formed `features/**/*.feature` that `tdm validate` accepts. No database is
touched — `validate` and `explain` persist nothing, so iterate freely.

## Loop

1. **Learn the schema.** Run `tdm list-entities` (and read `tdm.model.json`) for the real
   entity names, their natural keys, repositories and fakers. Do not invent entity or
   property names — use what resolved.
2. **Draft the smallest scenario** that expresses the data the test needs. One `Feature`,
   a pinned seed, a `Given` that creates, a `Then` that verifies:
   ```gherkin
   @seed:42
   Feature: Orders regression seed
     Scenario: Customer places an order
       Given a Customer exists with name "Acme Ltd" and tier "Gold"
       And an Order exists for Customer "Acme Ltd" with status "Pending"
       Then 1 Order should exist with status "Pending"
   ```
3. **`tdm explain` every new step** *before* running validate. It prints the grammar rule
   matched, the entity resolved, the faker/persistence route and the derived id. If a step
   prints **no matching rule** or the wrong entity, the step is wrong — fix the wording,
   don't proceed. This is the single highest-value habit.
4. **`tdm validate --report sarif=output/tdm.sarif`.** Read the SARIF, not stdout. Fix the
   squiggle-class findings it lists (unresolved entity, unknown property, missing natural
   key, write-repository policy violation) until validate is `0`.
5. **Repeat** per scenario. Keep each scenario independent and readable — a scenario is a
   business statement, not a script.

## Grammar rules that matter

- **Create:** `Given a <Entity> exists with <prop> "<value>" [and <prop> "<value>"]`.
  Unspecified properties are generated from the seed; the values you give always win.
- **Reference by natural key:** `... for <Entity> "<naturalKey>"` — never by id. The id is
  derived (`UUIDv5("{domain}|{Entity}|{naturalKey}")`); creating the principal first (a
  `Background` is the natural home) makes the reference resolve.
- **Bulk:** `Given <N> <Entities> exist` generates N rows under the seed.
- **Verify:** `Then <N> <Entities> should exist [with <prop> "<value>"]` /
  `Then a <Entity> "<naturalKey>" should exist`.
- **Externally-owned references:** `Given an external <Entity> reference "<key>" from
  <OtherDomain>` — cite a row another domain owns without creating it here.

If unsure whether a phrasing is legal, **`tdm explain` it** — the grammar reference is
authoritative but explain is faster and specific to this repo's resolved model.

## Definition of done

- Every step `tdm explain`s to the intended entity and route.
- `tdm validate` exits `0` (or `1` only for warnings you have consciously accepted).
- Seeds are pinned; references use natural keys; scenarios are independent.

Do **not** run `tdm run` to check your work unless explicitly asked to seed — a green
`validate` is the bar. If you do seed, target only a dev/local database.

Depth: <https://chrisw000.github.io/test-data-manager/reference/grammar/> and the
[feature-author guides](https://chrisw000.github.io/test-data-manager/guides/daily-use-qa/).
