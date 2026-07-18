# A drift check verifies that a *persisted* run's rows are still intact, so this seeds
# them first; in a real pipeline you verify the manifest your deploy produced. verify
# writes no manifest, so the persistent run stays newest through verify and teardown.
tdm run --lifecycle Persistent
# --8<-- [start:cmd]
tdm verify --manifest "$(ls -t output/*.tdm.json | head -n 1)"
# --8<-- [end:cmd]
tdm teardown --manifest "$(ls -t output/*.tdm.json | head -n 1)"
