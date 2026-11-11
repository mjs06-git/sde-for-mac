// SDE phone form: a small page served by SDE on the Mac. No libraries; everything is built with DOM calls (never
// innerHTML), so text from the database is always shown as text.
(function () {
  "use strict";

  var state = { assessment: null, saving: Promise.resolve(), pairToken: null };

  function $(id) { return document.getElementById(id); }

  function el(tag, attrs, children) {
    var e = document.createElement(tag);
    if (attrs) {
      Object.keys(attrs).forEach(function (k) {
        if (k === "text") { e.textContent = attrs[k]; }
        else if (k === "className") { e.className = attrs[k]; }
        else if (attrs[k] === true) { e.setAttribute(k, ""); }
        else if (attrs[k] !== false && attrs[k] != null) { e.setAttribute(k, attrs[k]); }
      });
    }
    (children || []).forEach(function (c) { if (c) { e.appendChild(c); } });
    return e;
  }

  function show(section) {
    ["pair", "find", "edit"].forEach(function (s) { $(s).hidden = s !== section; });
    window.scrollTo(0, 0);
  }

  function message(text) {
    var m = $("message");
    m.textContent = text || "";
    m.hidden = !text;
  }

  // Every call: same-origin cookie; changes also carry X-SDE: 1 (the server refuses them without it).
  function api(method, path, body, isForm) {
    var headers = { "Accept": "application/json" };
    if (method !== "GET") { headers["X-SDE"] = "1"; }
    if (body && !isForm) { headers["Content-Type"] = "application/json"; }
    return fetch(path, {
      method: method,
      headers: headers,
      credentials: "same-origin",
      body: body ? (isForm ? body : JSON.stringify(body)) : undefined
    }).then(function (res) {
      return res.json().catch(function () { return {}; }).then(function (data) {
        if (res.status === 401 && path !== "/api/pair") {
          showPair(null, data.error);
        }
        return { ok: res.ok, status: res.status, data: data };
      });
    }, function () {
      return { ok: false, status: 0, data: { error: "Can't reach the Mac. Check that this phone is on the same Wi-Fi and the phone form is still running." } };
    });
  }

  // ───────── Pairing ─────────

  function showPair(token, text) {
    if (token) { state.pairToken = token; }
    $("pin-row").hidden = !!state.pairToken;
    $("pair-help").textContent = state.pairToken
      ? "Type your name, then tap Pair."
      : "Type the 6-digit PIN shown on the Mac (Settings, Phone form), or scan its QR code with the camera.";
    $("who").hidden = true;
    message(text || "");
    show("pair");
  }

  $("pair-form").addEventListener("submit", function (e) {
    e.preventDefault();
    var name = $("pair-name").value.trim();
    if (!name) { message("Type your name first."); $("pair-name").focus(); return; }
    var body = { name: name };
    if (state.pairToken) { body.token = state.pairToken; } else { body.pin = $("pair-pin").value.trim(); }
    api("POST", "/api/pair", body).then(function (r) {
      if (!r.ok) {
        // A used or expired link: fall back to the PIN.
        state.pairToken = null;
        showPair(null, r.data.error || "Pairing didn't work. Try again.");
        return;
      }
      state.pairToken = null;
      $("pair-pin").value = "";
      paired(r.data.name);
    });
  });

  function paired(name) {
    $("who").textContent = "Paired as " + name;
    $("who").hidden = false;
    message("");
    show("find");
    teams();
    search("");
  }

  // ───────── My assignments ─────────

  var teamKey = "sde-team";

  function savedTeam() {
    try { return localStorage.getItem(teamKey) || ""; } catch (e) { return ""; }
  }

  function teams() {
    api("GET", "/api/teams").then(function (r) {
      if (!r.ok || r.data.length === 0) { $("mine").hidden = true; return; }
      var select = $("team");
      while (select.options.length > 1) { select.remove(1); }
      r.data.forEach(function (t) { select.appendChild(el("option", { value: t.teamId, text: t.name })); });
      var saved = savedTeam();
      select.value = r.data.some(function (t) { return t.teamId === saved; }) ? saved : "";
      $("mine").hidden = false;
      assignments(select.value);
    });
  }

  function assignments(teamId) {
    var list = $("assignments");
    list.textContent = "";
    if (!teamId) { return; }
    api("GET", "/api/teams/" + teamId + "/assignments").then(function (r) {
      if (!r.ok) { message(r.data.error); return; }
      if (r.data.length === 0) {
        list.appendChild(el("li", { text: "Nothing assigned to this team yet." }));
        return;
      }
      r.data.forEach(function (a) {
        var prefix = a.routeOrder ? a.routeOrder + ". " : "";
        var extra = [a.shortCode, a.status !== "Assigned" ? a.status : null, a.dueDate ? "Due " + a.dueDate : null];
        list.appendChild(structureItem(a.structure, prefix, extra));
      });
    });
  }

  $("team").addEventListener("change", function () {
    try { localStorage.setItem(teamKey, $("team").value); } catch (e) { }
    assignments($("team").value);
  });

  // ───────── Search and add ─────────

  function structureItem(s, prefix, extra) {
    var meta = [s.city, s.owner, s.parcel ? "Parcel " + s.parcel : null, s.recordId ? "Record " + s.recordId : null, s.result]
      .concat(extra || []).filter(function (x) { return x; }).join(" · ");
    var b = el("button", { type: "button" }, [
      el("span", { className: "addr", text: (prefix || "") + s.address }),
      el("span", { className: "meta", text: meta || s.structureType })
    ]);
    b.addEventListener("click", function () { openStructure(s); });
    return el("li", null, [b]);
  }

  function search(q) {
    api("GET", "/api/structures?q=" + encodeURIComponent(q)).then(function (r) {
      var list = $("results");
      list.textContent = "";
      if (!r.ok) { message(r.data.error); return; }
      if (r.data.length === 0) {
        list.appendChild(el("li", { text: q ? "Nothing matches. Add the structure below." : "No structures yet." }));
        return;
      }
      r.data.forEach(function (s) { list.appendChild(structureItem(s)); });
    });
  }

  $("search-form").addEventListener("submit", function (e) {
    e.preventDefault();
    search($("search").value.trim());
  });

  $("show-add").addEventListener("click", function () {
    $("add-form").hidden = false;
    $("show-add").hidden = true;
    $("add-number").focus();
  });

  $("cancel-add").addEventListener("click", function () {
    $("add-form").hidden = true;
    $("show-add").hidden = false;
  });

  $("add-form").addEventListener("submit", function (e) {
    e.preventDefault();
    var type = document.querySelector("input[name=type]:checked").value;
    var changes = [];
    Array.prototype.forEach.call($("add-form").querySelectorAll("input[data-key]"), function (i) {
      if (i.value.trim()) { changes.push({ key: i.getAttribute("data-key"), value: i.value.trim() }); }
    });
    api("POST", "/api/structures", { structureType: parseInt(type, 10), changes: changes }).then(function (r) {
      if (!r.ok) { message(r.data.error || "Couldn't add the structure."); return; }
      $("add-form").reset();
      $("add-form").hidden = true;
      $("show-add").hidden = false;
      renderAssessment(r.data, true);
    });
  });

  function openStructure(s) {
    message("");
    var call = s.assessmentId
      ? api("GET", "/api/assessments/" + s.assessmentId)
      : api("POST", "/api/properties/" + s.propertyId + "/assessments");
    call.then(function (r) {
      if (!r.ok) { message(r.data.error || "Couldn't open that structure."); return; }
      renderAssessment(r.data, true);
    });
  }

  function openAssessment(id) {
    message("");
    api("GET", "/api/assessments/" + id).then(function (r) {
      if (!r.ok) { message(r.data.error || "Couldn't open that assessment."); return; }
      renderAssessment(r.data, true);
    });
  }

  $("back").addEventListener("click", function () {
    state.assessment = null;
    show("find");
    search($("search").value.trim());
  });

  // ───────── One assessment ─────────

  function fieldId(key) { return "f-" + key.replace(/\./g, "-"); }

  // What the page is built from: when it is the same after a save, only values are refreshed (nothing is rebuilt
  // under the person's finger).
  function keysOf(a) {
    return a.assessmentId + "#" + a.steps.map(function (s) {
      return s.step + ":" + s.fields.map(function (f) { return f.key + ":" + f.options.length; }).join(",");
    }).join("|") + "#E" + a.elements.map(function (e) { return e.id; }).join(",") +
      "#C" + a.costAdjustments.map(function (c) { return c.id; }).join(",");
  }

  function renderAssessment(a, fresh) {
    var previous = state.assessment;
    state.assessment = a;
    $("edit-heading").textContent = a.address;
    var hasPercent = typeof a.percentDamage === "number";
    $("edit-summary").textContent = [a.structureType, a.status, hasPercent ? null : a.result].filter(function (x) { return x; }).join(" · ");
    showResult(a);
    $("locked").hidden = !a.locked;
    $("locked").textContent = a.locked ? a.lockedMessage : "";
    $("photo-count").textContent = a.photoCount === 1 ? "1 photo" : a.photoCount + " photos";
    Array.prototype.forEach.call($("photo-form").elements, function (c) { c.disabled = a.locked; });
    if (fresh) { $("save-status").textContent = ""; }

    if (!fresh && previous && keysOf(previous) === keysOf(a)) {
      // Same fields: only refresh values the person isn't typing in.
      a.steps.forEach(function (s) { s.fields.forEach(function (f) { setValue(f); }); });
      refreshCosts(a);
      refreshElements(a);
    } else {
      var open = {};
      if (!fresh) {
        Array.prototype.forEach.call($("steps").querySelectorAll("details"), function (d) { open[d.getAttribute("data-step")] = d.open; });
      }
      var steps = $("steps");
      steps.textContent = "";
      a.steps.forEach(function (s, i) {
        var d = el("details", { "data-step": String(s.step) }, [el("summary", { text: s.step + ". " + s.name })]);
        d.open = open[String(s.step)] !== undefined ? open[String(s.step)] : (fresh && i === 0);
        s.fields.forEach(function (f) { d.appendChild(control(f, a.locked)); });
        if (s.step === costStep) { costControls(a).forEach(function (c) { d.appendChild(c); }); }
        if (s.step === damageStep) { elementControls(a).forEach(function (c) { d.appendChild(c); }); }
        steps.appendChild(d);
      });
      refreshCosts(a);
      refreshElements(a);
    }
    renderStatus(a);
    renderOthers(a, fresh);
    show("edit");
  }

  // ───────── Numbers and money ─────────

  var costStep = 5, damageStep = 6;

  function money(v) {
    if (typeof v !== "number") { return "$0.00"; }
    var s = Math.abs(v).toFixed(2).replace(/\B(?=(\d{3})+(?!\d))/g, ",");
    return (v < 0 ? "-$" : "$") + s;
  }

  function plain(v) { return typeof v === "number" ? String(v) : ""; }

  // Reads a number as typed (commas, a dollar sign and spaces are fine). Empty is null; anything else is NaN.
  function readNumber(text) {
    var t = (text || "").replace(/[,$\s]/g, "");
    if (t === "") { return null; }
    return /^-?(\d+\.?\d*|\.\d+)$/.test(t) ? parseFloat(t) : NaN;
  }

  function setText(id, text) { var e = $(id); if (e) { e.textContent = text; } }

  function setInput(id, value, locked) {
    var input = $(id);
    if (!input) { return; }
    input.disabled = locked;
    if (input !== document.activeElement) { input.value = value; }
  }

  function numberInput(id, label, describedBy) {
    var input = el("input", { id: id, type: "text", inputmode: "decimal", autocomplete: "off", "aria-describedby": describedBy });
    return [el("label", { "for": id, text: label }), input];
  }

  // ───────── Damage by element ─────────

  var quickValues = [0, 25, 50, 75, 100];

  function elementControls(a) {
    var parts = [];
    if (a.damageUndetermined) {
      parts.push(el("p", { className: "hint", text: "Damage is undetermined (Inspection step), so these values don't affect the result." }));
    }
    parts.push(el("p", { className: "hint", text: "0% is untouched, 100% must be fully replaced. Tap a value or type an exact one." }));
    a.elements.forEach(function (e) {
      var base = "el-" + e.id;
      var name = el("p", { className: "element-name", id: base + "-name", text: e.name });
      var share = el("span", { className: "element-share", id: base + "-share" });
      var segments = el("div", { className: "segments", role: "group", "aria-labelledby": base + "-name" });
      quickValues.forEach(function (v) {
        var b = el("button", { type: "button", "data-value": String(v), "aria-pressed": "false", "aria-label": e.name + " " + v + " percent", text: String(v) });
        b.addEventListener("click", function () { saveElement(e.id, v); });
        segments.appendChild(b);
      });
      var input = el("input", { id: base, type: "text", inputmode: "decimal", autocomplete: "off", "aria-describedby": base + "-error" });
      input.addEventListener("change", function () {
        var v = readNumber(input.value);
        if (v === null || isNaN(v) || v < 0 || v > 100) { fieldError(base, e.name + ": type a percentage from 0 to 100."); return; }
        saveElement(e.id, v);
      });
      var title = el("div", { className: "element-title" }, [
        name,
        el("p", { className: "element-meta" }, [share, el("span", { className: "element-dollars", id: base + "-dollars" })])
      ]);
      var entry = el("div", { className: "with-unit" }, [
        el("label", { "for": base, className: "sr-only", text: e.name + ", percent damaged" }),
        input,
        el("span", { className: "suffix", "aria-hidden": "true", text: "%" })
      ]);
      parts.push(el("div", { className: "element", id: base + "-row" }, [
        el("div", { className: "element-head" }, [title, entry]),
        segments,
        el("p", { className: "field-error", id: base + "-error", hidden: true })
      ]));
    });
    parts.push(el("dl", { className: "totals" }, [
      el("dt", { text: "Total damage" }), el("dd", { id: "total-damage" }),
      el("dt", { text: "Replacement cost" }), el("dd", { id: "total-replacement-2" })
    ]));
    return parts;
  }

  function refreshElements(a) {
    a.elements.forEach(function (e) {
      var base = "el-" + e.id;
      setText(base + "-share", typeof e.share === "number" ? e.share.toFixed(1).replace(/\.0$/, "") + "% of cost" : "");
      setText(base + "-dollars", money(e.damageValue) + " damage");
      setInput(base, plain(e.damaged), a.locked);
      var row = $(base + "-row");
      if (!row) { return; }
      Array.prototype.forEach.call(row.querySelectorAll(".segments button"), function (b) {
        b.setAttribute("aria-pressed", Number(b.getAttribute("data-value")) === e.damaged ? "true" : "false");
        b.disabled = a.locked;
      });
    });
    setText("total-damage", money(a.totals.computedDamages));
    setText("total-replacement-2", money(a.totals.replacementCost));
  }

  function saveElement(id, value) {
    send("elements", { elements: [{ id: id, damaged: value }] }, "el-" + id);
  }

  // ───────── Cost adjustments ─────────

  function costControls(a) {
    var parts = [];
    if (a.costAdjustments.length > 0) {
      parts.push(el("h4", { text: "Cost adjustments" }));
      parts.push(el("p", { className: "hint", text: "Features that add to the replacement cost." }));
      a.costAdjustments.forEach(function (c) {
        var base = "ca-" + c.id;
        var title = el("div", { className: "line-head" }, [
          el("p", { className: "line-name", id: base + "-name", text: c.name + (c.units ? " (" + c.units + ")" : "") }),
          el("p", { className: "line-total", id: base + "-total" })
        ]);
        var q = numberInput(base + "-q", "Quantity", base + "-error");
        var u = numberInput(base + "-u", "Unit cost ($)", base + "-error");
        var line = el("div", { className: "line", role: "group", "aria-labelledby": base + "-name" }, [
          title,
          el("div", { className: "pair" }, [el("div", null, q), el("div", null, u)]),
          el("p", { className: "field-error", id: base + "-error", hidden: true })
        ]);
        [q[1], u[1]].forEach(function (i) { i.addEventListener("change", function () { saveCost(c, base); }); });
        parts.push(line);
      });
    }
    parts.push(el("h4", { text: "Additional adjustments" }));
    parts.push(el("p", { className: "hint", text: "Up to 7 lines of your own." }));
    var shown = 0;
    a.additionalAdjustments.forEach(function (x) {
      var base = "aa-" + x.line;
      var used = !!(x.name || typeof x.quantity === "number" || typeof x.unitCost === "number");
      var n = el("label", { "for": base + "-n", text: "Description" });
      var name = el("input", { id: base + "-n", type: "text", autocomplete: "off", maxlength: "255", "aria-describedby": base + "-error" });
      var q = numberInput(base + "-q", "Quantity", base + "-error");
      var u = numberInput(base + "-u", "Unit cost ($)", base + "-error");
      var line = el("div", { className: "line", id: base, role: "group", "aria-labelledby": base + "-title", hidden: !used && shown > 0 ? true : false }, [
        el("div", { className: "line-head" }, [
          el("p", { className: "line-name", id: base + "-title", text: "Line " + x.line }),
          el("p", { className: "line-total", id: base + "-total" })
        ]),
        n, name,
        el("div", { className: "pair" }, [el("div", null, q), el("div", null, u)]),
        el("p", { className: "field-error", id: base + "-error", hidden: true })
      ]);
      if (used || shown === 0) { shown++; }
      [name, q[1], u[1]].forEach(function (i) { i.addEventListener("change", function () { saveAdditional(x.line, base); }); });
      parts.push(line);
    });
    var more = el("button", { type: "button", id: "add-line", className: "secondary", text: "Add a line" });
    more.addEventListener("click", function () {
      var next = $("steps").querySelector(".line[id^='aa-'][hidden]");
      if (next) { next.hidden = false; $(next.id + "-n").focus(); }
      more.hidden = !$("steps").querySelector(".line[id^='aa-'][hidden]");
    });
    parts.push(more);
    parts.push(el("dl", { className: "totals" }, [
      el("dt", { text: "Adjustments" }), el("dd", { id: "total-adjustments" }),
      el("dt", { text: "Replacement cost" }), el("dd", { id: "total-replacement" }),
      el("dt", { text: "Actual cash value" }), el("dd", { id: "total-acv" })
    ]));
    return parts;
  }

  function refreshCosts(a) {
    a.costAdjustments.forEach(function (c) {
      var base = "ca-" + c.id;
      setInput(base + "-q", plain(c.quantity), a.locked);
      setInput(base + "-u", plain(c.unitCost), a.locked);
      setText(base + "-total", money(c.totalCost));
    });
    a.additionalAdjustments.forEach(function (x) {
      var base = "aa-" + x.line;
      setInput(base + "-n", x.name || "", a.locked);
      setInput(base + "-q", plain(x.quantity), a.locked);
      setInput(base + "-u", plain(x.unitCost), a.locked);
      setText(base + "-total", money(x.totalCost));
    });
    var more = $("add-line");
    if (more) { more.hidden = a.locked || !$("steps").querySelector(".line[id^='aa-'][hidden]"); }
    setText("total-adjustments", money(a.totals.totalAdjustments));
    setText("total-replacement", money(a.totals.replacementCost));
    setText("total-acv", money(a.totals.actualCashValue));
  }

  function numbersOf(base, label) {
    var q = readNumber($(base + "-q").value), u = readNumber($(base + "-u").value);
    if (isNaN(q)) { fieldError(base, label + " quantity: type a number."); return null; }
    if (isNaN(u)) { fieldError(base, label + " unit cost: type a number."); return null; }
    return { quantity: q, unitCost: u };
  }

  function saveCost(c, base) {
    var n = numbersOf(base, c.name);
    if (n) { send("costs", { adjustments: [{ id: c.id, quantity: n.quantity, unitCost: n.unitCost }] }, base); }
  }

  function saveAdditional(lineNumber, base) {
    var n = numbersOf(base, "Line " + lineNumber);
    if (n) { send("costs", { additional: [{ line: lineNumber, name: $(base + "-n").value, quantity: n.quantity, unitCost: n.unitCost }] }, base); }
  }

  // ───────── Status ─────────

  function renderStatus(a) {
    var box = $("status-choices");
    var built = Array.prototype.map.call(box.querySelectorAll("button"), function (b) { return b.getAttribute("data-value"); }).join(",");
    var wanted = a.statusChoices.map(function (c) { return c.id; }).join(",");
    if (built !== wanted) {
      box.textContent = "";
      a.statusChoices.forEach(function (c) {
        var b = el("button", { type: "button", "data-value": c.id, "aria-pressed": "false", text: c.name });
        b.addEventListener("click", function () {
          if (b.getAttribute("aria-pressed") === "true") { return; }
          send("status", { status: c.id }, null);
        });
        box.appendChild(b);
      });
    }
    Array.prototype.forEach.call(box.querySelectorAll("button"), function (b) {
      b.setAttribute("aria-pressed", b.getAttribute("data-value") === a.statusValue ? "true" : "false");
    });
    box.hidden = a.statusChoices.length === 0;
    $("status-note").textContent = a.statusNote || "";
    $("status-card").hidden = a.locked;
  }

  // ───────── The structure's other assessments ─────────

  function renderOthers(a, fresh) {
    var list = $("others-list");
    list.textContent = "";
    if (a.assessments.length > 1) {
      a.assessments.forEach(function (o) {
        var b = el("button", { type: "button", "aria-current": o.current ? "true" : null }, [
          el("span", { className: "addr", text: o.label }),
          el("span", { className: "meta", text: o.status + (o.current ? " · Open now" : "") })
        ]);
        b.addEventListener("click", function () { if (!o.current) { openAssessment(o.assessmentId); } });
        list.appendChild(el("li", null, [b]));
      });
    }
    if (fresh) {
      $("another-confirm").hidden = true;
      $("another").hidden = false;
    }
  }

  $("another").addEventListener("click", function () {
    $("another").hidden = true;
    $("another-confirm").hidden = false;
    $("another-yes").focus();
  });

  $("another-no").addEventListener("click", function () {
    $("another-confirm").hidden = true;
    $("another").hidden = false;
    $("another").focus();
  });

  $("another-yes").addEventListener("click", function () {
    var a = state.assessment;
    $("another-yes").disabled = true;
    api("POST", "/api/properties/" + a.propertyId + "/assessments", { another: true }).then(function (r) {
      $("another-yes").disabled = false;
      if (!r.ok) { message(r.data.error || "Couldn't start another assessment."); return; }
      renderAssessment(r.data, true);
      $("save-status").textContent = "Started a new assessment on the Mac.";
    });
  });

  // ───────── The result and its staff gauge ─────────

  var svgNs = "http://www.w3.org/2000/svg";
  var line = 50;   // FEMA's substantial damage line, in percent

  function svg(tag, attrs, text) {
    var e = document.createElementNS(svgNs, tag);
    Object.keys(attrs || {}).forEach(function (k) { e.setAttribute(k, attrs[k]); });
    if (text) { e.textContent = text; }
    return e;
  }

  function percentText(p) { return p.toFixed(1) + "%"; }

  // Water blue below the line, amber within 10 points of it, red at or above it.
  function toneOf(p) { return p >= line ? "substantial" : p >= line - 10 ? "close" : "not-substantial"; }

  function showResult(a) {
    var box = $("result");
    if (typeof a.percentDamage !== "number") { box.hidden = true; return; }
    var p = a.percentDamage;
    var message = (a.damageMessage || "").trim();
    box.className = "result " + toneOf(p);
    $("result-value").textContent = percentText(p);
    $("result-text").textContent = message;
    $("result-text").hidden = !message;

    var fill = Math.max(0, Math.min(100, p));
    var drawing = svg("svg", { "aria-hidden": "true", focusable: "false" });
    drawing.appendChild(svg("rect", { "class": "track", x: "0", y: "8", width: "100%", height: "14", rx: "3" }));
    if (fill > 0) { drawing.appendChild(svg("rect", { "class": "water", x: "0", y: "8", width: fill + "%", height: "14", rx: "3" })); }
    for (var i = 0; i <= 100; i += 10) {
      drawing.appendChild(svg("line", { "class": "tick", x1: i + "%", x2: i + "%", y1: "22", y2: i % 50 === 0 ? "31" : "27" }));
    }
    // The high-water mark: a red line at 50 percent that stands above and below the track.
    drawing.appendChild(svg("line", { "class": "halo", x1: line + "%", x2: line + "%", y1: "1", y2: "29" }));
    drawing.appendChild(svg("line", { "class": "mark", x1: line + "%", x2: line + "%", y1: "1", y2: "29" }));
    drawing.appendChild(svg("text", { x: "0", y: "45", "text-anchor": "middle" }, "0"));
    drawing.appendChild(svg("text", { "class": "line", x: line + "%", y: "45", "text-anchor": "middle" }, line + "%"));
    drawing.appendChild(svg("text", { x: "100%", y: "45", "text-anchor": "middle" }, "100%"));

    var gauge = $("gauge");
    gauge.setAttribute("aria-label", "Gauge: " + percentText(p) + " damaged, " + (p >= line ? "at or above" : "below") +
      " the " + line + "% substantial damage line.");
    gauge.textContent = "";
    gauge.appendChild(drawing);
    box.hidden = false;
  }

  function control(f, locked) {
    var id = fieldId(f.key);
    var wrap = el("div", { className: "field" });
    var input;
    if (f.kind === "yesno") {
      input = el("input", { type: "checkbox", id: id });
      input.checked = f.value === "True";
      wrap.appendChild(el("label", { className: "check", "for": id }, [input, document.createTextNode(f.label)]));
    } else {
      var label = el("label", { "for": id, text: f.label });
      if (f.unit) { label.appendChild(el("span", { className: "unit", text: " (" + f.unit + ")" })); }
      wrap.appendChild(label);
      if (f.options.length > 0 || f.kind === "lookup" || f.kind === "state" || f.kind === "county" || f.kind === "structureType") {
        input = el("select", { id: id });
        if (f.kind !== "structureType") { input.appendChild(el("option", { value: "", text: "(none)" })); }
        f.options.forEach(function (o) { input.appendChild(el("option", { value: o.id, text: o.name })); });
      } else if (f.kind === "multiline") {
        input = el("textarea", { id: id, rows: "4" });
      } else if (f.kind === "date") {
        input = el("input", { id: id, type: "date" });
      } else if (f.kind === "number") {
        input = el("input", { id: id, type: "text", inputmode: "decimal", autocomplete: "off" });
      } else {
        input = el("input", { id: id, type: "text", autocomplete: "off" });
      }
      wrap.appendChild(input);
    }
    input.setAttribute("data-key", f.key);
    input.disabled = locked;
    var error = el("p", { className: "field-error", id: id + "-error", hidden: true });
    input.setAttribute("aria-describedby", error.id);
    wrap.appendChild(error);
    input.addEventListener("change", function () { save(f.key, readValue(input)); });
    setValue(f, input);
    return wrap;
  }

  function readValue(input) {
    if (input.type === "checkbox") { return input.checked ? "True" : "False"; }
    return input.value;
  }

  function setValue(f, input) {
    input = input || $(fieldId(f.key));
    if (!input || input === document.activeElement) { return; }
    var v = f.value == null ? "" : f.value;
    if (input.type === "checkbox") { input.checked = v === "True"; }
    else if (input.type === "date") { input.value = v.substring(0, 10); }
    else { input.value = v; }
    input.disabled = state.assessment.locked;
  }

  // Shows (or clears) the error under a control; idBase is the control's id.
  function fieldError(idBase, text) {
    var e = $(idBase + "-error");
    if (!e) { message(text); return; }
    e.textContent = text || "";
    e.hidden = !text;
  }

  function save(key, value) {
    send("changes", { changes: [{ key: key, value: value }] }, fieldId(key));
  }

  // Saves run one after another, in the order they were made. errorId is where a refusal is shown (null: the page's message).
  function send(what, body, errorId) {
    var id = state.assessment.assessmentId;
    $("save-status").textContent = "Saving…";
    state.saving = state.saving.then(function () {
      return api("POST", "/api/assessments/" + id + "/" + what, body).then(function (r) {
        if (!state.assessment || state.assessment.assessmentId !== id) { return; }
        if (!r.ok) {
          $("save-status").textContent = "";
          showError(errorId, r.data.error || "Not saved.");
          if (r.status === 409) { reload(id); }
          return;
        }
        showError(errorId, "");
        renderAssessment(r.data, false);
        $("save-status").textContent = "Saved on the Mac.";
      });
    });
  }

  function showError(errorId, text) {
    if (errorId) { fieldError(errorId, text); } else { message(text); }
  }

  function reload(id) {
    api("GET", "/api/assessments/" + id).then(function (r) { if (r.ok) { renderAssessment(r.data, false); } });
  }

  $("photo-form").addEventListener("submit", function (e) {
    e.preventDefault();
    var input = $("photo");
    if (!input.files || input.files.length === 0) { message("Take or choose a photo first."); return; }
    if (input.files[0].size > 25 * 1024 * 1024) { message("That photo is larger than 25 MB."); return; }
    var data = new FormData();
    data.append("photo", input.files[0]);
    data.append("description", $("photo-description").value);
    var id = state.assessment.assessmentId;
    var button = $("photo-form").querySelector("button");
    button.disabled = true;
    $("save-status").textContent = "Uploading photo…";
    api("POST", "/api/assessments/" + id + "/photos", data, true).then(function (r) {
      button.disabled = false;
      if (!r.ok) { $("save-status").textContent = ""; message(r.data.error || "The photo wasn't added."); return; }
      message("");
      $("photo-form").reset();
      state.assessment.photoCount = r.data.photoCount;
      $("photo-count").textContent = r.data.photoCount === 1 ? "1 photo" : r.data.photoCount + " photos";
      $("save-status").textContent = "Photo added on the Mac.";
    });
  });

  // ───────── Start ─────────

  var token = new URLSearchParams(window.location.search).get("t");
  if (token) {
    // Keep the one-time token out of the history and address bar.
    window.history.replaceState(null, "", "/");
  }
  api("GET", "/api/session").then(function (r) {
    if (r.ok) { paired(r.data.name); } else { showPair(token, token ? "" : null); }
  });
})();
