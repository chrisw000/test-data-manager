---
tour_prev: guides/complex-domains.md
---

# Agents & the agent-kit

**Persona: the agentic coder or tester.** An agent is a first-class TDM user — it reads
repo-local operating instructions, iterates in tight tool loops, and needs guardrails
stated, not implied. TDM is built for this: every command is scriptable, every result is a
structured artifact (manifest JSON, SARIF, JUnit), and the safe path — `explain → validate`
— touches no database. The **agent-kit** is what you hand your agent so it uses that path
by default.

## What to hand your agent

The kit is plain markdown, runner-agnostic, and versioned in-repo under
[`agent-kit/`](https://github.com/chrisw000/test-data-manager/tree/main/agent-kit):

- **`AGENTS.md`** — the operating instructions for a repo that uses TDM: the command loop
  (`explain → list-entities → validate → run → read the manifest`), how to read results
  (exit codes, manifest anatomy, SARIF), the guardrails, and the determinism rules.
- **`skills/<name>/SKILL.md`** — four task-scoped playbooks, one job each, each with YAML
  front matter (name, description/trigger) so a runner can route to it:

    | Skill | When it fires | What it drives |
    |---|---|---|
    | `tdm-feature-author` | "seed / author test data" | model → grammar → explain each step → green `validate` |
    | `tdm-run-triage` | "the seeding run failed" | manifest → journal → policy → SARIF → cause class + minimal repro |
    | `tdm-perf-analyst` | "check / gate performance" | bench stats → trend store → `bench compare` → evidence-backed gate |
    | `tdm-domain-onboarding` | "wire a new domain in" | `list-entities` loop → fix resolution warnings → `export-model` → first feature |

Read the exact files, verbatim, on [The agent-kit (verbatim)](kit.md) — those pages are
single-sourced from `agent-kit/` via the snippet mechanism, so what you read here is
byte-for-byte what ships.

## Scaffold it into your repo

`tdm init --agents` writes the kit into your workspace, substituting your domain name for
the template's placeholder:

```bash
tdm init --agents --domain Orders
# writes AGENTS.md, VERSION, and skills/<four>/SKILL.md alongside the usual init output
```

It never overwrites an existing file (each is reported *written* or *skipped*), so it is
safe to re-run as the kit evolves. Plain `tdm init` mentions the flag in its closing tip.

## How the kit maps to runners

The kit deliberately uses the conventions the major runners already read, so no adapter is
needed:

- **Claude Code** and similar skill-aware runners read `skills/<name>/SKILL.md` with its
  YAML front matter — drop the `skills/` directory where the runner discovers skills, or
  point it at the scaffolded copy.
- **Generic `AGENTS.md` consumers** read the single operating file at the repo root — that
  is `AGENTS.md` as-is, no conversion.
- **Anything else** — the files are plain markdown; paste `AGENTS.md` into a system prompt
  and the skills as task context. There is nothing runner-specific in the bodies.

## What to expect

Given only the kit and a sample-domain workspace, an agent should author a multi-step
feature and reach a green `tdm validate` **unaided** — and, handed a broken run, diagnose
it from the manifest and SARIF alone (bad natural key, policy violation) without guessing.
That empirical bar is the kit's acceptance test. The kit keeps the agent on the safe,
no-database path until you explicitly ask it to seed, and stops it short of the actions
that move other teams' ids.

## Where next

- [The agent-kit (verbatim)](kit.md) — the exact files, single-sourced from `agent-kit/`.
- [CI — validate, report, gate](../guides/ci.md) — the SARIF/JUnit outputs agents consume.
- Back to [the guided tour](../start/tour.md) — you've reached the last stop.

**Guided tour:** this is the final stop — back to [the tour index](../start/tour.md).
