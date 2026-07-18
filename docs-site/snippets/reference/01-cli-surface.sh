# W5-P1: the CLI reference cannot drift — every command `tdm --help` lists must have a
# section in reference/cli.md, and every documented command must still exist.
cli_md="docs-site/docs/reference/cli.md"
actual=$(tdm --help | awk '/^Commands:/{f=1;next} f && /^  [a-z]/{print $1}')
documented=$(sed -n 's/^## `tdm \([a-z-]*\).*/\1/p' "$cli_md" | sort -u)
for cmd in $actual; do
  grep -q "^## \`tdm $cmd" "$cli_md" || { echo "cli.md is missing a section for: tdm $cmd"; exit 1; }
done
for cmd in $documented; do
  echo "$actual" | grep -qx "$cmd" || { echo "cli.md documents a command that no longer exists: tdm $cmd"; exit 1; }
done
echo "cli.md matches tdm --help ($(echo "$actual" | wc -l) top-level commands)"
