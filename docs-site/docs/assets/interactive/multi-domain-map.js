/* W5-D5 multi-domain map: the Orders / Billing / Fulfilment picture — each domain as a
 * swim-lane (API · DbContext · database), with identity edges that highlight on hover to
 * show both sides deriving the same GUID independently. Hand-rolled inline SVG, no D3
 * needed (the layout is a fixed three-lane grid). Vanilla JS, no network. Mounts on
 * #tdm-multi-domain-map. */
(function () {
  "use strict";
  if (typeof document === "undefined") return;

  var SVGNS = "http://www.w3.org/2000/svg";
  function s(tag, attrs, text) {
    var e = document.createElementNS(SVGNS, tag);
    if (attrs) Object.keys(attrs).forEach(function (k) { e.setAttribute(k, attrs[k]); });
    if (text != null) e.textContent = text;
    return e;
  }

  var LANES = [
    { name: "Orders",     x: 20,  entities: "Customer · Order · Product" },
    { name: "Billing",    x: 330, entities: "Account · Invoice · CustomerSummary" },
    { name: "Fulfilment", x: 640, entities: "Location · Shipment" }
  ];

  // Identity edges: the same natural key derives the same id in both the owning and the
  // referencing domain. from/to are lane indices; label is the shared canonical name.
  var EDGES = [
    { from: 0, to: 1, key: "Orders|Customer|Acme Ltd", text: "Billing Invoice → Orders Customer" },
    { from: 0, to: 2, key: "Orders|Order|ORD-1001",    text: "Fulfilment Shipment → Orders Order" }
  ];

  function render(mount) {
    var W = 900, H = 300;
    var svg = s("svg", { viewBox: "0 0 " + W + " " + H, class: "tdm-mm-svg", role: "img",
      "aria-label": "Three domains — Orders, Billing, Fulfilment — each with its own API, DbContext and database, joined by identity edges" });

    var defs = s("defs");
    var marker = s("marker", { id: "tdm-mm-arrow", viewBox: "0 0 10 10", refX: "9", refY: "5", markerWidth: "7", markerHeight: "7", orient: "auto" });
    marker.appendChild(s("path", { d: "M0 0L10 5L0 10z", class: "tdm-mm-arrowhead" }));
    defs.appendChild(marker); svg.appendChild(defs);

    var laneW = 260;
    LANES.forEach(function (lane) {
      var g = s("g");
      g.appendChild(s("rect", { class: "tdm-mm-lane", x: lane.x, y: 30, width: laneW, height: 200, rx: 10 }));
      g.appendChild(s("text", { class: "tdm-mm-title", x: lane.x + laneW / 2, y: 55, "text-anchor": "middle" }, lane.name));
      [["API", 72], ["DbContext", 112]].forEach(function (r) {
        g.appendChild(s("rect", { class: "tdm-mm-box", x: lane.x + 20, y: r[1], width: laneW - 40, height: 30, rx: 5 }));
        g.appendChild(s("text", { class: "tdm-mm-mono", x: lane.x + 32, y: r[1] + 20 }, lane.name + " " + r[0]));
      });
      g.appendChild(s("rect", { class: "tdm-mm-db", x: lane.x + 20, y: 152, width: laneW - 40, height: 58, rx: 5 }));
      g.appendChild(s("text", { class: "tdm-mm-mono", x: lane.x + 32, y: 174 }, lane.name.toLowerCase() + "-db"));
      g.appendChild(s("text", { class: "tdm-mm-dim", x: lane.x + 32, y: 194 }, lane.entities));
      svg.appendChild(g);
    });

    var readout = document.createElement("div");
    readout.className = "tdm-mm-readout";
    readout.textContent = "hover an identity edge — both domains derive the same id, with no shared transaction.";

    EDGES.forEach(function (edge) {
      var x1 = LANES[edge.from].x + laneW / 2, x2 = LANES[edge.to].x + laneW / 2;
      var dip = 236 + (edge.to - edge.from) * 22;
      var path = s("path", {
        class: "tdm-mm-edge",
        d: "M" + x1 + " 230 C " + x1 + " " + dip + ", " + x2 + " " + dip + ", " + x2 + " 230",
        "marker-end": "url(#tdm-mm-arrow)"
      });
      function on() {
        path.classList.add("tdm-mm-edge-hot");
        readout.innerHTML = "";
        var b = document.createElement("b"); b.textContent = edge.key;
        readout.appendChild(document.createTextNode(edge.text + " — both derive "));
        readout.appendChild(b);
      }
      function off() { path.classList.remove("tdm-mm-edge-hot"); }
      path.addEventListener("mouseenter", on);
      path.addEventListener("mouseleave", off);
      path.addEventListener("focus", on);
      path.addEventListener("blur", off);
      path.setAttribute("tabindex", "0");
      svg.appendChild(path);
    });

    mount.appendChild(svg);
    mount.appendChild(readout);
  }

  function init() {
    var m = document.getElementById("tdm-multi-domain-map");
    if (m && !m.dataset.tdmMmMounted) { m.dataset.tdmMmMounted = "1"; render(m); }
  }
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init); else init();
})();
