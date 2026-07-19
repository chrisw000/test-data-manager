---
tour_prev: guides/performance-testing.md
tour_next: guides/profiling-production-shapes.md
---

# Statistical generation

**Personas:** QA, developer. Production-shaped data with test-grade determinism —
declared in config, drawn from the scenario seed, no code required for the common cases.

## Why shape matters

Uniform-random test data hides the problems real data causes. Skew drives index
selectivity, hot partitions, and cache behaviour; a status column that is 90 %
`Completed` exercises very different query plans than an even split. If your perf test's
data is shaped wrong, its numbers are reassuring and meaningless. Statistical generation
lets you declare the shape — and keep it reproducible.

## Try it: the distribution playground

Drag the sliders; the histogram redraws. Toggle **same seed** off and on to watch
determinism directly — with it locked, every redraw is byte-identical; unlocked, each
draw is fresh. For `lognormal`, the readout shows the observed median tracking your
**mean/median** input — because in TDM's config the lognormal `mean` *is* the median.

<div id="tdm-distribution-playground" data-tdm-playground>
  <noscript>
    <p><em>The playground needs JavaScript. The shapes it draws are the same ones
    <code>entities.{X}.properties</code> produce: <code>normal</code>, <code>lognormal</code>
    (where <code>mean</code> is the median), <code>uniform</code>, <code>exponential</code>,
    and weighted categoricals.</em></p>
  </noscript>
</div>

!!! note "A mirror, not the engine"
    The playground reproduces the *shapes* and the median-equals-mean convention of
    `Tdm.Core.Generation.Distributions`, using its own seedable PRNG. A docs-verify test
    asserts its lognormal median tracks the mean across the slider range and its weighted
    draw honours declared proportions within 2 % — so the teaching tool cannot drift from
    the semantics it teaches.

## Config walkthrough

Everything lives under `entities.{X}.properties`, keyed by property. Exactly one of
`distribution`, `weights` or `dataset` applies to a property:

```jsonc
"Order": {
  "naturalKey": "OrderNumber",
  "properties": {
    // Weighted categorical: values in the declared proportions.
    "Status": { "weights": { "Pending": 0.6, "Shipped": 0.3, "Cancelled": 0.1 } },
    // Long right tail: half of all orders below 120, clamped non-negative.
    "Total":  { "distribution": "lognormal", "mean": 120, "sigma": 1.2, "min": 0, "decimals": 2 }
  }
}
```

| Distribution | `mean` means | Other keys |
|---|---|---|
| `normal` | the mean | `sigma` (std dev) |
| `lognormal` | the **median** (scale, `exp(μ)`) | `sigma` (σ of the underlying normal) |
| `uniform` | — | `min`, `max` (bounds) |
| `exponential` | the mean (`1/rate`) | — |

`min`/`max` clamp any distribution; `decimals` rounds floating targets (integers round
whole). Weighted values are normalised at sample time, so they needn't sum to 1.

### Correlated datasets & locale

When several fields must agree (city ↔ postcode ↔ country), a **dataset** samples one CSV
row whole:

```jsonc
"datasets": { "uk-places": { "path": "./datasets/uk-places.csv" } },
"entities": {
  "Address": { "properties": {
    "City":     { "dataset": "uk-places", "column": "City" },
    "Postcode": { "dataset": "uk-places", "column": "Postcode" }
  } }
}
```

All properties of one entity naming the same dataset are filled from a single sampled
row. Per-domain `locale` (`en_GB`, `de`, `fr`, …) picks the Bogus vocabulary for
generated names/addresses; the per-scenario Randomizer still picks from it, so
determinism is intact.

### The layering rule

For any property, the last applicable layer wins:

```
faker  <  generator plugins  <  distributions / weights / datasets  <  step overrides
```

So the statistical layer refines a faker's output, and an explicit value in a Gherkin
step always beats everything. Confirm what a given entity resolves to:

```bash
--8<-- "statistical-generation/01-explain-order.sh"
```

## Code, for the complex 5 %

When a property needs logic (SKU formats, check digits), ship an `IValueGeneratorPlugin`
in your domain assembly — consulted *before* the auto-faker heuristics:

```csharp
public sealed class SkuGenerator : Tdm.Core.Generation.IValueGeneratorPlugin
{
    public string Name => "AcmeSkus";
    public bool Matches(ValueGenerationContext ctx) =>
        ctx.Entity == "Product" && ctx.Property.Name == "Sku";
    public object? Generate(ValueGenerationContext ctx, Randomizer random) =>
        $"ACME-{random.Int(10_000, 99_999)}";
}
```

Draw **only** from the supplied `Randomizer` — that is the determinism contract.
Returning `null` falls through to the next plugin, then the heuristics.

## Verifying shape in tests

- **Assert proportions**, not exact rows: over 10,000 orders the weighted `Status` lands
  within ~2 % of the declared split, and identically on the same seed — the playground's
  parity test asserts exactly this property.
- **`fakerSource`** in each manifest entry records what shaped the row
  (`auto+plugin:AcmeSkus+distributions+datasets`), and the run **attestation** gains
  `Distribution` / `DatasetPack` / `GeneratorPlugin` sources — everything stays
  synthetic-only by construction.

!!! info "Engineering record"
    [statistical-generation.md](https://github.com/chrisw000/test-data-manager/blob/main/docs/statistical-generation.md).

## Where next

- [Profiling production shapes](profiling-production-shapes.md) — the sanctioned way to
  derive these numbers *from* production, without copying rows.
- [Performance testing](performance-testing.md) — why shaped data makes perf numbers real.
- [Configuration → entities](../reference/configuration.md#entitiesxproperties-statistical-generation-w4-d4).

**Guided tour:** next stop → [Profiling production shapes](profiling-production-shapes.md)
