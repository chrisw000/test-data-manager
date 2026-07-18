---
tour_prev: start/getting-started.md
tour_next: guides/daily-use-qa.md
---

# Concepts

Four ideas carry everything TDM does: **determinism**, **the grammar**, **the identity
contract**, and **the manifest**. This page explains each, then walks one real step
through the whole pipeline, stage by stage.

## Determinism

Every scenario runs under a seed (`@seed:42`, or `run.defaultSeed`). Generated values —
names, dates, totals, statistical draws — come from that seed, so the same feature file
produces the same rows on every machine, every run. Failures reproduce; baselines mean
something. We never say "random test data": everything is *deterministic under the
scenario seed*.

## The grammar

Steps are plain Gherkin matched by TDM's StepGrammar: create, reference, bulk, update,
delete, and verify verbs, with property overrides in business language
(`credit limit` ≡ `CreditLimit`). The [grammar reference](../reference/grammar.md) lists
every form — and every example there is a repository feature file CI executes.

## The identity contract

The id of a business object is derived, not assigned:

```text
id = UUIDv5("{domain}|{Entity}|{naturalKey}")
UUIDv5("Orders|Customer|Acme Ltd") = e47cf5ae-4475-54d3-8027-e09e3a4a1600
```

Two teams that have never spoken derive the same id for the same business object. That
is what lets an Orders row and a Billing row reference the same customer with **no
shared transaction and no coordination service** — each domain lives in its own
database, typically behind its own API, with its own `DbContext`, and their data still
agrees. Cross-domain work builds on this one idea; the
[multi-domain guide](../guides/multi-domain-identity.md) goes deep.

## The manifest

Every run writes a JSON manifest: full final values, seeds, persistence routes, ids,
attribution and attestation. It is *the* audit and reproducibility artifact — `tdm
replay` re-creates exactly its rows, `tdm verify` detects drift from it, `tdm teardown`
reverses it, `tdm report` renders it as a self-contained HTML file. The schema is toured
in [Reports & the manifest](../reference/reports-and-manifest.md).

## One step, end to end

The pipeline below is the journey of this real step from the repository's executed
sample features — click each stage to see the artifact it produces. The outputs shown
are captured from the same commands CI runs; ask for them yourself with:

```bash
--8<-- "concepts/01-explain-canonical.sh"
```

<div id="tdm-pipeline-walkthrough" data-tdm-walkthrough>
  <noscript>
    <p><em>The interactive walkthrough needs JavaScript. The same journey in prose:
    step text → StepGrammar match → entity resolution → faker/statistical layer →
    overrides → identity → persistence route → manifest entry — run
    <code>tdm explain</code> on any step to see stages 2–7 for real.</em></p>
  </noscript>
</div>

!!! info "Engineering record"
    The design docs behind this pipeline:
    [statistical-generation.md](https://github.com/chrisw000/test-data-manager/blob/main/docs/statistical-generation.md),
    [policy-and-key-registry.md](https://github.com/chrisw000/test-data-manager/blob/main/docs/policy-and-key-registry.md),
    [living-report.md](https://github.com/chrisw000/test-data-manager/blob/main/docs/living-report.md).

## Where next

- Do the [15-minute Getting started path](getting-started.md) if you haven't yet.
- QA? → [Daily use for QAs](../guides/daily-use-qa.md).
- Building a domain plugin? → [Convention profiles](../reference/profiles.md) and
  [plugin packaging](../reference/plugin-packaging.md).

**Guided tour:** next stop → [Daily use for QAs](../guides/daily-use-qa.md)
