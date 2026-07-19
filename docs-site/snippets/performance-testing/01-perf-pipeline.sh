# End-to-end perf pipeline against an isolated 5k-row workspace: seed with benchmarks,
# publish to a trend store to establish a baseline, seed again, compare the new run
# against the stored rolling baseline (perf gates in tdm.perf.policy.json), then publish
# it too. Bounded sizes and a generous gate keep it green under CI perf variance.
settings=docs-site/snippets/performance-testing/tdm.perf.settings.json
policy=docs-site/snippets/performance-testing/tdm.perf.policy.json
store=./output/perf-store

# 1 · Establish a baseline: seed with --benchmark, then publish.
tdm run --settings "$settings"
baseline=$(ls -t output/perf-demo-*.tdm.json | head -n 1)
tdm publish --manifest "$baseline" --store "$store" --env perf
tdm teardown --settings "$settings" --manifest "$baseline"

# 2 · A later run: seed again, then gate it against the stored baseline.
tdm run --settings "$settings"
current=$(ls -t output/perf-demo-*.tdm.json | head -n 1)
# --8<-- [start:gate]
tdm bench compare --manifest "$current" --store "$store" \
  --env perf --policy-file "$policy" --stat p95Ms
# --8<-- [end:gate]
tdm publish --manifest "$current" --store "$store" --env perf
tdm teardown --settings "$settings" --manifest "$current"
