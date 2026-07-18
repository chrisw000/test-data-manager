/* W5-D5 pipeline walkthrough: one step's journey through the TDM pipeline, one clickable
 * stage at a time. Vanilla JS, no libraries, no network — every artifact below is
 * captured from the repository's own executed sample workspace (the same commands the
 * docs-verify CI job runs), so the walkthrough shows real output, not illustrations.
 * Mounts on #tdm-pipeline-walkthrough; no-op on pages without it. */
(function () {
  "use strict";

  var STAGES = [
    {
      id: "step",
      label: "Step text",
      title: "1 · The step, as written",
      body:
        "The input is a plain Gherkin step — business language, no SQL, no builders. " +
        "This one comes from the repository's executed sample feature.",
      artifactLabel: "features/orders-seeding.feature",
      artifact:
        'Given an Order exists for Customer "Acme Ltd" with order number "ORD-1001" and status "Pending" and total "199.99"',
    },
    {
      id: "grammar",
      label: "Grammar match",
      title: "2 · StepGrammar match",
      body:
        "The StepGrammar classifies the step (Create, count 1), separates property " +
        "overrides from references, and normalises names — no domain code has run yet.",
      artifactLabel: "tdm explain — grammar section",
      artifact:
        'Grammar     : Create — entity "Order", count 1\n' +
        '  overrides : order number = "ORD-1001", status = "Pending", total = "199.99"\n' +
        '  references: Customer "Acme Ltd"',
    },
    {
      id: "resolution",
      label: "Entity resolution",
      title: "3 · Entity resolution",
      body:
        "Conventions map the logical name “Order” to a CLR type, a natural key, a key " +
        "strategy and repositories — resolved from the domain plugin, zero TDM code in " +
        "the domain. `tdm list-entities` prints this map for every entity.",
      artifactLabel: "tdm explain — resolution section",
      artifact:
        "Resolution  : Orders.Order → Acme.Orders.Data.Persistence.Domain.OrderEntity\n" +
        "  natural key : OrderNumber\n" +
        "  key         : Id:Guid (Deterministic)\n" +
        "  faker       : auto\n" +
        "  persist via : IOrderRepository.AddOrder\n" +
        "  read repo   : IOrderRepository → OrderRepository",
    },
    {
      id: "faker",
      label: "Faker / statistics",
      title: "4 · Faker + statistical layer",
      body:
        "Properties the step doesn't mention are generated: the convention faker (or a " +
        "heuristic auto-faker) fills the object, then declarative statistical config is " +
        "applied over it — every draw from the scenario seed, so it's deterministic.",
      artifactLabel: "tdm.settings.json — entities.Order.properties",
      artifact:
        '"Order": {\n' +
        '  "naturalKey": "OrderNumber",\n' +
        '  "properties": {\n' +
        '    "Status": { "weights": { "Pending": 0.6, "Shipped": 0.3, "Cancelled": 0.1 } },\n' +
        '    "Total":  { "distribution": "lognormal", "mean": 120, "sigma": 1.2 }\n' +
        "  }\n" +
        "}",
    },
    {
      id: "overrides",
      label: "Overrides",
      title: "5 · Step overrides win",
      body:
        "Values written in the step always beat generated ones — the test's language is " +
        "authoritative. The manifest records exactly which properties were overridden.",
      artifactLabel: "manifest — overridesApplied",
      artifact:
        '"overridesApplied": [ "OrderNumber", "Status", "Total" ]\n' +
        '// generated: OrderDate (faker, seed 42) — everything else came from the step',
    },
    {
      id: "identity",
      label: "Identity",
      title: "6 · The identity contract",
      body:
        "The id is derived, not assigned: UUIDv5 over “{domain}|{Entity}|{naturalKey}”. " +
        "Any team, any process, derives the same id for the same business object — this " +
        "is what makes cross-domain references agree without coordination.",
      artifactLabel: "derivation (real ids from the sample run)",
      artifact:
        'UUIDv5("Orders|Order|ORD-1001")    = a8eae15f-913e-5e14-b95a-735a8c3fc9c5\n' +
        'UUIDv5("Orders|Customer|Acme Ltd") = e47cf5ae-4475-54d3-8027-e09e3a4a1600\n' +
        "                                     ↳ the Order's CustomerId, derived independently",
    },
    {
      id: "persist",
      label: "Persistence",
      title: "7 · Persistence route",
      body:
        "RepositoryFirst persistence calls the domain's own write repository — the same " +
        "code path production uses (ADR-0001). Domains without repositories can run " +
        "DbContextOnly; API-fronted domains can seed through their HTTP API instead.",
      artifactLabel: "manifest — persistedVia",
      artifact: '"persistedVia": "IOrderRepository.AddOrder"',
    },
    {
      id: "manifest",
      label: "Manifest entry",
      title: "8 · The manifest entry",
      body:
        "Every row's final values, id, route and overrides land in the run manifest — " +
        "the audit artifact that replay, verify, teardown and the HTML report all read.",
      artifactLabel: "output/…tdm.json — scenarios[].entities[] (real entry)",
      artifact:
        "{\n" +
        '  "entity": "Order",\n' +
        '  "verb": "Create",\n' +
        '  "domain": "Orders",\n' +
        '  "persistedVia": "IOrderRepository.AddOrder",\n' +
        '  "id": "a8eae15f-913e-5e14-b95a-735a8c3fc9c5",\n' +
        '  "idStrategy": "Deterministic",\n' +
        '  "naturalKey": "ORD-1001",\n' +
        '  "fakerSource": "auto",\n' +
        '  "values": {\n' +
        '    "Id": "a8eae15f-913e-5e14-b95a-735a8c3fc9c5",\n' +
        '    "OrderNumber": "ORD-1001",\n' +
        '    "CustomerId": "e47cf5ae-4475-54d3-8027-e09e3a4a1600",\n' +
        '    "Status": "Pending",\n' +
        '    "Total": "199.99"\n' +
        "  },\n" +
        '  "overridesApplied": [ "OrderNumber", "Status", "Total" ]\n' +
        "}",
    },
  ];

  function render(mount) {
    var nav = document.createElement("div");
    nav.className = "tdm-pw-stages";
    nav.setAttribute("role", "tablist");
    nav.setAttribute("aria-label", "Pipeline stages");

    var panel = document.createElement("div");
    panel.className = "tdm-pw-panel";
    var heading = document.createElement("h3");
    heading.className = "tdm-pw-title";
    var body = document.createElement("p");
    body.className = "tdm-pw-body";
    var artifactLabel = document.createElement("div");
    artifactLabel.className = "tdm-pw-artifact-label";
    var artifact = document.createElement("pre");
    artifact.className = "tdm-pw-artifact";
    var artifactCode = document.createElement("code");
    artifact.appendChild(artifactCode);
    panel.appendChild(heading);
    panel.appendChild(body);
    panel.appendChild(artifactLabel);
    panel.appendChild(artifact);

    var buttons = STAGES.map(function (stage, i) {
      var wrap = document.createElement("span");
      wrap.className = "tdm-pw-stage-wrap";
      var btn = document.createElement("button");
      btn.type = "button";
      btn.className = "tdm-pw-stage";
      btn.setAttribute("role", "tab");
      btn.textContent = i + 1 + ". " + stage.label;
      btn.addEventListener("click", function () { select(i); });
      btn.addEventListener("keydown", function (e) {
        if (e.key === "ArrowRight") select(Math.min(i + 1, STAGES.length - 1), true);
        if (e.key === "ArrowLeft") select(Math.max(i - 1, 0), true);
      });
      wrap.appendChild(btn);
      if (i < STAGES.length - 1) {
        var arrow = document.createElement("span");
        arrow.className = "tdm-pw-arrow";
        arrow.setAttribute("aria-hidden", "true");
        arrow.textContent = "→";
        wrap.appendChild(arrow);
      }
      nav.appendChild(wrap);
      return btn;
    });

    function select(i, focus) {
      var stage = STAGES[i];
      buttons.forEach(function (b, j) {
        b.classList.toggle("tdm-pw-active", j === i);
        b.setAttribute("aria-selected", j === i ? "true" : "false");
      });
      heading.textContent = stage.title;
      body.textContent = stage.body;
      artifactLabel.textContent = stage.artifactLabel;
      artifactCode.textContent = stage.artifact;
      if (focus) buttons[i].focus();
    }

    mount.appendChild(nav);
    mount.appendChild(panel);
    select(0);
  }

  function init() {
    var mount = document.getElementById("tdm-pipeline-walkthrough");
    if (mount && !mount.dataset.tdmPwMounted) {
      mount.dataset.tdmPwMounted = "1";
      render(mount);
    }
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})();
