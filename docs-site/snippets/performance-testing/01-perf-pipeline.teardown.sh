# Remove the isolated perf artifacts (rows already torn down inside the pipeline snippet).
rm -f output/perf-demo-*.tdm.json output/perf-demo-*.tdm.json.sha256 output/perf-demo-*.tdm.journal.jsonl output/perf-demo.db
rm -rf output/perf-store
