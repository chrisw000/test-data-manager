#!/usr/bin/env bash
# W5-P6 design-record cross-link audit: every design record under /docs/design must be
# reachable from the published site — linked from at least one guide/page as a
# blob/main/docs/design/<name>.md reference. A design record nobody can navigate to from the
# docs is an orphan. The folder README (an index, not a feature record) is excluded. Run by
# docs-CI so the docs and the design record cannot silently drift apart.
set -euo pipefail
cd "$(dirname "$0")/.."   # repo root

site_dir="docs-site/docs"
orphans=()
checked=0

for doc in docs/design/*.md; do
  name=$(basename "$doc")
  case "$name" in
    README.md) continue ;;   # the folder index, not a feature record
  esac
  checked=$((checked + 1))
  # A live reference is a GitHub blob link to this record from anywhere on the site.
  if ! grep -rqF "docs/design/$name" "$site_dir"; then
    orphans+=("$doc")
  fi
done

if [[ ${#orphans[@]} -gt 0 ]]; then
  echo "lint-doclinks: FAIL — design records not linked from any site page:" >&2
  printf '  - %s\n' "${orphans[@]}" >&2
  echo "Link each from the guide/reference page whose feature it documents." >&2
  exit 1
fi

echo "lint-doclinks: OK — all $checked design records linked from the site."
