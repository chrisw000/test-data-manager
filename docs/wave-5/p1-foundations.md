# W5-P1 — Foundations: IA, platform, snippet-CI, Getting Started

**Parent:** [wave-5-handoff.md](../wave-5-handoff.md) · Decisions W5-D1, W5-D2, W5-D4, W5-D5

The phase every other phase builds on: the site's information architecture, the linking
conventions, the verification harness that keeps guides honest, and the first guide
written against all of it.

## Deliverables

### 0. Messaging spine — `docs/wave-5/messaging.md`

One page, written first: the product one-sentence, the three benefit pillars per persona,
the canonical example feature, and the phrases we always/never use (e.g. always "no
production data by default", never "mocking"). The home page (this phase) and the forum
deck (P2) are both written *from* it — the mechanism that keeps deck and docs from
diverging (wave risk table).

### 1. Information architecture (docs-site nav rebuild)

```
Home                        ← persona router + product one-pager + pipeline walkthrough
Start here/
  getting-started.md        ← the ≤15-minute path (this phase)
  concepts.md               ← determinism, the grammar, the identity contract, the manifest
  tour.md                   ← guided-tour index (chain completed in P6)
Guides/                     ← one file per guide from the master table (P3–P5 fill these)
Reference/
  grammar.md  configuration.md  profiles.md  cli.md (new)  reports-and-manifest.md (new)
  plugin-packaging.md  troubleshooting.md  decisions.md
Agents/                     ← P6
Slides/engineering-forum.html  ← P2 (raw HTML passes through mkdocs)
```

- Existing pages are refreshed in place (grammar/configuration gain Wave 2–4 material:
  `--env`, policy, seed packs, `entities.{X}.properties`, api settings, statsPacks).
- **New reference pages:** `cli.md` — every `tdm` command, its options, exit codes, one
  table per command, generated *content* checked against `tdm --help` output by the
  snippet harness; `reports-and-manifest.md` — the manifest schema tour, SARIF/JUnit/HTML
  report formats, checksum/signing.

### 2. Linking conventions (the walkable set)

- Every guide ends with a **"Where next"** footer: 2–4 links (related guides, the design
  doc behind the feature, the reference page). Implemented as a consistent markdown
  section, not tooling.
- Every guide carries tour metadata (`tour_prev` / `tour_next` in front matter). A tiny
  lint script (`docs-site/lint-tour.ps1` + sh twin, run in docs-CI) asserts the chain is
  a single path with no orphans — the tour cannot silently break.
- Design docs in `/docs` are linked with a standing admonition style: *"Engineering
  record: [statistical-generation.md](…)"* — users learn where depth lives.

### 3. Snippet-verification harness (W5-D4)

- Guides embed commands via `pymdownx.snippets` from `docs-site/snippets/*.sh` — one file
  per guide section, plain `tdm …`/`dotnet …` lines.
- New `docs-verify` job in `ci.yml`: builds the host, then executes every
  `docs-site/snippets/*.sh` in order against the sample workspace (SQLite), failing on
  non-zero exit. Snippets that need cleanup pair with `*.teardown.sh`.
- `mkdocs build --strict` moves into the same job (currently only at deploy time), so
  broken links fail PRs.
- Pages deploy workflow gains `push: branches: [main], paths: [docs-site/**]`.

### 4. Pipeline walkthrough interactive (W5-D5)

`assets/interactive/pipeline-walkthrough.js` + inline SVG on the concepts page: a feature
step's journey — *step text → StepGrammar match → entity resolution → faker/statistical
layer → overrides → identity → persistence route → manifest entry* — click each stage to
see the artifact it produces (the real `tdm explain` output for the example step, captured
by a snippet). Vanilla JS, no libraries; light/dark aware like the W4-P1 report.

### 5. Getting Started (the proof guide)

The ≤15-minute path, structured as numbered checkpoints with expected output shown after
each command (captured output verified by the harness where stable):

1. Install (`dotnet tool install Tdm.Tool`) — or clone-and-run for this repo.
2. `tdm init` a workspace; read what it scaffolded.
3. `tdm list-entities` — meet convention resolution.
4. Write a 3-step feature (create + reference + verify); `tdm explain` one step.
5. `tdm validate` — the no-database gate.
6. `tdm run` — read the console summary, then open the manifest and the HTML report.
7. Where next: the persona router (QA → daily-use-qa; dev → daily-use-dev; platform → ci).

## Acceptance

- Nav matches the IA above; all existing pages reachable; `--strict` green.
- `docs-verify` job green on a PR that deliberately breaks a snippet (prove it fails),
  then green on the fix.
- Getting Started executed end-to-end by a colleague (or fresh agent session) in ≤15 min.
- Tour lint runs (chain may be partial until P6, but the mechanism works).
