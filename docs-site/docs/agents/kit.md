# The agent-kit, verbatim

Every file below is **single-sourced** from
[`agent-kit/`](https://github.com/chrisw000/test-data-manager/tree/main/agent-kit) and
included here verbatim (via the snippet mechanism). What you read is byte-for-byte what
`tdm init --agents` scaffolds and what a runner reads — the docs and the shipped kit
cannot diverge. Copy any block as-is.

!!! tip "Placeholders"
    The kit ships with `YourDomain` as a stand-in domain name. `tdm init --agents --domain
    <D>` substitutes your real domain when it scaffolds; the files below show the template
    form.

## `AGENTS.md`

The consuming-repo operating instructions — the command loop, reading results, guardrails
and determinism rules.

```markdown
--8<-- "AGENTS.md"
```

## `skills/tdm-feature-author/SKILL.md`

Author or edit a feature and reach a green `tdm validate`.

```markdown
--8<-- "skills/tdm-feature-author/SKILL.md"
```

## `skills/tdm-run-triage/SKILL.md`

Diagnose a failed or wrong seeding run from its manifest, journal and SARIF.

```markdown
--8<-- "skills/tdm-run-triage/SKILL.md"
```

## `skills/tdm-perf-analyst/SKILL.md`

Check or gate seeding performance from benchmark stats and the trend store.

```markdown
--8<-- "skills/tdm-perf-analyst/SKILL.md"
```

## `skills/tdm-domain-onboarding/SKILL.md`

Wire a new domain's `DbContext` + repositories into TDM so its entities are seedable.

```markdown
--8<-- "skills/tdm-domain-onboarding/SKILL.md"
```
