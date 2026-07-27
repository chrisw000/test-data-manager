# agent-kit

The distributable operating kit for agentic coders and testers using TDM. This directory
is the **single source of truth**:

- **`tdm init --agents`** embeds these files (as resources in `Tdm.Host`) and scaffolds
  them into a consuming repo, substituting the real domain name for the `YourDomain`
  placeholder.
- **The docs site** renders them under *Agents* via the snippet mechanism
  (`--8<--`), so the published docs and the shipped kit cannot diverge.

## Contents

- `AGENTS.md` — the consuming-repo operating instructions (command loop, reading results,
  guardrails, determinism).
- `skills/<name>/SKILL.md` — four task-scoped playbooks with YAML front matter (the
  convention major agent runners read), runner-agnostic in body:
  `tdm-feature-author`, `tdm-run-triage`, `tdm-perf-analyst`, `tdm-domain-onboarding`.
- `VERSION` — the kit's semantic version. Bump it when the operating contract changes;
  consuming repos can diff their scaffolded copy against a known version.

## Placeholders

Templates carry `YourDomain` (and lowercase `yourdomain` for db/paths) where a domain name
belongs. `tdm init --agents --domain <D>` substitutes them; the un-substituted files read
as generic examples.
