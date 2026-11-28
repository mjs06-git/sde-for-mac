(() => {
  "use strict";

  const render = (state, opts) => {
    const { post, onClearSketch, canvas, draggingMarkerId } = opts;

    // Active tool button
    document.querySelectorAll(".tool-palette .tool-btn").forEach((btn) => {
      const on = btn.dataset.mode === state.mode;
      btn.classList.toggle("active", on);
      btn.setAttribute("aria-pressed", String(on));
    });

    // Cursor
    if (canvas) {
      const isCross = state.mode === "SetLocation" || state.sketch !== "None";
      canvas.style.cursor = draggingMarkerId ? "grabbing" : isCross ? "crosshair" : "";
    }

    // Mode bar
    const modeBar = document.getElementById("mode-bar");
    modeBar.innerHTML = "";
    if (state.mode === "Select") {
      modeBar.hidden = false;
      if (state.selectableLayers && state.selectableLayers.length > 0) {
        const lbl = document.createElement("span");
        lbl.className = "hud-text";
        lbl.textContent = "Select in";
        const sel = document.createElement("select");
        sel.className = "hud-select";
        sel.setAttribute("aria-label", "Layer to select in");
        for (const l of state.selectableLayers) {
          const opt = document.createElement("option");
          opt.value = l.key || "";
          opt.textContent = l.name;
          opt.selected = (l.key || "") === (state.selectLayer || "");
          sel.appendChild(opt);
        }
        sel.onchange = () => post({ type: "setSelectTarget", key: sel.value || null });
        modeBar.append(lbl, sel);
      }
      const polyBtn = document.createElement("button");
      polyBtn.type = "button";
      polyBtn.className = "hud-btn" + (state.sketch === "Polygon" ? " active" : "");
      polyBtn.setAttribute("aria-pressed", String(state.sketch === "Polygon"));
      polyBtn.textContent = "Draw an Area";
      polyBtn.onclick = () => post({ type: "togglePolygonSelect" });
      const attrBtn = document.createElement("button");
      attrBtn.type = "button";
      attrBtn.className = "hud-btn";
      attrBtn.textContent = "By Attribute…";
      attrBtn.onclick = () => post({ type: "selectByAttribute" });
      const locBtn = document.createElement("button");
      locBtn.type = "button";
      locBtn.className = "hud-btn";
      locBtn.textContent = "By Location…";
      locBtn.onclick = () => post({ type: "selectByLocation" });
      modeBar.append(polyBtn, attrBtn, locBtn);
    } else if (state.mode === "SetLocation") {
      modeBar.hidden = false;
      const lbl = document.createElement("span");
      lbl.className = "hud-text";
      lbl.textContent = "Structure to place";
      modeBar.appendChild(lbl);
      if (state.placeChoices && state.placeChoices.length > 0) {
        const sel = document.createElement("select");
        sel.className = "hud-select";
        sel.setAttribute("aria-label", "Structure to place");
        for (const p of state.placeChoices) {
          const opt = document.createElement("option");
          opt.value = p.id;
          opt.textContent = p.address;
          opt.selected = p.id === state.placeTargetId;
          sel.appendChild(opt);
        }
        sel.onchange = () => post({ type: "setPlaceTarget", id: sel.value });
        modeBar.appendChild(sel);
      } else {
        const hint = document.createElement("span");
        hint.className = "hud-text";
        hint.textContent = state.hint || "Click the map or drag a dot";
        modeBar.appendChild(hint);
      }
    } else if (state.mode === "Measure") {
      modeBar.hidden = false;
      const seg = document.createElement("div");
      seg.className = "segmented";
      const dBtn = document.createElement("button");
      dBtn.type = "button";
      dBtn.className = "hud-btn" + (!state.measureArea ? " active" : "");
      dBtn.setAttribute("aria-pressed", String(!state.measureArea));
      dBtn.textContent = "Distance";
      dBtn.onclick = () => post({ type: "setMeasureArea", area: false });
      const aBtn = document.createElement("button");
      aBtn.type = "button";
      aBtn.className = "hud-btn" + (state.measureArea ? " active" : "");
      aBtn.setAttribute("aria-pressed", String(!!state.measureArea));
      aBtn.textContent = "Area";
      aBtn.onclick = () => post({ type: "setMeasureArea", area: true });
      seg.append(dBtn, aBtn);
      modeBar.appendChild(seg);
      if (state.measurementLine) {
        const read = document.createElement("span");
        read.className = "hud-strong";
        read.textContent = state.measurementLine;
        modeBar.appendChild(read);
        const clr = document.createElement("button");
        clr.type = "button";
        clr.className = "hud-btn";
        clr.textContent = "Clear";
        clr.onclick = () => {
          onClearSketch();
          post({ type: "clearMeasurement" });
        };
        modeBar.appendChild(clr);
      }
    } else {
      modeBar.hidden = true;
    }

    // Empty state
    document.getElementById("empty-state").hidden = (state.onMapCount || 0) > 0;

    // Legend card
    document.getElementById("legend-title").textContent = state.legendTitle || "Structures";
    document.getElementById("legend-count").textContent = state.countText || "";
    const toggleBtn = document.getElementById("legend-toggle");
    toggleBtn.textContent = state.legendOpen ? "▾" : "▴";
    toggleBtn.setAttribute("aria-expanded", String(!!state.legendOpen));
    const body = document.getElementById("legend-body");
    body.hidden = !state.legendOpen;
    body.innerHTML = "";
    if (state.display === "Heatmap") {
      const ramp = document.createElement("div");
      ramp.className = "heat-ramp";
      const labels = document.createElement("div");
      labels.className = "heat-labels";
      labels.innerHTML = "<span>Less</span><span>More</span>";
      const note = document.createElement("div");
      note.className = "hud-text";
      note.textContent = state.heatLegendNote || "";
      body.append(ramp, labels, note);
    } else {
      for (const row of state.legendRows || []) {
        const el = document.createElement("div");
        el.className = "legend-row";
        const dot = document.createElement("span");
        dot.className = "legend-dot";
        dot.style.background = row.color || "#6b7280";
        const lbl = document.createElement("span");
        lbl.className = "legend-label";
        lbl.textContent = row.label;
        el.append(dot, lbl);
        if (row.count) {
          const num = document.createElement("span");
          num.className = "legend-num";
          num.textContent = row.count;
          el.appendChild(num);
        }
        body.appendChild(el);
      }
    }
    if (state.surfaceLegendRows && state.surfaceLegendRows.length > 0) {
      const div = document.createElement("div");
      div.className = "legend-divider";
      const title = document.createElement("div");
      title.className = "legend-title";
      title.textContent = state.surfaceLegendTitle || "Water surface";
      body.append(div, title);
      for (const row of state.surfaceLegendRows) {
        const el = document.createElement("div");
        el.className = "legend-row";
        const sw = document.createElement("span");
        sw.className = "legend-swatch";
        sw.style.background = row.color;
        const lbl = document.createElement("span");
        lbl.className = "legend-label";
        lbl.textContent = row.label;
        el.append(sw, lbl);
        body.appendChild(el);
      }
    }

    // Selection / Status bar
    const selBar = document.getElementById("selection-bar");
    selBar.innerHTML = "";
    if (state.hasChecked || state.hasLayerSelection || state.statusMessage) {
      selBar.hidden = false;
      if (state.hasChecked) {
        const cnt = document.createElement("span");
        cnt.className = "hud-strong";
        cnt.textContent = state.selectionText;
        const showBtn = document.createElement("button");
        showBtn.type = "button";
        showBtn.className = "hud-btn";
        showBtn.textContent = "Show in Structures";
        showBtn.onclick = () => post({ type: "showChecked" });
        const fieldBtn = document.createElement("button");
        fieldBtn.type = "button";
        fieldBtn.className = "hud-btn";
        fieldBtn.textContent = "Set a Field…";
        fieldBtn.onclick = () => post({ type: "setFieldOnSelection" });
        const teamBtn = document.createElement("button");
        teamBtn.type = "button";
        teamBtn.className = "hud-btn";
        teamBtn.textContent = "Assign to Team…";
        teamBtn.onclick = () => post({ type: "assignSelection" });
        const copyBtn = document.createElement("button");
        copyBtn.type = "button";
        copyBtn.className = "hud-btn";
        copyBtn.textContent = "Copy Values…";
        copyBtn.onclick = () => post({ type: "copyValues" });
        const rule = document.createElement("div");
        rule.className = "map-rule vertical";
        const clr = document.createElement("button");
        clr.type = "button";
        clr.className = "hud-btn ghost";
        clr.textContent = "Clear";
        clr.onclick = () => post({ type: "clearChecked" });
        selBar.append(cnt, showBtn, fieldBtn, teamBtn, copyBtn, rule, clr);
      } else if (state.hasLayerSelection) {
        const cnt = document.createElement("span");
        cnt.className = "hud-strong";
        cnt.textContent = state.layerSelectionText;
        const tblBtn = document.createElement("button");
        tblBtn.type = "button";
        tblBtn.className = "hud-btn";
        tblBtn.textContent = "Show in Table";
        tblBtn.onclick = () => post({ type: "showSelectionInTable" });
        const rule = document.createElement("div");
        rule.className = "map-rule vertical";
        const clr = document.createElement("button");
        clr.type = "button";
        clr.className = "hud-btn ghost";
        clr.textContent = "Clear";
        clr.onclick = () => post({ type: "clearLayerSelection" });
        selBar.append(cnt, tblBtn, rule, clr);
      } else if (state.statusMessage) {
        const msg = document.createElement("span");
        msg.className = "hud-text";
        msg.textContent = state.statusMessage;
        selBar.appendChild(msg);
      }
    } else {
      selBar.hidden = true;
    }

    // Attribution
    const attr = document.getElementById("attribution");
    if (state.attribution) {
      attr.textContent = state.attribution;
      attr.hidden = false;
    } else {
      attr.hidden = true;
    }

    announce(state);
    placeSelectionBar();
  };

  // The bars above are rebuilt on every state push, so a live region on them would repeat itself. VoiceOver hears
  // this one instead, and only when the words change.
  let lastAnnouncement = "";
  const announce = (state) => {
    const parts = [
      state.hasChecked ? state.selectionText : state.hasLayerSelection ? state.layerSelectionText : state.statusMessage,
      state.mode === "Measure" ? state.measurementLine : "",
    ];
    const text = parts.filter(Boolean).join(". ");
    if (text !== lastAnnouncement) {
      lastAnnouncement = text;
      document.getElementById("hud-live").textContent = text;
    }
  };

  const placeSelectionBar = () => {
    const selBar = document.getElementById("selection-bar");
    const legend = document.getElementById("legend-card");
    if (!selBar || selBar.hidden || !legend) return;
    selBar.style.bottom = "12px";
    const sRect = selBar.getBoundingClientRect();
    const lRect = legend.getBoundingClientRect();
    if (sRect.left < lRect.right + 8) {
      selBar.style.bottom = `${Math.ceil(lRect.height) + 22}px`;
    }
  };

  window.addEventListener("resize", placeSelectionBar);

  const wire = ({ post, zoomIn, zoomOut, resetNorth }) => {
    document.querySelectorAll(".tool-palette .tool-btn").forEach((btn) => {
      btn.addEventListener("click", () => post({ type: "setMode", mode: btn.dataset.mode }));
    });
    document.getElementById("legend-toggle").addEventListener("click", () => post({ type: "toggleLegend" }));
    document.getElementById("zoom-in").addEventListener("click", zoomIn);
    document.getElementById("zoom-out").addEventListener("click", zoomOut);
    document.getElementById("zoom-fit").addEventListener("click", () => post({ type: "fitAll" }));
    document.getElementById("reset-north").addEventListener("click", resetNorth);
  };

  window.sdeHud = { render, wire };
})();
