# Working with TDM in this repo

Operating instructions for agentic coders and testers. TDM (Test Data Manager) seeds
relational test data from plain-language Gherkin — deterministically, with an audit
manifest for every row. You are a first-class user: read this before touching a feature
file, and prefer its loop to guessing.

> Where this kit names a domain, it means the domain(s) this repo configures in
> `tdm.settings.json` — `tdm init --agents` fills the real name in when it scaffolds the kit.

## What this repo uses TDM for

Test data is declared as Gherkin in `features/**/*.feature` and seeded against the
`YourDomain` domain(s). Configuration lives in `tdm.settings.json`; the resolved schema
oracle is `tdm.model.json` (generated — never hand-edit). Every run writes a JSON
**manifest** under `output/` — that manifest, not stdout, is ground truth.

## The command loop

Work in this order. Each step is cheap and safe until the last.

1. **`tdm explain "<a Gherkin step>"`** — never guess a step. Prints the grammar rule it
   matches, the entity/faker/repository it resolves, the persistence route, and the
   derived identity. No database connection. Use it to author and to build a minimal repro.
2. **`tdm list-entities`** — the resolved entity → repository → faker → natural-key map.
   This plus `tdm.model.json` is your schema oracle; consult it before inventing an
   entity or property name.
3. **`tdm validate`** — parses features and resolves entities/fakers/repositories.
   **Persists nothing**, so run it freely and often. This is your inner loop; a green
   validate is the bar for "the feature is well-formed." Emit machine-readable findings
   with `--report sarif=output/tdm.sarif`.
4. **`tdm run`** — actually seeds. Only against **dev/local targets** (see Guardrails).
   Writes the manifest and a crash-safe `.tdm.journal.jsonl`.
5. **Read the manifest, not stdout.** `output/*.tdm.json` holds final values, seeds,
   persistence routes, ids and lineage. Assertions and diagnosis come from there.

## Reading results

- **Exit codes:** `0` succeeded · `1` completed with warnings · `2` failed (grammar,
  unresolved entities, policy refusals, configuration errors). `tdm manifest verify` and
  `tdm verify` (drift) have their own codes — check `tdm <cmd> --help`.
- **Manifest anatomy:** per-scenario `warnings`, `unmatchedSteps` (a step no rule caught),
  per-row final `values`, `persistedVia`/`idStrategy`, and `lineage` (which step produced
  which row, and the references between rows). Unmatched steps and warnings are the first
  place to look when a run is green-ish but wrong.
- **SARIF** (`--report sarif=…`) is the machine-readable findings stream: one result per
  grammar/resolution/policy issue, with file and line. Prefer it to scraping stdout.

## Guardrails

- **Never `tdm run --env` against a shared environment.** `--env <name>` switches on
  policy enforcement for a named target; only use dev/local targets, and prefer `validate`
  until explicitly asked to seed.
- **Never hand-edit `tdm.plugins.lock.json`, `tdm.model.json`, or a checked-in manifest.**
  They are generated/locked artifacts; regenerate them with the tool
  (`tdm run --update-plugins`, `tdm export-model`) so their checksums stay honest.
- **Never bypass a key-registry violation.** The key registry (`tdm.keys.json`) is a
  cross-team contract; a violation means your change would move ids other teams depend on.
  Surface it — do not force past it.
- **Prefer `validate` to `run`.** If a task only needs the feature to be well-formed,
  stop at green validate. Seed only when asked, and only to a throwaway target.

## Determinism rules

- **Pin the seed when reproducing.** `@seed:N` on a Feature (or `--seed N`) fixes all
  generated values; the same seed + same plan = the same rows. When you file a repro,
  state the seed.
- **Overrides beat regeneration.** A value written in a step (`with name "Acme Ltd"`)
  always wins over the faker; only unspecified properties are generated. If a value is
  surprising, check whether a step set it before suspecting the generator.
- **Identity is derived, not random.** An entity's id is `UUIDv5("{domain}|{Entity}|
  {naturalKey}")` — stable across runs and repos. Reference rows by natural key; the id
  follows.

## Skills

Task-scoped playbooks live in `skills/`. Reach for the one that matches the job:

- **`tdm-feature-author`** — author or edit a feature and reach green `validate`.
- **`tdm-run-triage`** — a seeding run failed; diagnose from manifest/SARIF.
- **`tdm-perf-analyst`** — check or gate seeding performance.
- **`tdm-domain-onboarding`** — wire a new domain into TDM.

Full documentation: <https://chrisw000.github.io/test-data-manager/>
