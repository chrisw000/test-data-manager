# Acceptance lint (W5-P5): the complex-domains guide may only reference features that
# actually exist under the domain workspace — a guide that cites a missing feature lies.
guide=docs-site/docs/guides/complex-domains.md
feature_dir=docs-site/snippets/complex-domains
missing=0
# Every `feature: NAME` mention in the guide must exist as a Feature: in fulfilment.feature.
grep -oE 'exercised by \*\*`[^`]+`\*\*' "$guide" | sed -E 's/.*`([^`]+)`.*/\1/' | while read -r scenario; do
  grep -qF "$scenario" "$feature_dir/fulfilment.feature" || { echo "guide cites missing scenario: $scenario"; exit 1; }
done
# The settings, feature and dataset the guide names must all be present.
for f in tdm.fulfilment.settings.json fulfilment.feature datasets/carriers.csv; do
  [ -f "$feature_dir/$f" ] || { echo "guide workspace file missing: $f"; missing=1; }
done
[ "$missing" -eq 0 ] && echo "complex-domains guide: all cited features/files exist"
exit "$missing"
