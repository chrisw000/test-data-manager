# TDM Wave 5 — Documentation & Enablement: Implementation Handoff

**Status:** Design proposed, ready for review
**Audience:** Implementing engineer / AI pair
**Owner:** Chris (Engineering Manager)
**Date:** 2026-07-18
**Depends on:** Waves 1–4 (all shipped); the existing docs-site (W1-D5) and the design docs in `/docs`

Per-phase specifications live in [`docs/wave-5/`](wave-5/) — each phase is a
self-contained handoff so the wave builds incrementally, exactly like the product waves.

---

## 1. Purpose

Waves 1–4 built the product; the docs-site still describes Wave 1. This wave turns TDM's
capabilities into **adoption**: an Engineering Forum deck that lands the concept in twenty
minutes, task-first user guides that carry a reader from first install to multi-domain
CI/CD, interactive explainers where a picture genuinely beats prose, and — because the
next generation of users are agents — distributable `AGENTS.md`/skill files that make TDM
legible to agentic coders and testers.

**Audiences** (every page declares whose path it is on):

| Persona | Cares about | Primary entry |
|---|---|---|
| **QA / test author** | expressing test data in the test's language; determinism; verify steps | Daily use for QAs |
| **Developer / domain owner** | seeding their own domain's database; conventions; fakers; local loops | Daily use for developers |
| **Platform / DevEx** | golden paths: CI validate gates, CD environment policy, locks, secrets, perf gates | CI and CD guides |
| **Agentic coder / tester** | machine-readable operating instructions, tight feedback loops, guardrails | `AGENTS.md` + skills |

**The domain reality the docs must teach** (it shapes every multi-domain page): an
application is many domains; each domain lives in its own database, typically fronted by
its own access API, with its own `DbContext`. TDM's answer — the identity contract, external
references, projections, API seeding, seed packs — only makes sense against that picture,
so the docs draw it early and often.

**Non-goals (this wave):** a hosted docs portal beyond GitHub Pages; video content;
translating the engineering design docs in `/docs` into user guides wholesale (they remain
the engineering record; guides re-explain and link to them).

---

## 2. Deliverables

### 2.1 Documentation platform & information architecture (W5-D1, W5-D2, W5-D4) — P1

The existing MkDocs Material site becomes the hub: persona-routed home page, a
**guided tour** (prev/next chain through every guide), a "where next" footer convention on
every page, and a **snippet-verification harness** so every command a guide shows is
executed by CI against the sample workspace — the same "docs cannot drift" posture
`grammar.md` already has. Interactive components are self-contained vanilla JS/SVG assets
(D3 vendored only if a component truly needs it — never a CDN), mirroring the W4-D1
zero-external-dependency posture. Site deploys on push to `main` (docs paths), not only on
release. Includes the rewritten **Getting Started** guide, which doubles as the harness's
proof.

### 2.2 Engineering Forum slide deck (W5-D3) — P2

A **single self-contained HTML deck** (keyboard navigation, speaker notes, print-to-PDF)
served from the docs site and openable from `file://` with zero network access — TDM's
own report posture applied to slides. Content: the problem → what TDM is → QA test
seeding → natural keys & the identity contract (the conceptual centrepiece) → the VS Code
helper → CI operation & reporting → statistical generation for realistic, deterministic
performance data → a closing "deeper dive menu" slide enumerating what a follow-up
session covers.

### 2.3 Task-first user guides — P3, P4, P5

| Guide | Persona(s) | Phase |
|---|---|---|
| Getting started (≤15 min to green) | all | P1 |
| Daily use for QAs · Daily use for developers | QA · dev | P3 |
| Editor setup: VS Code + any LSP editor | QA, dev | P3 |
| CI: validate, report, gate | platform | P3 |
| CD & environments: policy, approvals, locks, secrets, drift | platform | P3 |
| Performance testing & tracking: bulk, bench, trends, Grafana | QA, platform | P4 |
| Statistical generation (with the **distribution playground**) | QA, dev | P4 |
| Profiling production shapes (spike posture + risk framing) | platform | P4 |
| Multi-domain identity alignment (with the **identity explorer** and **multi-domain map**) | dev, architects | P5 |
| API seeding how-to | dev, QA | P5 |
| Seed packs: consuming & authoring | dev, platform | P5 |
| TestContainers & the provider matrix | dev, platform | P5 |
| Testing complex domains (walks the new example domain) | QA, dev | P5 |

### 2.4 New example domain: Acme.Fulfilment (W5-D7) — P5

A third sample domain that exists to be documented: its own `DbContext`/database, API
persistence in the demo variant, and the edge cases the current samples dodge —
self-referencing hierarchy, server-assigned `long` keys, enum-heavy state flows,
`DateOnly`/`TimeOnly`, a correlated dataset, and a three-domain identity chain
(Orders → Billing → Fulfilment). CI builds and runs it; the "Testing complex domains"
guide walks it case by case, so the guide's features are the CI-executed ones.

### 2.5 Agent enablement kit (W5-D6) — P6

Distributable, versioned templates under `agent-kit/`: an `AGENTS.md` for consuming
repos (what TDM is, the command loop, how to read manifests/SARIF, guardrails) and
skill files (`tdm-feature-author`, `tdm-run-triage`, `tdm-perf-analyst`,
`tdm-domain-onboarding`) written to work for any agent runner that reads
`SKILL.md`-style instructions. `tdm init --agents` scaffolds them into a consuming repo.
Acceptance is empirical: a fresh agent session given only the kit must author and
validate a feature unaided.

### 2.6 Interactive components (W5-D5) — built in the phase that needs them

| Component | Teaches | Guide / phase |
|---|---|---|
| **Identity explorer** — type `domain | entity | key`, watch the UUIDv5 derive | the identity contract | Multi-domain guide, P5 (also embedded in the deck, P2) |
| **Distribution playground** — sliders for weights/lognormal/normal, live histogram | statistical generation | Statistical generation guide, P4 |
| **Pipeline walkthrough** — a step's journey: grammar → resolution → faker → overrides → persist → manifest | the core mental model | Home/concepts, P1 |
| **Multi-domain map** — domains, databases, APIs, DbContexts, identity edges | the domain reality | Multi-domain guide, P5 |

Each component is parity-anchored to the engine (the explorer must reproduce
`Tdm.Identity` test vectors; the playground mirrors `Distributions.cs` semantics) and
those anchors are asserted by tests or documented vectors.

---

## 3. Decisions log

| # | Decision | Rationale |
|---|---|---|
| W5-D1 | The MkDocs Material site is the single hub; interactives are self-contained assets, no CDNs | One place to search and link; zero-external-dependency posture matches W4-D1; Pages already wired |
| W5-D2 | Persona-first IA: routed home, guided tour chain, "where next" footers everywhere | Users arrive with a role and a task, not a feature name; the tour makes the set walkable end-to-end |
| W5-D3 | The forum deck is one self-contained HTML file, in-repo, served from the site | Survives projector laptops with no network; reviewable in PRs; dogfoods the report posture |
| W5-D4 | Every guide command is an executable snippet run by CI against the sample workspace | Docs that drift are worse than no docs; extends the W1 "docs cannot drift" posture from grammar to everything |
| W5-D5 | Vanilla JS/SVG first; vendor D3 only where a component needs it (force layout) | Fewer moving parts to maintain; the components are small and bespoke |
| W5-D6 | Agent kit ships as in-repo templates + `tdm init --agents` scaffolding, runner-agnostic | Distribution rides the tool teams already install; SKILL.md conventions work across agent runners |
| W5-D7 | Complex-domain documentation is anchored to a real, CI-executed example domain | A guide that walks living code cannot lie; edge cases get regression coverage for free |
| W5-D8 | One wave, six phases, per-phase handoffs in `docs/wave-5/` | Same incremental delivery discipline as the product waves; each phase lands usable value |

## 4. Phases

1. **W5-P1 — Foundations:** IA + nav rebuild, persona home, tour/footers, snippet-CI
   harness, pipeline walkthrough, deploy-on-push, Getting Started.
   → [`wave-5/p1-foundations.md`](wave-5/p1-foundations.md)
2. **W5-P2 — Engineering Forum deck:** the self-contained HTML slides + speaker notes.
   (Pull ahead of P1 if the forum date demands — it only needs P1's messaging spine.)
   → [`wave-5/p2-forum-deck.md`](wave-5/p2-forum-deck.md)
3. **W5-P3 — Core usage guides:** Daily use ×2, editor setup, CI guide, CD guide.
   → [`wave-5/p3-core-guides.md`](wave-5/p3-core-guides.md)
4. **W5-P4 — Performance & data-shape guides:** perf testing/tracking, statistical
   generation + playground, profiling posture.
   → [`wave-5/p4-performance-guides.md`](wave-5/p4-performance-guides.md)
5. **W5-P5 — Multi-domain & integration:** identity guide + explorer + map, API seeding,
   seed packs, TestContainers, Acme.Fulfilment + complex-domain guide.
   → [`wave-5/p5-multi-domain-guides.md`](wave-5/p5-multi-domain-guides.md)
6. **W5-P6 — Agent enablement & wrap:** agent kit, `init --agents`, guided-tour
   completion, cross-link audit, docs QA.
   → [`wave-5/p6-agent-enablement.md`](wave-5/p6-agent-enablement.md)

## 5. Acceptance criteria

- A fresh clone, following Getting Started only, reaches a green `tdm validate` and a
  seeded demo run in ≤15 minutes; every command on that path is a snippet the docs-CI job
  executed on the same commit.
- The forum deck opens from `file://` with zero network requests, presents in ≤20 minutes,
  prints cleanly to PDF, and every claim on a slide links (in notes) to the guide that
  substantiates it.
- From any persona landing section, any guide is reachable in ≤2 clicks; the guided tour
  visits every guide with no dead ends; `mkdocs build --strict` stays green.
- The identity explorer reproduces the `Tdm.Identity` unit-test vectors exactly; the
  distribution playground's lognormal median equals its `mean` slider per the §2.3
  convention.
- Acme.Fulfilment builds and seeds in CI on the SQLite leg, and every feature shown in
  "Testing complex domains" is one CI executed.
- A fresh agent session, given only `agent-kit/` contents and a sample-domain workspace,
  authors a new feature and reaches green `tdm validate` without human help.

## 6. Risks & mitigations

| Risk | Mitigation |
|---|---|
| Guides drift as the product moves | W5-D4 snippet-CI executes guide commands; `--strict` link checking; guides link design docs rather than restating internals |
| Interactive components rot (bespoke JS) | Few, small, framework-free; parity anchors tested; each component owned by exactly one guide |
| Deck and docs tell diverging stories | P1 produces a one-page messaging spine (`docs/wave-5/messaging.md`) that both deck and home page are written from |
| Example-domain scope creep | Fulfilment's edge-case list is capped in the P5 handoff; anything more is a new wave item |
| Agent kit tuned to one runner | Kit is plain markdown with explicit tool-agnostic phrasing; acceptance test uses a runner not used to write it, if available |
| Docs-site deploy cadence (release-only) hides new guides | P1 switches Pages deploy to push-on-main for docs paths |

## 7. Open items

- Whether the deck should also exist as a maintained PPTX export (proposed: no — print-to-PDF covers distribution).
- Screenshot/GIF capture pipeline for the VS Code guide (manual capture accepted for now; automation is a nice-to-have).
- A public "try it in the browser" sandbox — out of scope; revisit with the hosted-portal open item from Wave 4.
- Whether `agent-kit` skills should be published as a package (ride seed-pack style NuGet distribution) — decide after P6 lands.
