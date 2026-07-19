/* W5-D5 identity explorer: type domain | entity | key → watch UUIDv5 derive, with the
 * byte-level steps expandable. Pure-JS RFC 4122 §4.3 under the frozen Tdm.Identity
 * namespace — it MUST reproduce the engine's ids (the docs-verify parity test asserts it
 * against Tdm.Identity's unit-test vectors). Vanilla JS, no network. Mounts on
 * #tdm-identity-explorer; also exports its deriver for the parity test (node). */
(function (root) {
  "use strict";

  function sha1(bytes) {
    var ml = bytes.length, total = Math.ceil((ml + 9) / 64) * 64;
    var msg = new Uint8Array(total); msg.set(bytes); msg[ml] = 0x80;
    var dv = new DataView(msg.buffer), bitLen = ml * 8;
    dv.setUint32(total - 8, Math.floor(bitLen / 0x100000000));
    dv.setUint32(total - 4, bitLen >>> 0);
    var h0 = 0x67452301, h1 = 0xEFCDAB89, h2 = 0x98BADCFE, h3 = 0x10325476, h4 = 0xC3D2E1F0;
    var w = new Uint32Array(80);
    for (var i = 0; i < total; i += 64) {
      for (var j = 0; j < 16; j++) w[j] = dv.getUint32(i + j * 4);
      for (j = 16; j < 80; j++) { var n = w[j-3]^w[j-8]^w[j-14]^w[j-16]; w[j] = (n<<1)|(n>>>31); }
      var a=h0,b=h1,c=h2,d=h3,e=h4,f,k,t;
      for (j = 0; j < 80; j++) {
        if (j<20){f=(b&c)|(~b&d);k=0x5A827999;}
        else if (j<40){f=b^c^d;k=0x6ED9EBA1;}
        else if (j<60){f=(b&c)|(b&d)|(c&d);k=0x8F1BBCDC;}
        else {f=b^c^d;k=0xCA62C1D6;}
        t=(((a<<5)|(a>>>27))+f+e+k+w[j])>>>0; e=d;d=c;c=((b<<30)|(b>>>2))>>>0;b=a;a=t;
      }
      h0=(h0+a)>>>0;h1=(h1+b)>>>0;h2=(h2+c)>>>0;h3=(h3+d)>>>0;h4=(h4+e)>>>0;
    }
    var out = new Uint8Array(20), odv = new DataView(out.buffer);
    odv.setUint32(0,h0);odv.setUint32(4,h1);odv.setUint32(8,h2);odv.setUint32(12,h3);odv.setUint32(16,h4);
    return out;
  }

  // Frozen Tdm.Identity namespace GUID, network byte order (contract v1).
  var NAMESPACE = [0x8f,0x1b,0x9c,0x6e,0x2a,0x4d,0x5e,0x7f,0x9b,0x3c,0x6d,0x8e,0x0f,0x2a,0x4b,0x5c];

  function hex(bytes, from, to) {
    var s = ""; for (var i = from; i < to; i++) s += (bytes[i] + 0x100).toString(16).slice(1);
    return s;
  }

  function derive(canonicalName) {
    var nameBytes = new TextEncoder().encode(canonicalName);
    var input = new Uint8Array(16 + nameBytes.length);
    input.set(NAMESPACE); input.set(nameBytes, 16);
    var h = sha1(input);
    h[6] = (h[6] & 0x0f) | 0x50;   // version 5
    h[8] = (h[8] & 0x3f) | 0x80;   // RFC 4122 variant
    return hex(h,0,4)+"-"+hex(h,4,6)+"-"+hex(h,6,8)+"-"+hex(h,8,10)+"-"+hex(h,10,16);
  }
  // ForNaturalKey(domain, entity, key) === FromName("domain|entity|key").
  function forNaturalKey(domain, entity, key) { return derive(domain + "|" + entity + "|" + key); }

  var api = { derive: derive, forNaturalKey: forNaturalKey };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  if (typeof document === "undefined") return;

  // Presets are Tdm.Identity's own unit-test vectors — the explorer proves parity live.
  var PRESETS = [
    { domain: "CRM",    entity: "Customer", key: "Acme Ltd",  note: "frozen contract vector" },
    { domain: "Orders", entity: "Order",    key: "ORD-1001",  note: "the sample Orders order" },
    { domain: "Orders", entity: "Customer", key: "Acme Ltd",  note: "referenced cross-domain" }
  ];

  function el(t, a, txt) { var e = document.createElement(t); if (a) Object.keys(a).forEach(function(k){e.setAttribute(k,a[k]);}); if (txt!=null) e.textContent=txt; return e; }

  function render(mount) {
    var state = { domain: "Orders", entity: "Customer", key: "Acme Ltd" };

    var row = el("div", { class: "tdm-ix-inputs" });
    function field(key, label) {
      var w = el("label", { class: "tdm-ix-field" });
      w.appendChild(el("span", null, label));
      var i = el("input", { value: state[key], spellcheck: "false" });
      i.addEventListener("input", function () { state[key] = i.value; draw(); });
      w.appendChild(i); row.appendChild(w); return i;
    }
    var iDomain = field("domain", "domain"), iEntity = field("entity", "entity"), iKey = field("key", "natural key");

    var presetWrap = el("div", { class: "tdm-ix-presets" });
    presetWrap.appendChild(el("span", { class: "tdm-ix-plabel" }, "vectors:"));
    PRESETS.forEach(function (p) {
      var b = el("button", { type: "button", class: "tdm-ix-preset", title: p.note }, p.domain + "|" + p.entity + "|" + p.key);
      b.addEventListener("click", function () {
        state.domain = p.domain; state.entity = p.entity; state.key = p.key;
        iDomain.value = p.domain; iEntity.value = p.entity; iKey.value = p.key; draw();
      });
      presetWrap.appendChild(b);
    });

    var name = el("div", { class: "tdm-ix-name" });
    var uuid = el("div", { class: "tdm-ix-uuid" });
    var steps = el("details", { class: "tdm-ix-steps" });
    steps.appendChild(el("summary", null, "byte-level derivation"));
    var stepBody = el("div", { class: "tdm-ix-stepbody" });
    steps.appendChild(stepBody);

    mount.appendChild(row); mount.appendChild(presetWrap);
    mount.appendChild(name); mount.appendChild(uuid); mount.appendChild(steps);

    function draw() {
      var canonical = state.domain + "|" + state.entity + "|" + state.key;
      var id = derive(canonical);
      name.innerHTML = "";
      name.appendChild(el("span", { class: "tdm-ix-dim" }, "canonical name  "));
      name.appendChild(el("b", null, canonical));
      uuid.innerHTML = "";
      uuid.appendChild(el("span", { class: "tdm-ix-dim" }, "UUIDv5  "));
      uuid.appendChild(el("b", { class: "tdm-ix-id" }, id));
      stepBody.innerHTML =
        "1 · input  = \"" + canonical + "\"\n" +
        "2 · hash   = SHA1( namespace(16 bytes) ++ UTF-8(input) )\n" +
        "3 · set version nibble → 5, variant bits → RFC 4122 (10xx)\n" +
        "4 · format = 8-4-4-4-12  →  " + id;
    }
    draw();
  }

  function init() {
    var m = document.getElementById("tdm-identity-explorer");
    if (m && !m.dataset.tdmIxMounted) { m.dataset.tdmIxMounted = "1"; render(m); }
  }
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init); else init();
})(typeof globalThis !== "undefined" ? globalThis : this);
