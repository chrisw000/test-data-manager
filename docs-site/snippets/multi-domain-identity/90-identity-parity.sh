# Parity check (W5-P5 acceptance): the identity explorer must reproduce Tdm.Identity's
# ids exactly. Runs the actual explorer module in node and checks the frozen unit-test
# vector plus the sample ids the docs quote (verified against the engine's own manifests).
node - <<'JS'
const ix = require("./docs-site/docs/assets/interactive/identity-explorer.js");

const cases = [
  // Frozen contract vector — TdmIdentityTests.ForNaturalKey_ContractVector_FrozenValue.
  ["CRM",    "Customer", "Acme Ltd", "f629ad79-bbba-5d12-ae83-b8a8b9bf4ce0"],
  // Sample-workspace ids the guides quote, taken from real TDM manifests.
  ["Orders", "Customer", "Acme Ltd", "e47cf5ae-4475-54d3-8027-e09e3a4a1600"],
  ["Orders", "Order",    "ORD-1001", "a8eae15f-913e-5e14-b95a-735a8c3fc9c5"],
];

let failures = 0;
for (const [domain, entity, key, expected] of cases) {
  const got = ix.forNaturalKey(domain, entity, key);
  if (got !== expected) {
    console.error(`FAIL ${domain}|${entity}|${key}: got ${got}, expected ${expected}`);
    failures++;
  }
}
if (failures) { console.error(`identity parity: ${failures} failure(s)`); process.exit(1); }
console.log("identity explorer parity OK: reproduces Tdm.Identity vectors exactly");
JS
