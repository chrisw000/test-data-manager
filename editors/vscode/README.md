# TDM Feature Files (VS Code)

Thin client for the TDM language server (W4-D3). Everything lives in the dotnet tool —
this extension (~100 lines) just launches `tdm lsp` for workspaces that contain a
`tdm.settings.json`, and only claims `*.feature` files under the settings'
`run.featurePaths` (so Reqnroll/Cucumber extensions keep the rest).

What you get, powered by the *actual* `StepGrammar` and the exported `tdm.model.json`:

- **Diagnostics** — unmatched steps, unknown/ambiguous entities, unknown properties
  (including DataTable column headers), unknown reference targets and `@domain:` tags.
- **Completion** — entity names after `a`/`an`/`the following`/counts/`for`, property
  names inside `with` clauses, the tag vocabulary after `@`.
- **Hover** — verb documentation with examples, plus the resolved entity's model facts.

## Setup

```bash
dotnet tool install -g Tdm.Tool          # provides `tdm`
tdm export-model --settings tdm.settings.json --out tdm.model.json
```

Then install this extension (from source: `npm install`, then `npx @vscode/vsce package`
and install the `.vsix`). Settings:

| Setting | Default | Purpose |
|---|---|---|
| `tdm.toolPath` | `tdm` | Path to the tdm executable |
| `tdm.modelPath` | `tdm.model.json` | Workspace-relative model file |

The model file is re-read live whenever it changes. If it was exported from a different
`tdm.settings.json` than the workspace's current one, a staleness banner offers to
regenerate it — CI should also regenerate and `git diff --exit-code` it (see
`docs/editor-support.md`).

Other LSP-capable editors (Rider, Neovim, Helix) can use `tdm lsp --model tdm.model.json`
directly — stdio, no arguments beyond the model path.
