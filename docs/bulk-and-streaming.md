# Bulk insert & streaming generation (W3-D3 / W3-D4)

Count-bulk steps — `Given 1000000 Products exist with category "Load"` — run through a
streaming pipeline: generate → override → reference → identity → persist, in bounded chunks
of `run.bulkChunkSize`. Memory is **O(chunk), not O(count)**, at any row count.

## Provider-native insert (W3-D3)

```jsonc
"run": { "bulkStrategy": "Provider" }   // Provider (default) | EfCore
```

| Provider | Route | Mechanism |
|---|---|---|
| SQL Server | `SqlBulkCopy` | rows projected through the EF value converters into a `DataTable`, streamed over the context's connection |
| SQLite | `Sqlite(batch)` | multi-row `INSERT` statements (~900 parameters each); each chunk is atomic |
| PostgreSQL | `Npgsql(COPY)` | wire-level binary `COPY … FROM STDIN`, store-typed values; each chunk's stream is atomic ([providers](providers.md)) |
| anything else / ineligible entity | `DbContext(bulk)` | the portable chunked `AddRange`+`SaveChanges` path (v1) |

Inserters enlist in the scenario's transaction, so `Transactional` lifecycle rolls bulk rows
back like everything else. An entity falls back to the EF path when its key is
store-generated (bulk APIs don't propagate generated keys back — teardown and references
need them) or when a required column has no CLR property. `bulkStrategy: "EfCore"` forces
the portable path everywhere.

Bulk deliberately bypasses repositories (v1 semantics, D-bulk): row-wise write-repository
invariants don't scale to millions of rows. Entities that must go through their repository
should be seeded row-wise (a data table or count 1 steps).

## The manifest at volume (W3-D4)

```jsonc
"run": {
  "manifestBulkValues": "Sample",   // All | Sample (default) | None
  "manifestBulkSampleRows": 5       // rows kept with full values at each end
}
```

A million-row manifest is unusable; `Sample` keeps the audit trail meaningful: the first and
last N rows keep full values in `entities[]`, and every other row is folded — in ordinal
order — into a SHA-256 value hash recorded in the scenario's `bulkOperations[]` summary
(`requested`, `count`, `failed`, `persistedVia`, `sampledRows`, `hashedRows`,
`valuesSha256`, ordinal range, duration). Failed rows always keep their full entries,
whatever the mode. `All` restores v1 behaviour.

What sampling costs, made explicit — TDM warns on each:

- `tdm replay` / `tdm verify` can only replay/verify the sampled rows.
- `tdm teardown --manifest` can only delete the sampled rows (in-run `TrackedTeardown`
  is unaffected — see below). Use a delete-all step, or `All` mode, when bulk rows must be
  manifest-removable.

## Teardown at volume

Under `TrackedTeardown`, bulk rows are tracked by **primary key, not instance**, and removed
with set-based `DELETE … WHERE key IN (…)` batches at scenario end — no million-instance
tracking list, no row-wise deletes.

## Natural keys at volume

Identical natural keys derive identical deterministic ids (the identity contract), so a
faker whose key space is too small will birthday-collide at volume and fail the insert with
a unique violation (TDM's error says exactly this). Give volume-seeded entities a
collision-free natural key component — Bogus's `IndexFaker` is the pattern, still
deterministic under a seed:

```csharp
RuleFor(p => p.Sku, f => $"SKU-{f.IndexFaker:D7}-{f.Random.Replace("??").ToUpperInvariant()}");
```

Bulk rows are not held in the scenario context bag (that would be O(count) memory);
references to them resolve from the database.

## `tdm bench tune` (chunk-size auto-tune)

```bash
tdm bench tune --domain Orders --entity Product --rows 4000 --chunk-sizes 100,250,500,1000,2000
```

Inserts `--rows` rows at each chunk size (TrackedTeardown — every measurement cleans up
set-based), ranks by throughput and writes the winner into `run.bulkChunkSize` as a targeted
textual edit (comments survive). `--no-write` reports without touching the file. Point it at
a dev database.
