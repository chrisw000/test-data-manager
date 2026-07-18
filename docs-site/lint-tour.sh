#!/usr/bin/env bash
# W5-P1 guided-tour lint: every guide (plus the Start-here walkable pages) carries
# tour_prev/tour_next front matter, and the chain is a single unbroken path — one start,
# one end, every member visited exactly once, prev/next reciprocal. Run by docs-CI so the
# tour cannot silently break. (The chain may grow as P3–P6 land; the shape rule is fixed.)
set -euo pipefail
cd "$(dirname "$0")/docs"

members=(start/getting-started.md start/concepts.md)
while IFS= read -r f; do members+=("$f"); done < <(find guides -name '*.md' | sort)

declare -A tour_next tour_prev in_tour
fail() { echo "lint-tour: $1" >&2; exit 1; }

for f in "${members[@]}"; do
  [[ -f "$f" ]] || fail "tour member does not exist: $f"
  fm=$(awk '/^---[[:space:]]*$/{n++; next} n==1{print} n>=2{exit}' "$f")
  next=$(sed -n 's/^tour_next:[[:space:]]*//p' <<<"$fm")
  prev=$(sed -n 's/^tour_prev:[[:space:]]*//p' <<<"$fm")
  [[ -n "$next" || -n "$prev" ]] || fail "$f has no tour_prev/tour_next front matter"
  tour_next[$f]=$next
  tour_prev[$f]=$prev
  in_tour[$f]=1
done

start="" end=""
for f in "${members[@]}"; do
  if [[ -z "${tour_prev[$f]}" ]]; then
    [[ -z "$start" ]] || fail "two chain starts: $start and $f"
    start=$f
  fi
  if [[ -z "${tour_next[$f]}" ]]; then
    [[ -z "$end" ]] || fail "two chain ends: $end and $f"
    end=$f
  fi
done
[[ -n "$start" ]] || fail "no chain start (every page has tour_prev — cycle?)"
[[ -n "$end" ]] || fail "no chain end (every page has tour_next — cycle?)"

declare -A walked
visited=0
current=$start
prev=""
while [[ -n "$current" ]]; do
  [[ -n "${in_tour[$current]:-}" ]] || fail "chain leaves the tour set at: $current"
  [[ -z "${walked[$current]:-}" ]] || fail "cycle detected: $current visited twice"
  [[ "${tour_prev[$current]}" == "$prev" ]] || \
    fail "$current: tour_prev is '${tour_prev[$current]}' but the chain arrives from '$prev'"
  walked[$current]=1
  visited=$((visited + 1))
  prev=$current
  current=${tour_next[$current]}
done

if [[ $visited -ne ${#members[@]} ]]; then
  orphans=()
  for f in "${members[@]}"; do [[ -n "${walked[$f]:-}" ]] || orphans+=("$f"); done
  fail "chain visits $visited of ${#members[@]} pages — orphaned: ${orphans[*]}"
fi

echo "lint-tour: chain OK — $visited pages, $start → $end"
