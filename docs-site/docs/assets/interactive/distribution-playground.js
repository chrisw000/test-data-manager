/* W5-D5 distribution playground: sliders → live histogram, with a "same seed" toggle that
 * makes determinism visible. The shape math MIRRORS Tdm.Core.Generation.Distributions —
 * same Box–Muller normal, lognormal-as-median (mean = exp(μ)), uniform, exponential and
 * ordinal-stable weighted draw. It is a mirror, not the engine: the playground uses its
 * own seedable PRNG (mulberry32), so it reproduces the *shapes* and the median-equals-mean
 * convention, not the engine's exact byte sequence. Vanilla JS, no libraries, no network;
 * colours come from Material's CSS variables so light/dark just work.
 *
 * Mounts on #tdm-distribution-playground. The pure sampling API is also exported for the
 * docs-verify parity test (node), which asserts the lognormal median tracks the mean. */
(function (root) {
  "use strict";

  // Deterministic PRNG: same seed → same stream (the "same seed" toggle relies on this).
  function mulberry32(seed) {
    var a = seed >>> 0;
    return function () {
      a |= 0; a = (a + 0x6D2B79F5) | 0;
      var t = Math.imul(a ^ (a >>> 15), 1 | a);
      t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
      return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
  }

  // Box–Muller, mirroring Distributions.Normal: a fixed two-draw count keeps streams aligned.
  function normal(rng, mean, sigma) {
    var u1 = 1 - rng();   // (0,1] — guards log(0)
    var u2 = rng();
    return mean + sigma * Math.sqrt(-2 * Math.log(u1)) * Math.cos(2 * Math.PI * u2);
  }

  // Mirrors Distributions.Sample: lognormal 'mean' is the MEDIAN (scale = exp(μ)).
  function sampleOne(kind, p, rng) {
    var v;
    switch (kind) {
      case "normal":      v = normal(rng, p.mean, p.sigma); break;
      case "lognormal":   v = Math.exp(normal(rng, Math.log(p.mean), p.sigma)); break;
      case "uniform":     v = p.min + rng() * (p.max - p.min); break;
      case "exponential": v = -Math.log(1 - rng()) * p.mean; break;
      default: throw new Error("unknown distribution: " + kind);
    }
    if (p.min != null && v < p.min) v = p.min;
    if (p.max != null && v > p.max) v = p.max;
    return v;
  }

  // Mirrors Distributions.SampleWeighted: keys iterated ordinal-sorted for a stable stream.
  function sampleWeighted(weights, rng) {
    var ordered = Object.keys(weights).sort();
    var total = ordered.reduce(function (s, k) { return s + weights[k]; }, 0);
    var roll = rng() * total, cumulative = 0;
    for (var i = 0; i < ordered.length; i++) {
      cumulative += weights[ordered[i]];
      if (roll < cumulative) return ordered[i];
    }
    return ordered[ordered.length - 1];
  }

  var api = { mulberry32: mulberry32, sampleOne: sampleOne, sampleWeighted: sampleWeighted };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  if (typeof document === "undefined") return;  // node parity test: no DOM, don't mount.

  var N = 8000, BINS = 32;

  function el(tag, attrs, text) {
    var e = document.createElement(tag);
    if (attrs) Object.keys(attrs).forEach(function (k) { e.setAttribute(k, attrs[k]); });
    if (text != null) e.textContent = text;
    return e;
  }

  function render(mount) {
    var kinds = ["lognormal", "normal", "uniform", "exponential"];
    var state = { kind: "lognormal", mean: 120, sigma: 1.2, min: 0, max: 400, seed: 42, lockSeed: true };

    // ── controls ──────────────────────────────────────────────────────────
    var controls = el("div", { class: "tdm-dp-controls" });

    var kindWrap = el("label", { class: "tdm-dp-field" });
    kindWrap.appendChild(el("span", null, "distribution"));
    var kindSel = el("select");
    kinds.forEach(function (k) {
      var o = el("option", { value: k }, k); if (k === state.kind) o.selected = true; kindSel.appendChild(o);
    });
    kindWrap.appendChild(kindSel);
    controls.appendChild(kindWrap);

    function slider(key, label, min, max, step) {
      var wrap = el("label", { class: "tdm-dp-field tdm-dp-" + key });
      var head = el("span", null, label + " ");
      var val = el("b", null, String(state[key]));
      head.appendChild(val);
      var input = el("input", { type: "range", min: min, max: max, step: step });
      input.value = state[key];
      input.addEventListener("input", function () {
        state[key] = parseFloat(input.value); val.textContent = input.value; draw();
      });
      wrap.appendChild(head); wrap.appendChild(input);
      wrap._val = val; wrap._input = input;
      controls.appendChild(wrap);
      return wrap;
    }

    var fMean = slider("mean", "mean / median", 10, 300, 5);
    var fSigma = slider("sigma", "sigma", 0.1, 2.5, 0.1);
    var fMin = slider("min", "min (clamp)", 0, 200, 5);
    var fMax = slider("max", "max (clamp)", 100, 800, 10);

    var seedWrap = el("label", { class: "tdm-dp-field tdm-dp-check" });
    var seedBox = el("input", { type: "checkbox" }); seedBox.checked = state.lockSeed;
    seedBox.addEventListener("change", function () { state.lockSeed = seedBox.checked; draw(); });
    seedWrap.appendChild(seedBox);
    seedWrap.appendChild(el("span", null, "same seed (deterministic)"));
    controls.appendChild(seedWrap);

    var reroll = el("button", { type: "button", class: "tdm-dp-btn" }, "re-roll seed");
    reroll.addEventListener("click", function () { state.seed = (state.seed * 1664525 + 1013904223) >>> 0; draw(); });
    controls.appendChild(reroll);

    // ── chart + readout ───────────────────────────────────────────────────
    var chart = el("div", { class: "tdm-dp-chart" });
    var svgNS = "http://www.w3.org/2000/svg";
    var svg = document.createElementNS(svgNS, "svg");
    svg.setAttribute("viewBox", "0 0 640 220");
    svg.setAttribute("class", "tdm-dp-svg");
    svg.setAttribute("role", "img");
    chart.appendChild(svg);
    var readout = el("div", { class: "tdm-dp-readout" });

    mount.appendChild(controls);
    mount.appendChild(chart);
    mount.appendChild(readout);

    function kindNeeds(key) {
      if (state.kind === "uniform") return key === "min" || key === "max";
      if (state.kind === "exponential") return key === "mean";
      return key === "mean" || key === "sigma";       // normal, lognormal
    }

    function draw() {
      [["mean", fMean], ["sigma", fSigma], ["min", fMin], ["max", fMax]].forEach(function (pair) {
        pair[1].style.display = kindNeeds(pair[0]) ? "" : "none";
      });

      var rng = mulberry32(state.lockSeed ? state.seed : (Math.random() * 4294967296) >>> 0);
      var clampMin = (state.kind === "uniform" || state.kind === "exponential") ? null : state.min;
      var clampMax = (state.kind === "uniform") ? null : state.max;
      var params = { mean: state.mean, sigma: state.sigma,
                     min: state.kind === "uniform" ? state.min : clampMin,
                     max: state.kind === "uniform" ? state.max : clampMax };

      var samples = new Array(N);
      for (var i = 0; i < N; i++) samples[i] = sampleOne(state.kind, params, rng);

      var lo = Math.min.apply(null, samples), hi = Math.max.apply(null, samples);
      if (hi - lo < 1e-9) hi = lo + 1;
      var counts = new Array(BINS).fill(0);
      for (i = 0; i < N; i++) {
        var b = Math.min(BINS - 1, Math.floor((samples[i] - lo) / (hi - lo) * BINS));
        counts[b]++;
      }
      var peak = Math.max.apply(null, counts) || 1;

      var sorted = samples.slice().sort(function (a, b) { return a - b; });
      var median = sorted[Math.floor(N / 2)];
      var mean = samples.reduce(function (s, x) { return s + x; }, 0) / N;

      while (svg.firstChild) svg.removeChild(svg.firstChild);
      var W = 640, H = 220, padB = 24, padL = 4, bw = (W - padL * 2) / BINS;
      for (i = 0; i < BINS; i++) {
        var h = (counts[i] / peak) * (H - padB - 8);
        var rect = document.createElementNS(svgNS, "rect");
        rect.setAttribute("x", (padL + i * bw + 1).toFixed(1));
        rect.setAttribute("y", (H - padB - h).toFixed(1));
        rect.setAttribute("width", (bw - 2).toFixed(1));
        rect.setAttribute("height", h.toFixed(1));
        rect.setAttribute("class", "tdm-dp-bar");
        svg.appendChild(rect);
      }
      // median marker
      var mx = padL + (median - lo) / (hi - lo) * (W - padL * 2);
      var line = document.createElementNS(svgNS, "line");
      line.setAttribute("x1", mx.toFixed(1)); line.setAttribute("x2", mx.toFixed(1));
      line.setAttribute("y1", "8"); line.setAttribute("y2", String(H - padB));
      line.setAttribute("class", "tdm-dp-median");
      svg.appendChild(line);
      var lbl = document.createElementNS(svgNS, "text");
      lbl.setAttribute("x", Math.min(W - 60, mx + 4).toFixed(1));
      lbl.setAttribute("y", "18"); lbl.setAttribute("class", "tdm-dp-medlabel");
      lbl.textContent = "median " + median.toFixed(1);
      svg.appendChild(lbl);

      var parity = "";
      if (state.kind === "lognormal") {
        var errPct = Math.abs(median - state.mean) / state.mean * 100;
        parity = " · lognormal median " + median.toFixed(1) + " ≈ mean input " +
                 state.mean + " (" + errPct.toFixed(1) + "% off)";
      }
      readout.textContent = "n=" + N + " · mean " + mean.toFixed(1) +
        " · median " + median.toFixed(1) +
        (state.lockSeed ? " · seed " + state.seed + " (locked — same every redraw)" : " · unlocked seed") +
        parity;
    }

    kindSel.addEventListener("change", function () { state.kind = kindSel.value; draw(); });
    draw();
  }

  function init() {
    var mount = document.getElementById("tdm-distribution-playground");
    if (mount && !mount.dataset.tdmDpMounted) { mount.dataset.tdmDpMounted = "1"; render(mount); }
  }
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init);
  else init();
})(typeof globalThis !== "undefined" ? globalThis : this);
