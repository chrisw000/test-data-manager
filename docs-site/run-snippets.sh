#!/usr/bin/env bash
# W5-D4 snippet-verification harness: executes every docs-site/snippets/**/*.sh in sorted
# order against the repository's sample workspace, then runs the paired *.teardown.sh
# files in reverse. Every command a guide shows is one of these files (included into the
# page via pymdownx.snippets), so a guide that breaks fails CI — docs cannot drift.
#
# Requires the solution to be built first (the `tdm` shim uses --no-build).
set -euo pipefail
cd "$(dirname "$0")/.."

shim_dir="$(mktemp -d)"
trap 'rm -rf "$shim_dir"' EXIT
cat > "$shim_dir/tdm" <<'EOF'
#!/usr/bin/env bash
exec dotnet run --project src/Tdm.Host --no-build -- "$@"
EOF
chmod +x "$shim_dir/tdm"
export PATH="$shim_dir:$PATH"

mapfile -t snippets < <(find docs-site/snippets -name '*.sh' ! -name '*.teardown.sh' | sort)
if [[ ${#snippets[@]} -eq 0 ]]; then
  echo "no snippets found under docs-site/snippets" >&2
  exit 1
fi

teardowns=()
for snippet in "${snippets[@]}"; do
  echo "::group::$snippet"
  bash -euo pipefail "$snippet"
  echo "::endgroup::"
  paired="${snippet%.sh}.teardown.sh"
  [[ -f "$paired" ]] && teardowns+=("$paired")
done

for ((i = ${#teardowns[@]} - 1; i >= 0; i--)); do
  echo "::group::${teardowns[$i]}"
  bash -euo pipefail "${teardowns[$i]}"
  echo "::endgroup::"
done

echo "all ${#snippets[@]} snippet(s) executed"
