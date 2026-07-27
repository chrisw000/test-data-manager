#!/usr/bin/env bash
# W5-P6 design-doc cross-link audit: every feature design doc under /docs must be reachable
# from the published site — linked from at least one guide/page as a
# blob/main/docs/<name>.md reference. A design doc nobody can navigate to from the docs is
# an orphan. Handoffs and process notes are excluded (they document the build, not a
# feature). Run by docs-CI so the docs and design record cannot silently drift apart.
set -euo pipefail
cd "$(dirname "$0")/.."   # repo root

site_dir="docs-site/docs"
orphans=()
checked=0

for doc in docs/*.md; do
  name=$(basename "$doc")
  case "$name" in
    wave-*-handoff.md|next_steps.md) continue ;;   # process docs, not feature design
  esac
  checked=$((checked + 1))
  # A live reference is a GitHub blob link to this doc from anywhere on the site.
  if ! grep -rqF "docs/$name" "$site_dir"; then
    orphans+=("$doc")
  fi
done

if [[ ${#orphans[@]} -gt 0 ]]; then
  echo "lint-doclinks: FAIL — design docs not linked from any site page:" >&2
  printf '  - %s\n' "${orphans[@]}" >&2
  echo "Link each from the guide/reference page whose feature it documents." >&2
  exit 1
fi

echo "lint-doclinks: OK — all $checked design docs linked from the site."
