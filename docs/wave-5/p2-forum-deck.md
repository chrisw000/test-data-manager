# W5-P2 — Engineering Forum deck

**Parent:** [wave-5-handoff.md](../wave-5-handoff.md) · Decision W5-D3
**Depends on:** P1's messaging spine (`docs/wave-5/messaging.md`). If the forum date
arrives first, write the spine as part of this phase and P1 inherits it.

One self-contained HTML file: `docs-site/docs/slides/engineering-forum.html`. Keyboard
navigation (←/→, Home/End), slide counter, `S` toggles speaker notes, print stylesheet
renders one slide per page. Zero network requests — works from `file://` on a podium
laptop. Inline SVG diagrams reuse the visual language of the W4-P1 report (same palette
variables, light/dark).

## Slide plan (≤20 minutes, ~15 slides)

| # | Slide | Content beats | Visual |
|---|---|---|---|
| 1 | Title | TDM — test data as executable language | logo-free wordmark |
| 2 | The problem | hand-rolled SQL seeds; snapshot restores; copied prod data; flaky test data nobody owns | "before" collage |
| 3 | What TDM is | Gherkin verbs → deterministic, audited data in your domain's own model | one real feature, 6 lines |
| 4 | **QA test seeding** | the test's data in the test's language; create/update/delete/verify verbs; lifecycles (persistent / tracked teardown) | feature ↔ resulting rows split-screen |
| 5 | Determinism | same seed, same data — byte-for-byte; the manifest records everything | two runs, identical manifest excerpt |
| 6 | **Natural keys** — the concept | "Acme Ltd" is the name your test *means*; TDM resolves it wherever it lives | step → row lookup animation (static SVG frames) |
| 7 | **The identity contract** | UUIDv5(domain\|entity\|key): both domains derive the same id independently — cross-team alignment with zero runtime coordination | the derivation, live-ish (embed identity-explorer mini) |
| 8 | Multi-domain reality | many domains, each its own DB/API/DbContext; external references + projections stitch them | multi-domain map (static export) |
| 9 | **VS Code helper** | squiggles for unknown entities/properties in <1 s; completion from *your* schema; hover docs; one dotnet tool, any LSP editor | annotated screenshot / GIF |
| 10 | **CI operation** | validate gate with no database; SARIF annotations on the PR diff; JUnit in the test tab; policy gates before persistence | PR screenshot with TDM annotations |
| 11 | **Reporting** | the living HTML report: run header, drill-down, reference lineage graph; audit posture (checksums, signing, attribution) | lineage graph SVG from a real run |
| 12 | **Statistical generation** | weights/distributions/datasets in config; 10k orders land within 2% of declared weights — and identically on the same seed; perf data that looks like prod without being prod | distribution playground still + the 60/30/10 bar chart |
| 13 | Realism, safely | `tdm profile` spike: shapes from production, never rows; attribution keeps the audit trail honest | capture/refuse table condensed |
| 14 | **Deeper dive menu** | environment policy & approvals · run registry + locks · secrets chain · replay & drift verify · resumable runs · trend store + perf gates · API seeding · seed packs · provider plugins · agent kit | menu grid, one line each |
| 15 | Getting started | the ≤15-minute path; docs-site URL + QR; "bring a domain to the deep dive" | link + QR (inline SVG QR) |

## Speaker notes

Every slide carries notes with: the one-sentence takeaway, the demo fallback (what to say
if the live bit fails), and the docs-site link that substantiates the claim (deck and
guides must not diverge — W5 risk table).

## Acceptance

- Opens from `file://`, no console errors, zero external requests (verify with devtools).
- ≤20-minute read-through at presenter pace; prints to PDF one-slide-per-page.
- Identity mini-explorer on slide 7 reproduces a `Tdm.Identity` test vector.
- Linked from the docs-site nav (`Slides`), and the repo README.
