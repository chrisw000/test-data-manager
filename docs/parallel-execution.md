# Parallel scenario execution (W3-D1 / W3-D2)

`run.maxParallelScenarios` (default `1`) runs scenarios concurrently. The default preserves
strict serial execution — nothing changes until you opt in.

```jsonc
"run":     { "maxParallelScenarios": 8 },
"domains": [{ "name": "Legacy", "maxParallelScenarios": 1 }]   // optional per-domain cap
```

## The model

- **The scenario is the unit of parallelism (W3-D1).** Steps within a scenario stay strictly
  sequential — references depend on step order. Per-scenario seeds (v1 D8) already make
  scenarios order-independent in their *generated data*; this makes them
  execution-independent too.
- **One session per in-flight scenario (W3-D2).** A domain's expensive parts — the EF model,
  entity/repository/faker bindings — are built once and shared immutably. Each scenario runs
  on its own session holding the cheap per-scenario state: DbContexts, transactions, faker
  instances, tracked rows. No shared mutable runtime, no lock contention.
- **Deterministic manifests.** Scenarios are recorded in plan order regardless of completion
  order. Ordinal-derived identities are scenario-scoped by design, so a parallel run yields
  the same values and identities as a serial run — modulo timings (wall-clock audit stamps
  and time-anchored fakers such as `f.Date.Recent()` differ by the moments between runs, as
  they do between any two runs; pin `Bogus.DataSets.Date.SystemClock` if you need bytewise
  date stability).
- **Failure policy.** `FailRun` aborts the run: in-flight scenarios finish and are recorded
  as they ended; not-yet-started scenarios are recorded as `Skipped` with a warning.

## Caps and auto-serialisation

The effective parallelism is `run.maxParallelScenarios`, further capped by every
participating domain's `maxParallelScenarios` — a fragile database serialises the whole run
without anyone touching run settings.

Transactional scenarios on SQLite auto-serialise with a warning: SQLite is single-writer,
and parallel scenarios would hold competing write transactions for their entire lifetime.

## What parallelism is for — and what it isn't

Parallelism suits **disjoint seeding**: scenarios that create their own rows under their own
natural keys (bulk volume, per-team fixtures). Scenarios that target the *same* natural keys
can still contend at the database — idempotent create-or-reuse makes same-key collisions
converge rather than fail, but the winner of a race is not deterministic. If scenario B
*depends on* rows scenario A creates, keep the run serial (or put both sets of steps in one
scenario — steps are always ordered).
