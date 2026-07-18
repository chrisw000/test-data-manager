---
tour_prev: start/concepts.md
tour_next: guides/daily-use-dev.md
---

# Daily use for QAs

**Persona:** QA / test author. You think in test cases; TDM makes the test's data part
of the test. Everything on this page runs against this repository's sample workspace in
CI, on every push.

## The verb cookbook, by intent

**"I need a customer that…"** — create with overrides. Anything you don't say is
generated deterministically under the scenario seed:

```gherkin
Given a Customer exists with name "Acme Ltd" and tier "Gold" and credit limit "25000"
```

**"…that belongs to something"** — references, by natural key. Repeatable for multiple
principals; TDM resolves the target from this scenario, the database, or another domain:

```gherkin
Given an Order exists for Customer "Acme Ltd" with order number "ORD-1001" and status "Pending"
Given an Invoice exists for Account "A-1" for Customer "Acme Ltd" with amount "1250.00"
```

Ask the pipeline how a reference will resolve — no database needed:

```bash
--8<-- "daily-use-qa/01-explain-reference.sh"
```

**"…lots of them"** — count bulk for volume, DataTable bulk for precise rows (shared
reference/defaults before the colon, per-row cells win):

```gherkin
Given 500 Products exist with category "LoadTest"
Given the following Invoices exist for Account "Cleanup Account":
  | InvoiceNumber | Amount | Status | IssuedDate |
  | INV-D-01      | 10.00  | Draft  | today-3d   |
```

**"Something changed / went away"** — update and delete:

```gherkin
When the Customer "Acme Ltd" is updated with tier "Platinum"
When all Invoices with status "Draft" are deleted
```

```bash
--8<-- "daily-use-qa/02-explain-update.sh"
```

**"Prove it worked"** — verify steps read back through the same pipeline, so the seed
data asserts itself:

```gherkin
Then an Order "ORD-1001" should exist with status "Pending"
Then 2 Products should exist with category "Widgets"
Then 0 Invoices should exist with status "Draft"
```

**Relative dates** work anywhere a date does: `today`, `today-3d`, `today+2h`. Use them
for "recent order" / "overdue invoice" cases that must stay true forever.

The complete step syntax lives in the [grammar reference](../reference/grammar.md) —
every example there is an executed repository feature.

## Scenario design patterns

- **Background for base data** — the customer every scenario in the feature needs.
  Backgrounds run per scenario, so each scenario stays self-contained.
- **Scenario Outline + Examples for variants** — tiers, statuses, locales. Placeholders
  substitute in step text *and* DataTable cells.
- **Tags:**

| Tag | Reach for it when |
|---|---|
| `@seed:42` | Always, at feature level — reproducibility is the point |
| `@domain:Billing` | Two domains expose the same entity name and you want to pin one |
| `@persistent` | This scenario's rows must outlive the run (demo data, environment base data) |
| `@ephemeral` | This scenario must clean itself up at scenario end, whatever the run default |
| `@skip` | The step grammar or the domain isn't ready — parsed, reported, not executed |
| `@benchmark` | You want per-operation timings for this scenario in the manifest |

## The feedback loop: never guess

1. **Editor squiggles** — with [editor setup](editor-setup.md), unknown entities and
   properties are underlined as you type, before you ever run anything.
2. **`tdm explain`** — one step, every pipeline decision (grammar match, resolution,
   faker, persistence route, identity). The first command to reach for when a step
   surprises you.
3. **`tdm validate`** — the whole workspace, no database touched:

```bash
--8<-- "daily-use-qa/03-validate.sh"
```

If validate is green, your PR's TDM gate will be too — it's the same check
([CI guide](ci.md)).

## Reading a failed run

Work outward from the console summary:

1. **The summary line per scenario** — outcome, seed, rows created, warning count.
2. **The manifest** (`output/*.tdm.json`) — find your scenario, then look at:
   `unmatchedSteps` (a step the grammar didn't recognise — usually a typo the editor
   would have caught), `warnings`, and each entity's `values` + `overridesApplied`
   (did your override actually land?).
3. **The HTML report** (`tdm report --manifest …` or `--report html=…`) — the same
   evidence with drill-down and the reference lineage graph, easier to share in a bug
   ticket. See [Reports & the manifest](../reference/reports-and-manifest.md).

Common failures and what they mean are collected in
[troubleshooting](../reference/troubleshooting.md).

## Determinism etiquette

- **Pin `@seed` at feature level.** Unpinned features use `run.defaultSeed` — still
  deterministic, but your feature's data changes if the workspace default does.
- **Override what the test asserts.** If the test checks `tier "Gold"`, say
  `with tier "Gold"` — don't hope the faker picks it. Generated values are for the
  fields your test *doesn't* care about.
- **Don't assert on generated values** (names, emails, dates you didn't set). If you
  need to assert it, override it; if you need realistic volume shape, that's
  [statistical generation](statistical-generation.md) — configured, not hoped for.

## Hand-offs to your domain owner

Some fixes belong on the developer side of the fence — link them straight there:

- A property generates unrealistic values → ask for a
  [faker or statistical config](daily-use-dev.md#fakers-generated-values).
- "no write repository found" warnings → the
  [repository conventions](daily-use-dev.md#fixing-resolution-warnings) need attention.
- An entity resolves to the wrong natural key → the
  [entity config](daily-use-dev.md#entity-config-entitiesx) sets it.

## Where next

- [Daily use for developers](daily-use-dev.md) — the other side of the hand-off.
- [Grammar reference](../reference/grammar.md) — every verb, executed.
- [Editor setup](editor-setup.md) — squiggles and completion in under five minutes.

**Guided tour:** next stop → [Daily use for developers](daily-use-dev.md)
