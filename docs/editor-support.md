# Editor support: export-model, tdm lsp, VS Code (W4-D2 / W4-D3)

## `tdm export-model`

```bash
tdm export-model --settings tdm.settings.json --out tdm.model.json
```

Serialises the resolved entity map — logical names, properties + types, natural keys,
domains — to `tdm.model.json`. It is produced from the same resolved runtimes as
`tdm list-entities`, so editor tooling cannot drift from what the engine actually resolves.
Output is **fully deterministic** (ordinal-sorted, no timestamps, simple version without
build metadata), so the file is checked into the repo and CI regenerates it and fails on
diff (the staleness mitigation from the wave-4 risk table):

```yaml
- run: |
    tdm export-model --settings tdm.settings.json --out tdm.model.json
    git diff --exit-code tdm.model.json
```

The file records `settingsFileSha256` of the settings it was exported from — the staleness
signal editor clients compare against the current settings file.

## `tdm lsp`

```bash
tdm lsp --model tdm.model.json      # stdio; launched by editors, not interactively
```

A language server inside the existing dotnet tool (W4-D3) — one install, and any
LSP-capable editor (VS Code, Rider, Neovim, Helix) gets the same behaviour:

- **Diagnostics** — every step line runs through the *actual* `StepGrammar` (no
  reimplementation): unmatched steps warn; unknown/ambiguous entities, unknown properties
  (including DataTable column headers), unknown `for` reference targets and unknown
  `@domain:` tags squiggle against the model, with the same case/plural/space-tolerant
  `NameMatcher` matching the engine resolves with. Domain pins (`a Billing Customer ...`,
  `@domain:` tags) scope resolution exactly like at run time.
- **Completion** — entity names after `a`/`an`/`the following`/counts/`for` (plus domain
  qualifiers), property names inside `with` clauses resolved from the step's entity, and
  the tag vocabulary after `@`.
- **Hover** — verb documentation with examples (from the Wave 1 grammar reference), plus
  the resolved entity's model facts; external-reference hover shows the step's concrete
  identity-contract triple.

Implementation notes: hand-rolled LSP framing (~100 lines, `Tdm.Lsp.JsonRpcConnection`) —
no language-server framework dependency; full-document sync (feature files are small); the
model file is re-read whenever its timestamp changes, so `tdm export-model` takes effect
live. With no model file the server degrades to grammar-only diagnostics and surfaces one
`window/showMessage` warning. External-reference steps naming a domain that is not locally
modelled produce **no** diagnostic — cross-team references are the point of the identity
contract.

## VS Code extension (`editors/vscode`)

A thin client (~100 lines of JS): activates only for workspaces containing a
`tdm.settings.json`, launches `tdm lsp`, and claims only files under the settings'
`run.featurePaths` — Reqnroll/Cucumber extensions keep every other feature file. It also
compares the model's `settingsFileSha256` against the current settings file and shows a
staleness banner with a one-click regenerate. See `editors/vscode/README.md` for setup.
