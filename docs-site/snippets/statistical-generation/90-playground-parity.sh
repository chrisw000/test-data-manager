# Parity check (W5-P4 acceptance): the distribution playground must mirror
# Distributions.cs — specifically the lognormal 'mean' is the MEDIAN, so a large sample's
# observed median must track the mean input across the slider range. Runs the actual
# playground module in node (no DOM needed; it exports its sampler when document is absent).
node - <<'JS'
const dp = require("./docs-site/docs/assets/interactive/distribution-playground.js");

function median(xs) { const s = xs.slice().sort((a, b) => a - b); return s[s.length >> 1]; }

let failures = 0;
const N = 40000;
// Sweep the mean and sigma slider ranges; lognormal median === exp(ln(mean)) === mean.
for (const mean of [10, 60, 120, 200, 300]) {
  for (const sigma of [0.3, 0.8, 1.2, 2.0]) {
    const rng = dp.mulberry32(1234567);
    const xs = [];
    for (let i = 0; i < N; i++) xs.push(dp.sampleOne("lognormal", { mean, sigma }, rng));
    const med = median(xs);
    const errPct = Math.abs(med - mean) / mean * 100;
    if (errPct > 6) {
      console.error(`FAIL lognormal mean=${mean} sigma=${sigma}: median ${med.toFixed(2)} is ${errPct.toFixed(1)}% off`);
      failures++;
    }
  }
}

// Weighted draw must honour declared proportions (60/30/10) within tolerance.
{
  const rng = dp.mulberry32(42);
  const weights = { Pending: 0.6, Shipped: 0.3, Cancelled: 0.1 };
  const tally = { Pending: 0, Shipped: 0, Cancelled: 0 };
  for (let i = 0; i < N; i++) tally[dp.sampleWeighted(weights, rng)]++;
  for (const [k, w] of Object.entries(weights)) {
    const share = tally[k] / N;
    if (Math.abs(share - w) > 0.02) {
      console.error(`FAIL weighted ${k}: share ${(share*100).toFixed(1)}% vs declared ${(w*100)}%`);
      failures++;
    }
  }
}

if (failures) { console.error(`playground parity: ${failures} failure(s)`); process.exit(1); }
console.log("playground parity OK: lognormal median tracks mean across the slider range; weights within 2%");
JS
