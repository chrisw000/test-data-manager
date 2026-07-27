# The guided tour

**Walk the whole product in about ninety minutes.** Every guide carries a **prev/next**
chain in its front matter, so you can read the documentation set end to end without dead
ends — a lint script in docs-CI keeps the chain a single unbroken path. Start at the top;
each page's *"Guided tour: next stop"* footer takes you on. Every command you meet is
executed by CI against this repo's sample workspace on each push, so nothing you read here
has drifted from what the tool does.

The path runs from first principles to the deep end, then hands the whole product to your
agent:

**Foundations** — what TDM is and the ideas everything rests on:

1. [Getting started](getting-started.md) — clone to seeded run in ≤15 minutes
2. [Concepts](concepts.md) — determinism, the grammar, the identity contract, the manifest

**Daily use** — the loop each persona lives in:

3. [Daily use for QAs](../guides/daily-use-qa.md)
4. [Daily use for developers](../guides/daily-use-dev.md)
5. [Editor setup](../guides/editor-setup.md)

**Golden paths** — TDM in a pipeline:

6. [CI — validate, report, gate](../guides/ci.md)
7. [CD & environments](../guides/cd-environments.md)

**Scale & realism** — making seeded data big and lifelike:

8. [Performance testing & tracking](../guides/performance-testing.md)
9. [Statistical generation](../guides/statistical-generation.md)
10. [Profiling production shapes](../guides/profiling-production-shapes.md)

**Many domains** — the deep end, where the identity contract earns its keep:

11. [Multi-domain identity alignment](../guides/multi-domain-identity.md)
12. [API seeding](../guides/api-seeding.md)
13. [Seed packs](../guides/seed-packs.md)
14. [TestContainers & the provider matrix](../guides/testcontainers.md)
15. [Testing complex domains](../guides/complex-domains.md)

**Hand it over** — the last persona is your agent:

16. [Agents & the agent-kit](../agents/index.md) — the operating kit for agentic coders
    and testers, and `tdm init --agents` to scaffold it.

Ninety minutes end to end, or drop in at the section that matches today's task — the
[home page](../index.md) routes you straight there by persona.
