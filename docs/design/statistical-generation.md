# Statistical generation (W4-D4 / W4-D5)

Three ways to shape generated data beyond the built-in fakers — code for the complex 5%,
config for the common 95% (W4-D4). Every draw flows through the existing per-scenario
`Randomizer`, so the v1 determinism guarantee (D8) is unchanged: same seed, same values —
including every new capability here (W4-D5).

## Config-declared distributions & weights (no code)

```jsonc
"entities": {
  "Order": {
    "properties": {
      "Total":  { "distribution": "lognormal", "mean": 120, "sigma": 1.2 },
      "Status": { "weights": { "Pending": 0.6, "Shipped": 0.3, "Cancelled": 0.1 } }
    }
  }
}
```

- **Distributions** — `normal` (mean, sigma), `lognormal` (mean = the *median*/scale,
  sigma of the underlying normal), `uniform` (min, max), `exponential` (mean). Optional
  `min`/`max` clamp any distribution; `decimals` rounds floating targets (default 2).
  Numeric properties only — use `weights` for categorical values.
- **Weights** — value → relative weight, normalised at sample time; values convert through
  the same pipeline as step overrides (strings, enums, numbers …). Weight keys are sampled
  in ordinal-sorted order, so reordering the config file does not change draws.
- Applied **after** the faker (convention or auto) and **before** step overrides — the
  declared shape holds regardless of faker, and explicit steps still win.
- Property names match with the usual tolerance (`total` ≡ `Total`); a name matching no
  writable property fails loudly at generation (so `tdm validate` catches it).

## Correlated fields via datasets

```jsonc
"datasets": {
  "ukCities": { "path": "./datasets/uk-cities.csv" }   // CSV, first row = header
},
"entities": {
  "Customer": {
    "properties": {
      "City":     { "dataset": "ukCities", "column": "city" },
      "Postcode": { "dataset": "ukCities", "column": "postcode" },
      "Country":  { "dataset": "ukCities", "column": "country" }
    }
  }
}
```

All properties of one entity naming the same dataset are filled from a **single sampled
row** — city↔postcode↔country stay consistent per entity. Paths resolve relative to the
settings file; parsing is minimal RFC-4180 (quoted fields may contain commas). `column`
defaults to the property name. Wave 4's seed packs (W4-P4) are the intended distribution
vehicle for shared dataset files.

## Locale

```jsonc
"domains": [ { "name": "Orders", "locale": "en_GB" } ]
```

Per-domain Bogus locale for generated names/addresses (`en`, `en_GB`, `de`, `fr`,
`pt_BR`, …). The locale picks the vocabulary; the per-scenario Randomizer picks from it —
determinism intact. Invalid locales fail at scenario start with the valid examples listed.

## Generator plugins (code, for the complex 5%)

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

Concrete `IValueGeneratorPlugin` implementations with a parameterless constructor are
discovered from the domain's plugin assemblies (same rules as `IProviderBootstrap`
providers) and consulted per property **before** the built-in auto-faker heuristics —
teams extend the heuristic table with code, not forks. Plugins apply to auto-faked
entities; convention `{Name}Faker` classes remain fully in charge of their own output
(the statistical layer above still applies to both). Returning `null` falls through to
the next plugin / the heuristics. Draw exclusively from the supplied `Randomizer` — that
is the determinism contract. Consult order is ordinal by `Name`.

## Audit posture

The manifest's per-entity `fakerSource` records what shaped each row
(`auto+plugin:AcmeSkus+distributions+datasets`), and the run attestation (W2-D1) gains
`GeneratorPlugin` / `Distribution` / `DatasetPack` sources. Everything remains
synthetic-only by construction; the §2.6 profiling spike is the sanctioned route for
feeding *real* production shapes into this same distribution config.
