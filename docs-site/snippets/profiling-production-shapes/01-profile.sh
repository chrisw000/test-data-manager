# Profiling reads a *populated* database read-only, so this seeds an isolated demo first,
# profiles it (emitting a statistics pack + an entities-config fragment — never row
# values), then tears the rows down. In real use you point --settings at a read replica.
settings=docs-site/snippets/profiling-production-shapes/tdm.profile.settings.json
tdm run --settings "$settings"
manifest=$(ls -t output/profile-demo-*.tdm.json | head -n 1)
# --8<-- [start:profile]
tdm profile --settings "$settings" --domain Orders --sample 1000 \
  --categorical-max 10 --out tdm.stats.json --fragment tdm.fragment.json
# --8<-- [end:profile]
tdm teardown --settings "$settings" --manifest "$manifest"
