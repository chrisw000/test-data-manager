# Build-and-seed the Acme.Fulfilment complex-example domain on SQLite (TrackedTeardown, so
# it cleans up after itself). Every feature the "Testing complex domains" guide walks is
# this one, so the guide cannot describe anything CI does not run.
tdm run --settings docs-site/snippets/complex-domains/tdm.fulfilment.settings.json
