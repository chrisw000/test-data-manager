# W5-P6 — Agent enablement kit & wave wrap

**Parent:** [wave-5-handoff.md](../wave-5-handoff.md) · Decision W5-D6
**Depends on:** P1–P5 (the kit links guides; the wrap audits the whole set).

Agentic coders and testers are first-class users: they read repo-local operating
instructions, iterate in tight tool loops, and need guardrails stated, not implied.
Everything here is plain markdown, runner-agnostic, and versioned in-repo under
`agent-kit/` so it ships with the tool and rides `tdm init` into consuming repos.

## 1. `agent-kit/AGENTS.md` — the consuming-repo template

Sections (kept short; agents pay per token):

- **What this repo uses TDM for** (one paragraph + where features/settings live).
- **The command loop:** `tdm explain` (never guess a step) → `tdm validate` (no DB, run
  freely) → `tdm run` (only against dev targets) → read the manifest, not stdout, for
  ground truth. `tdm list-entities` / `tdm.model.json` as the schema oracle.
- **Reading results:** exit codes 0/1/2; manifest anatomy (warnings, unmatched steps,
  values, lineage); SARIF for machine-readable findings.
- **Guardrails:** never `run` with `--env` pointing at a shared environment; never edit
  `tdm.plugins.lock.json` or checked-in manifests by hand; never bypass a key-registry
  violation (it is a cross-team contract); prefer `validate` until asked to seed.
- **Determinism rules:** pin seeds when reproducing; overrides beat regeneration.

## 2. `agent-kit/skills/` — four skills, one job each

Each is `skills/<name>/SKILL.md` with YAML front matter (name, description/trigger) —
the convention major agent runners read; contents are runner-agnostic instructions.

| Skill | Trigger / job | Core loop |
|---|---|---|
| `tdm-feature-author` | "seed/author test data" | model → grammar rules → explain each step → validate → fix squiggle-class errors from SARIF |
| `tdm-run-triage` | "the seeding run failed" | manifest → journal → policy violations → per-entity warnings → minimal repro via explain; distinguish config, data, environment causes |
| `tdm-perf-analyst` | "check/gate performance" | bench stats → trend store → `bench compare` → propose gate/quarantine changes with evidence |
| `tdm-domain-onboarding` | "wire a new domain into TDM" | list-entities loop → fix resolution warnings (fakers, repos, natural keys) → entity config → export-model → first feature |

Skills reference guide URLs for depth but must be sufficient alone for the happy path.

## 3. `tdm init --agents`

Scaffolds `AGENTS.md` + `skills/` (from embedded copies of `agent-kit/`) into the target
workspace, with domain names substituted where the templates carry placeholders. Covered
by an `InitScaffolder` test. Plain `tdm init` mentions the flag in its closing output.

## 4. Docs-site `Agents/` section

- `agents/index.md` — why agents are a persona; what to hand your agent (the kit),
  what to expect; how the kit maps to runners (Claude Code skills dir, generic AGENTS.md
  consumers).
- Rendered copies of the kit files (single-sourced from `agent-kit/` via the snippet
  mechanism so site and shipped kit cannot diverge).

## 5. Wave wrap: tour completion & audits

- Complete the guided-tour chain across every guide (P1's lint now enforces the full
  path); write `tour.md`'s narrative ("walk the whole product in ninety minutes").
- Cross-link audit: every guide's "Where next" reviewed as a set; every design doc in
  `/docs` linked from at least one guide (script-assisted check).
- Home-page persona router finalised with the complete guide inventory.
- README's documentation section rewritten to point at the site's persona paths.

## Acceptance (empirical, per W5-D6)

- A **fresh agent session** given only `agent-kit/` + a sample-domain workspace authors a
  new multi-step feature and reaches green `tdm validate` unaided; a second session using
  `tdm-run-triage` correctly diagnoses a planted failure (bad natural key + a policy
  violation) from the manifest/SARIF alone.
- `tdm init --agents` scaffolds the kit; test asserts content and substitution.
- Tour lint passes over the complete chain; the design-doc link audit reports zero
  orphans; `mkdocs build --strict` green.
