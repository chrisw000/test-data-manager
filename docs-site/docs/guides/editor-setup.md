---
tour_prev: guides/daily-use-dev.md
tour_next: guides/ci.md
---

# Editor setup

**Personas:** QA, developer. Unknown entities and properties become squiggles as you
type, instead of unmatched steps at runtime — the single biggest friction, gone. It is
powered by the *actual* `StepGrammar` and your exported model, so the editor and the
runtime never disagree.

## How it works (one moving part)

Everything lives in the dotnet tool. `tdm lsp` is a stdio language server; the editor
client just launches it. Both read `tdm.model.json` — a deterministic export of your
resolved schema — so there is no database connection and no second implementation of the
grammar to drift.

```bash
--8<-- "editor-setup/01-export-model.sh"
```

Regenerate the model whenever entities, properties or config change. The server re-reads
it live; CI regenerates and diffs it (see [keeping the model honest](#keeping-the-model-honest)).

## VS Code

1. `dotnet tool install --global Tdm.Tool` (provides `tdm`).
2. Export the model (above).
3. Install the **TDM Feature Files** extension (in-repo at `editors/vscode`; from source:
   `npm install`, then `npx @vscode/vsce package` and install the `.vsix`).

The extension only activates for workspaces containing a `tdm.settings.json`, and only
claims `*.feature` files under that settings file's `run.featurePaths` — so
Reqnroll/Cucumber extensions keep ownership of your executable specs. Settings:

| Setting | Default | Purpose |
|---|---|---|
| `tdm.toolPath` | `tdm` | Path to the `tdm` executable |
| `tdm.modelPath` | `tdm.model.json` | Workspace-relative model file |

### What each cue means

- **Diagnostics (squiggles)** — unmatched steps, unknown or ambiguous entities, unknown
  properties (including DataTable column headers), unknown reference targets, and
  unknown `@domain:` tags.
- **Completion** — entity names after `a` / `an` / `the following` / counts / `for`;
  property names inside `with` clauses; the tag vocabulary after `@`.
- **Hover** — verb documentation with examples, plus the resolved entity's model facts
  (natural key, key strategy, persistence route).

### The staleness banner

If the model was exported from a different `tdm.settings.json` than the workspace
currently has, the extension shows a banner offering to regenerate it. That protects you
from a model that silently lags your config — accept it, and the diagnostics reflect
reality again.

## Any LSP editor (Rider, Neovim, Helix)

The server is editor-agnostic — stdio, no arguments beyond the model path:

```bash
tdm lsp --model tdm.model.json
```

Point your editor's LSP client at that command for `gherkin`/`*.feature` documents.
Sketches:

- **Neovim (`nvim-lspconfig`)** — a custom server whose `cmd` is
  `{ "tdm", "lsp", "--model", "tdm.model.json" }`, `filetypes = { "cucumber" }`,
  root detected by `tdm.settings.json`.
- **Helix** — a `language-server` entry running `tdm` with args `["lsp", "--model",
  "tdm.model.json"]`, attached to the `gherkin` language.
- **Rider / IntelliJ** — any generic LSP plugin pointed at the same command.

The model path is relative to the server's working directory; use an absolute path if
your editor launches servers elsewhere.

## Keeping the model honest

The model is a build artifact, so treat it like one: commit it, and let CI fail the PR
if it drifts from the resolved schema.

```bash
--8<-- "ci/02-model-drift.sh"
```

This repository runs exactly that check ([CI guide](ci.md)); the design rationale is in
[editor-support.md](https://github.com/chrisw000/test-data-manager/blob/main/docs/editor-support.md).

## Where next

- [CI — validate, report, gate](ci.md) — the same model-drift check, in the pipeline.
- [Daily use for QAs](daily-use-qa.md) — the feedback loop the editor accelerates.
- [CLI reference: `tdm lsp` / `export-model`](../reference/cli.md).

**Guided tour:** next stop → [CI — validate, report, gate](ci.md)
