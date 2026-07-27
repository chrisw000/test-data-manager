# Developing TDM — agent & contributor guide

Operating notes for anyone (human or agent) working **on this repository**. This is the
codebase of the Test Data Manager tool itself.

> Not to be confused with [`agent-kit/AGENTS.md`](agent-kit/AGENTS.md), which is the kit
> *shipped to repos that consume TDM* — it tells an agent how to *use* TDM, not develop it.

## Build & test

- **Build:** `dotnet build Tdm.slnx -warnaserror`. Warnings are errors; CI rejects any.
- **Test:** `dotnet test Tdm.slnx --no-build` (currently ~445 tests across the suites).
- **Target:** `net10.0`, `Nullable` enabled.
- **Package versions are centrally managed** in `Directory.Packages.props` — add/upgrade
  dependencies there; `PackageReference`s in `.csproj` files carry **no** `Version`.
  `Directory.Build.props` holds shared MSBuild settings.

## The footgun: model drift

Editing `tdm.settings.json` changes its hash, which changes `tdm.model.json`'s
`settingsFileSha256`. CI regenerates the model (`tdm export-model`) and runs
`git diff --exit-code tdm.model.json`. **If you touch `tdm.settings.json`, regenerate and
stage `tdm.model.json` in the same commit** or the drift check fails.

## Path resolution is split (surprising, and it bites)

- `pluginPath`, `featurePaths`, `outputPath` resolve against the **settings file's
  directory**.
- SQLite **connection strings** resolve against the **process CWD**.

Mixing these up makes files land somewhere unexpected. Isolated workspaces (e.g. the docs
snippet workspaces) rely on getting this right.

## Docs cannot drift — it is mechanically enforced

The published docs live in `docs-site/` (MkDocs Material). Four CI gates in the
`docs-verify` job:

- **`mkdocs build --strict`** — broken internal links or nav fail the build.
- **Executable snippets** — guide commands live in `docs-site/snippets/**/*.sh`, are
  `--8<--`-included into pages, and are **run** by `docs-site/run-snippets.sh` against the
  sample workspace. Document a command ⇒ it must run as a snippet.
- **`lint-tour.sh`** (+ `.ps1` twin) — every guide carries reciprocal `tour_prev`/
  `tour_next` front matter forming one unbroken chain.
- **`lint-doclinks.sh`** — every design record under `docs/design/` must be linked from a
  site page (no orphaned design records).

## Design records live in `docs/design/`

The per-feature design/decision records are in [`docs/design/`](docs/design/) with a
[README index](docs/design/README.md) in decision order. **Maintaining them, ongoing:**

- A new feature or non-trivial design decision ⇒ **add a Markdown record** to
  `docs/design/`, key it to its wave-decision (the `W…` code) in the first line, and add a
  bullet to `docs/design/README.md` in decision order.
- **Link it from the site.** Reference the record (as a `blob/main/docs/design/<name>.md`
  link) from the guide/reference page whose feature it documents — `lint-doclinks` fails CI
  otherwise. Source-code comments that cite a record use the same `docs/design/<name>.md`
  path.
- Keep descriptive filenames (don't renumber); `adr-0001` keeps its ADR identity. Chronology
  lives in the README index, not in filename prefixes.
- These records are the standing "why"; the guides are the "how to use." Update the record
  when the design changes, not just the guide.

## Other things worth knowing

- **Sample domains are plugins.** `Acme.Orders`, `Acme.Billing`, `Acme.Fulfilment` (under
  `tests/`) load from `bin/Debug/net10.0` — `dotnet build` before `run`/`validate` against
  them. `Acme.Fulfilment` is the complex-edge-case domain, kept out of the default demo run.
- **`Tdm.Host` is an Exe** (assembly `tdm`); to unit-test its internals, reference it and
  add `InternalsVisibleTo` (see `Tdm.Host.Tests`).
- **The identity contract namespace GUID is frozen** (`Tdm.Identity`). Changing it silently
  re-derives every cross-repo id — never touch it without a major-version story.
- **Provider matrix:** `TDM_TEST_PROVIDER` selects the provider (Sqlite default; SqlServer /
  PostgreSql via Testcontainers in the `provider-matrix` CI job).
- **`.csproj` XML comments cannot contain `--`** (e.g. writing about a `--flag`); it's an
  XML parse error.
- **Line endings:** `.gitattributes` normalises to LF in the repo. Windows is the primary
  dev environment (PowerShell), but `bash` is available and CI runs on Linux — keep `.sh`
  scripts POSIX and LF.
- **Commits:** work is organised in waves; commit per phase, and end commit messages with
  the `Co-Authored-By:` trailer.

## Where the design is written down

- [`docs/design/`](docs/design/) — per-feature design records (this repo's design of record),
  including [`tdm-handoff.md`](docs/design/tdm-handoff.md), the original v1 implementation
  handoff (decisions D1–D14).
- The published site: <https://chrisw000.github.io/test-data-manager/> (guides + reference +
  the agent kit).
