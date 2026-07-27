# Design records

The per-feature **design records** for TDM — the authoritative *why and how* behind each
capability, each keyed to the wave-decision (the `W…` codes) that introduced it. They are
historic in that they record decisions already made and shipped, and they remain the
standing design reference: the guides on the docs site link here for depth, and a CI audit
([`docs-site/lint-doclinks.sh`](../../docs-site/lint-doclinks.sh)) fails the build if any
record stops being linked from the site. They set out the overall design; the guides teach
how to *use* it.

Not decision records in the strict ADR sense (only `adr-0001` follows the
context/decision/consequences form) — most are design specs that carry the rationale for
the choice inline. `tdm-handoff.md` (below) holds the original v1 decisions (D1–D14);
everything from Wave 2 on has its own record.

Listed in the order the decisions were arrived at.

## Foundational

- [`tdm-handoff.md`](tdm-handoff.md) *(v1)* — the original implementation handoff: the whole
  v1 design and its decisions D1–D14 (the tool as first shipped, before the waves).
- [`adr-0001-data-access-via-repositories.md`](adr-0001-data-access-via-repositories.md) —
  TDM writes through your write repositories (audit stamps, validation, events live there),
  not straight to the `DbContext`. Enforced by the `validate` policy gate; exemptions are
  explicit. Reads stay on the context.

## Wave 2 — Trust

- [`audit-and-signing.md`](audit-and-signing.md) *(W2-P1)* — attribution, a always-on
  SHA-256 checksum, and optional detached signing that make the manifest safe to treat as
  evidence; `tdm manifest verify` checks both.
- [`policy-and-key-registry.md`](policy-and-key-registry.md) *(W2-P2)* — `tdm.policy.json`
  (validate-time governance) and the natural-key registry (`tdm.keys.json`, an always-on
  cross-team id contract).
- [`run-registry-and-locks.md`](run-registry-and-locks.md) *(W2-P3)* — a thin lease + index
  service (`Tdm.Registry`) answering *who seeded what, when* and stopping two runs from
  seeding the same database at once.
- [`secrets-and-playback.md`](secrets-and-playback.md) *(W2-P4)* — the secret-resolution
  chain (inline → env → host adapter) and manifest playback (`tdm replay` / `tdm verify`).

## Wave 3 — Scale & Performance

- [`parallel-execution.md`](parallel-execution.md) *(W3-D1/D2)* — concurrent scenarios on
  isolated runtime sessions; manifests still record plan order.
- [`bulk-and-streaming.md`](bulk-and-streaming.md) *(W3-D3/D4)* — provider-native bulk
  inserts and streaming generation with manifest sampling at volume.
- [`providers.md`](providers.md) *(W3-D5)* — database providers as plugin packages behind
  `IProviderBootstrap`; SQLite/SQL Server in-box, PostgreSQL as a plugin.
- [`resume-and-trends.md`](resume-and-trends.md) *(W3-D6/D7/D8)* — resumable runs (the JSONL
  journal), the benchmark trend store, and perf gates.

## Wave 4 — Product Depth

- [`living-report.md`](living-report.md) *(W4-D1)* — the self-contained HTML report rendered
  from a manifest.
- [`editor-support.md`](editor-support.md) *(W4-D2/D3)* — `tdm export-model`, the `tdm lsp`
  language server, and the VS Code extension.
- [`statistical-generation.md`](statistical-generation.md) *(W4-D4/D5)* — declarative
  distributions/weights/datasets over the faker output, and Bogus locales.
- [`api-seeding.md`](api-seeding.md) *(W4-D6)* — seeding through a domain's public API when
  direct DB writes are forbidden.
- [`seed-packs.md`](seed-packs.md) *(W4-D7)* — versioned, reusable packages of features,
  entity config and key-registry entries.
- [`subsetting-spike.md`](subsetting-spike.md) *(W4-D8, spike/prototype)* — `tdm profile`:
  read-only production-shape profiling into a statistics pack. Prototype, not GA.

## Cross-cutting reference

- [`compatibility.md`](compatibility.md) — the compatibility matrix (framework/provider/
  plugin versions).

---

**Adding a record.** New feature or design decision ⇒ add a Markdown file here, key it to
its wave-decision in the first line, link it from the guide/reference page whose feature it
documents (the `lint-doclinks` audit enforces this), and add a bullet above in decision
order. See the repo-root [`AGENTS.md`](../../AGENTS.md) for the working convention.
