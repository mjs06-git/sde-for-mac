(() => {
  "use strict";

  const post = (payload) => {
    try {
      window.webkit?.messageHandlers?.sde?.postMessage(JSON.stringify(payload));
    } catch (_) {
      // Ignore bridge errors during teardown.
    }
  };

  const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
  const dur = (ms) => (state.reduceMotion || reducedMotion.matches ? 0 : ms);

  let state = {
    theme: "light",
    highContrast: false,
    reduceMotion: false,
    accent: "#1769c2",
    basemap: { id: "none", maxZoom: 19, hasTiles: false, retina: false, attribution: "" },
    display: "Markers",
    heatByDamage: true,
    mode: "Browse",
    sketch: "None",
    selectLayer: null,
    selectableLayers: [],
    placeChoices: [],
    placeTargetId: null,
    measureArea: false,
    measurementLine: "",
    hint: "",
    legendTitle: "Structures",
    countText: "",
    legendOpen: true,
    legendRows: [],
    heatLegendNote: "",
    surfaceLegendTitle: "",
    surfaceLegendRows: [],
    hasChecked: false,
    selectionText: "",
    hasLayerSelection: false,
    layerSelectionText: "",
    statusMessage: "",
    attribution: "",
    markers: [],
    selectedIds: [],
    highlightedId: null,
    tileOverlays: [],
    featureLayers: [],
    rasters: [],
    onMapCount: 0,
  };

  const LABEL_FONT = '"Public Sans", sans-serif';
  const labelColors = () => (state.theme === "dark" ? { text: "#f3f4f6", halo: "#151921" } : { text: "#111827", halo: "#ffffff" });
  const labelLayout = (extra) => Object.assign({
    "text-field": ["get", "_label"],
    "text-font": ["Public Sans Regular"],
    "text-size": 11,
    "text-max-width": 12,
  }, extra);
  const labelPaint = () => {
    const c = labelColors();
    return { "text-color": c.text, "text-halo-color": c.halo, "text-halo-width": 1.5 };
  };

  const sketchPoints = [];
  let sketchFinished = false;
  let draggingMarkerId = null;
  let dragRafPending = false;
  let boxStart = null;
  let boxAdditive = false;
  let suppressNextClick = false;
  let mapReady = false;
  const clusterMarkers = new Map();

  const map = new maplibregl.Map({
    container: "map",
    style: {
      version: 8,
      // Required by MapLibre for text; never fetched, because every glyph is drawn from the embedded font (below).
      glyphs: `${window.location.href.replace(/[^/]*$/, "")}glyphs/{fontstack}/{range}.pbf`,
      sources: {},
      layers: [{ id: "bg", type: "background", paint: { "background-color": "#eef1f5" } }],
    },
    center: [-95.7, 37.1],
    zoom: 4,
    minZoom: 2,
    maxZoom: 19,
    dragRotate: true,
    pitchWithRotate: false,
    attributionControl: false,
    canvasContextAttributes: { preserveDrawingBuffer: true, antialias: true },
    localIdeographFontFamily: LABEL_FONT,
  });

  // Labels are drawn from the Public Sans the page embeds, so they work offline and match the Classic map and print.
  // shortcut: overrides a private GlyphManager method (MapLibre 5.3) that limits local glyphs to CJK; recheck it when
  // maplibre-gl.js is updated, or bundle glyph .pbf files instead.
  if (map.style?.glyphManager) map.style.glyphManager._doesCharSupportLocalGlyph = () => true;

  // WebKit can take the GPU context away (memory pressure, GPU switch). MapLibre rebuilds when it comes back;
  // if it doesn't come back the map stays blank, so hand over to the Classic map.
  let contextLostTimer = null;
  map.on("webglcontextlost", () => {
    clearTimeout(contextLostTimer);
    contextLostTimer = setTimeout(() => post({ type: "failed", reason: "it lost its graphics context" }), 5000);
  });
  map.on("webglcontextrestored", () => clearTimeout(contextLostTimer));

  const hoverPopup = new maplibregl.Popup({
    closeButton: false,
    closeOnClick: false,
    offset: 12,
    maxWidth: "260px",
  });

  const emptyFc = () => ({ type: "FeatureCollection", features: [] });
  const absUrl = (rel) => new URL(rel, window.location.href).href;
  const tileUrlFor = (id, retina) => {
    const scale = retina && window.devicePixelRatio > 1 ? "?scale=2" : "";
    return `${absUrl(`tiles/${encodeURIComponent(id)}/`)}{z}/{x}/{y}${scale}`;
  };

  const hexToRgba = (hex, alpha) => {
    const m = /^#?([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i.exec(hex || "");
    return m ? `rgba(${parseInt(m[1], 16)},${parseInt(m[2], 16)},${parseInt(m[3], 16)},${alpha})` : `rgba(23,105,194,${alpha})`;
  };

  const ensureCoreSourcesAndLayers = () => {
    if (!map.getSource("sde-structures")) {
      map.addSource("sde-structures", { type: "geojson", data: emptyFc(), promoteId: "id" });
    }
    if (!map.getSource("sde-clusters")) {
      map.addSource("sde-clusters", {
        type: "geojson",
        data: emptyFc(),
        cluster: true,
        clusterRadius: 46,
        clusterMaxZoom: 16,
        clusterProperties: { worst: ["max", ["get", "severity"]] },
      });
    }
    if (!map.getSource("sde-sketch")) {
      map.addSource("sde-sketch", { type: "geojson", data: emptyFc() });
    }

    const accent = state.accent || "#1769c2";
    const outline = state.theme === "dark" ? "#1e2430" : "#ffffff";

    if (!map.getLayer("sde-heatmap")) {
      map.addLayer({
        id: "sde-heatmap",
        type: "heatmap",
        source: "sde-structures",
        layout: { visibility: "none" },
        paint: {
          "heatmap-weight": ["get", "heatWeight"],
          "heatmap-intensity": ["interpolate", ["linear"], ["zoom"], 6, 0.8, 15, 2.2],
          "heatmap-radius": ["interpolate", ["linear"], ["zoom"], 6, 16, 14, 34, 18, 46],
          "heatmap-opacity": 0.78,
          "heatmap-color": [
            "interpolate", ["linear"], ["heatmap-density"],
            0, "rgba(46, 134, 193, 0)",
            0.2, "rgba(46, 134, 193, 0.55)",
            0.5, "rgba(245, 158, 11, 0.78)",
            0.85, "rgba(220, 38, 38, 0.92)",
            1, "#991b1b",
          ],
        },
      });
    }
    if (!map.getLayer("sde-selected-rings")) {
      map.addLayer({
        id: "sde-selected-rings",
        type: "circle",
        source: "sde-structures",
        filter: ["==", ["get", "selected"], true],
        paint: { "circle-radius": 11, "circle-color": "transparent", "circle-stroke-width": 2.5, "circle-stroke-color": accent },
      });
    }
    if (!map.getLayer("sde-highlight-ring")) {
      map.addLayer({
        id: "sde-highlight-ring",
        type: "circle",
        source: "sde-structures",
        filter: ["==", ["get", "highlighted"], true],
        paint: { "circle-radius": 13, "circle-color": hexToRgba(accent, 0.16), "circle-stroke-width": 2.5, "circle-stroke-color": accent },
      });
    }
    if (!map.getLayer("sde-markers")) {
      map.addLayer({
        id: "sde-markers",
        type: "circle",
        source: "sde-structures",
        paint: { "circle-radius": 6.5, "circle-color": ["get", "color"], "circle-stroke-width": 2, "circle-stroke-color": outline },
      });
    }
    if (!map.getLayer("sde-cluster-circles")) {
      map.addLayer({
        id: "sde-cluster-circles",
        type: "circle",
        source: "sde-clusters",
        filter: ["has", "point_count"],
        layout: { visibility: "none" },
        paint: {
          "circle-radius": ["step", ["get", "point_count"], 15, 10, 19, 50, 23, 200, 27],
          "circle-color": ["match", ["get", "worst"], 4, "#dc2626", 3, "#d97706", 2, "#16a34a", 1, "#6b7280", "#4b5563"],
          "circle-stroke-width": 2.5,
          "circle-stroke-color": outline,
        },
      });
    }
    if (!map.getLayer("sde-cluster-unclustered")) {
      map.addLayer({
        id: "sde-cluster-unclustered",
        type: "circle",
        source: "sde-clusters",
        filter: ["!", ["has", "point_count"]],
        layout: { visibility: "none" },
        paint: { "circle-radius": 6.5, "circle-color": ["get", "color"], "circle-stroke-width": 2, "circle-stroke-color": outline },
      });
    }
    if (!map.getLayer("sde-sketch-fill")) {
      map.addLayer({
        id: "sde-sketch-fill",
        type: "fill",
        source: "sde-sketch",
        filter: ["==", ["geometry-type"], "Polygon"],
        paint: { "fill-color": accent, "fill-opacity": 0.16 },
      });
    }
    if (!map.getLayer("sde-sketch-line")) {
      map.addLayer({
        id: "sde-sketch-line",
        type: "line",
        source: "sde-sketch",
        filter: ["any", ["==", ["geometry-type"], "LineString"], ["==", ["geometry-type"], "Polygon"]],
        paint: { "line-color": accent, "line-width": 2.5, "line-dasharray": [3, 2] },
      });
    }
    if (!map.getLayer("sde-sketch-pts")) {
      map.addLayer({
        id: "sde-sketch-pts",
        type: "circle",
        source: "sde-sketch",
        filter: ["==", ["geometry-type"], "Point"],
        paint: { "circle-radius": 4.5, "circle-color": "#ffffff", "circle-stroke-width": 2, "circle-stroke-color": accent },
      });
    }
    // Addresses (or the chosen field) above the dots; MapLibre leaves out labels that would overlap, as Classic does.
    if (!map.getLayer("sde-marker-labels")) {
      map.addLayer({
        id: "sde-marker-labels",
        type: "symbol",
        source: "sde-structures",
        layout: labelLayout({ "text-field": ["get", "label"], "text-anchor": "bottom", "text-offset": [0, -0.9] }),
        paint: labelPaint(),
      });
    }
  };

  let activeBasemapKey = null;
  let activeTileOverlayKeys = [];
  let activeRasterKeys = [];
  let activeFeatureLayerKeys = [];
  const featureLayerRevisions = new Map();
  const rasterRevisions = new Map();

  const beforeOverlayAnchor = () => (map.getLayer("sde-heatmap") ? "sde-heatmap" : undefined);

  const syncBasemap = () => {
    const bm = state.basemap || { id: "none", hasTiles: false };
    const wantKey = bm.hasTiles ? `bm:${bm.id}:${bm.retina ? 2 : 1}` : null;
    if (wantKey === activeBasemapKey) return;

    if (map.getLayer("sde-basemap")) map.removeLayer("sde-basemap");
    if (map.getSource("sde-basemap")) map.removeSource("sde-basemap");

    if (bm.hasTiles) {
      map.addSource("sde-basemap", {
        type: "raster",
        tiles: [tileUrlFor(bm.id, bm.retina)],
        tileSize: 256,
        maxzoom: bm.maxZoom || 19,
      });
      const firstDynamic = map.getStyle().layers.find((l) => l.id !== "bg")?.id;
      map.addLayer(
        {
          id: "sde-basemap",
          type: "raster",
          source: "sde-basemap",
          paint: { "raster-fade-duration": 150, "raster-brightness-max": state.theme === "dark" ? 0.86 : 1.0 },
        },
        firstDynamic
      );
    }
    activeBasemapKey = wantKey;
  };

  const syncTileOverlays = () => {
    const overlays = (state.tileOverlays || []).filter((o) => o.visible);
    const wantedIds = overlays.map((o) => `tile-ov-${o.key}`);

    for (const oldId of activeTileOverlayKeys) {
      if (!wantedIds.includes(oldId)) {
        if (map.getLayer(oldId)) map.removeLayer(oldId);
        if (map.getSource(oldId)) map.removeSource(oldId);
      }
    }

    const anchor = beforeOverlayAnchor();
    for (const ov of overlays) {
      const id = `tile-ov-${ov.key}`;
      if (!map.getSource(id)) {
        map.addSource(id, { type: "raster", tiles: [tileUrlFor(ov.sourceId, ov.retina)], tileSize: 256, maxzoom: ov.maxZoom || 19 });
      }
      if (!map.getLayer(id)) {
        map.addLayer({ id, type: "raster", source: id, paint: { "raster-opacity": ov.opacity ?? 0.75, "raster-fade-duration": 120 } }, anchor);
      } else {
        map.setPaintProperty(id, "raster-opacity", ov.opacity ?? 0.75);
        if (anchor) map.moveLayer(id, anchor);
      }
    }
    activeTileOverlayKeys = wantedIds;
  };

  const syncRasters = () => {
    const rasters = state.rasters || [];
    const wantedIds = rasters.map((r) => `raster-${r.key}`);
    for (const oldId of activeRasterKeys) {
      if (!wantedIds.includes(oldId)) {
        if (map.getLayer(oldId)) map.removeLayer(oldId);
        if (map.getSource(oldId)) map.removeSource(oldId);
        rasterRevisions.delete(oldId);
      }
    }
    const anchor = beforeOverlayAnchor();
    for (const r of rasters) {
      const id = `raster-${r.key}`;
      const coords = [[r.minLon, r.maxLat], [r.maxLon, r.maxLat], [r.maxLon, r.minLat], [r.minLon, r.minLat]];
      const url = absUrl(`rasters/${encodeURIComponent(r.key)}.png?v=${r.rev}`);
      if (map.getSource(id)) {
        if (rasterRevisions.get(id) !== r.rev) {
          map.getSource(id).updateImage({ url, coordinates: coords });
          rasterRevisions.set(id, r.rev);
        }
        map.setPaintProperty(id, "raster-opacity", r.opacity ?? 0.72);
        if (anchor) map.moveLayer(id, anchor);
      } else {
        map.addSource(id, { type: "image", url, coordinates: coords });
        map.addLayer({ id, type: "raster", source: id, paint: { "raster-opacity": r.opacity ?? 0.72, "raster-fade-duration": 0 } }, anchor);
        rasterRevisions.set(id, r.rev);
      }
    }
    activeRasterKeys = wantedIds;
  };

  const syncFeatureLayers = () => {
    const layers = (state.featureLayers || []).filter((l) => l.visible);
    const wantedKeys = layers.map((l) => l.key);

    for (const oldKey of activeFeatureLayerKeys) {
      if (!wantedKeys.includes(oldKey)) {
        for (const suffix of ["-fill", "-line", "-pts", "-labels", "-line-labels"]) {
          const lid = `fl-${oldKey}${suffix}`;
          if (map.getLayer(lid)) map.removeLayer(lid);
        }
        const sid = `fl-${oldKey}`;
        if (map.getSource(sid)) map.removeSource(sid);
        featureLayerRevisions.delete(oldKey);
      }
    }

    const anchor = beforeOverlayAnchor();
    const accent = state.accent || "#1769c2";
    for (const l of layers) {
      const sid = `fl-${l.key}`;
      const revTag = `${l.revision}:${l.selectionRevision}`;
      const url = absUrl(`layers/${encodeURIComponent(l.key)}.geojson?v=${encodeURIComponent(revTag)}`);
      if (!map.getSource(sid)) {
        map.addSource(sid, { type: "geojson", data: url });
        featureLayerRevisions.set(l.key, revTag);
      } else if (featureLayerRevisions.get(l.key) !== revTag) {
        map.getSource(sid).setData(url);
        featureLayerRevisions.set(l.key, revTag);
      }

      const opacity = l.opacity ?? 1;
      const fillId = `${sid}-fill`;
      const lineId = `${sid}-line`;
      const ptsId = `${sid}-pts`;

      if (!map.getLayer(fillId)) {
        map.addLayer({
          id: fillId,
          type: "fill",
          source: sid,
          filter: ["any", ["==", ["geometry-type"], "Polygon"], ["==", ["geometry-type"], "MultiPolygon"]],
          paint: {
            "fill-color": ["case", ["==", ["get", "_selected"], true], accent, ["get", "_fill"]],
            "fill-opacity": ["*", ["get", "_fillOpacity"], opacity],
          },
        }, anchor);
      } else {
        map.setPaintProperty(fillId, "fill-color", ["case", ["==", ["get", "_selected"], true], accent, ["get", "_fill"]]);
        map.setPaintProperty(fillId, "fill-opacity", ["*", ["get", "_fillOpacity"], opacity]);
        if (anchor) map.moveLayer(fillId, anchor);
      }

      if (!map.getLayer(lineId)) {
        map.addLayer({
          id: lineId,
          type: "line",
          source: sid,
          filter: ["!=", ["geometry-type"], "Point"],
          paint: {
            "line-color": ["case", ["==", ["get", "_selected"], true], accent, ["get", "_stroke"]],
            "line-width": ["case", ["==", ["get", "_selected"], true], ["+", ["get", "_strokeWidth"], 1.5], ["get", "_strokeWidth"]],
            "line-opacity": ["*", ["get", "_strokeOpacity"], opacity],
          },
        }, anchor);
      } else {
        map.setPaintProperty(lineId, "line-color", ["case", ["==", ["get", "_selected"], true], accent, ["get", "_stroke"]]);
        map.setPaintProperty(lineId, "line-opacity", ["*", ["get", "_strokeOpacity"], opacity]);
        if (anchor) map.moveLayer(lineId, anchor);
      }

      if (!map.getLayer(ptsId)) {
        map.addLayer({
          id: ptsId,
          type: "circle",
          source: sid,
          filter: ["any", ["==", ["geometry-type"], "Point"], ["==", ["geometry-type"], "MultiPoint"]],
          paint: {
            "circle-radius": ["get", "_pointRadius"],
            "circle-color": ["get", "_fill"],
            "circle-opacity": opacity,
            "circle-stroke-width": ["case", ["==", ["get", "_selected"], true], 2.5, ["get", "_strokeWidth"]],
            "circle-stroke-color": ["case", ["==", ["get", "_selected"], true], accent, ["get", "_stroke"]],
          },
        }, anchor);
      } else {
        map.setPaintProperty(ptsId, "circle-opacity", opacity);
        map.setPaintProperty(ptsId, "circle-stroke-color", ["case", ["==", ["get", "_selected"], true], accent, ["get", "_stroke"]]);
        if (anchor) map.moveLayer(ptsId, anchor);
      }

      // The layer's label field (_label, empty when none is chosen): on top of every layer, along the middle of lines.
      if (!map.getLayer(`${sid}-labels`)) {
        map.addLayer({
          id: `${sid}-labels`,
          type: "symbol",
          source: sid,
          filter: ["all", ["!=", ["geometry-type"], "LineString"], ["!=", ["geometry-type"], "MultiLineString"]],
          layout: labelLayout({}),
          paint: labelPaint(),
        });
        map.addLayer({
          id: `${sid}-line-labels`,
          type: "symbol",
          source: sid,
          filter: ["any", ["==", ["geometry-type"], "LineString"], ["==", ["geometry-type"], "MultiLineString"]],
          layout: labelLayout({ "symbol-placement": "line-center" }),
          paint: labelPaint(),
        });
      }
    }
    activeFeatureLayerKeys = wantedKeys;
  };

  const buildStructureFc = () => {
    const selectedSet = new Set(state.selectedIds || []);
    return {
      type: "FeatureCollection",
      features: (state.markers || []).map((m) => ({
        type: "Feature",
        id: m.id,
        geometry: { type: "Point", coordinates: [m.lon, m.lat] },
        properties: {
          id: m.id,
          color: m.color || "#6b7280",
          label: m.label || "",
          address: m.address || "",
          detail: m.detail || "",
          severity: m.severity ?? 0,
          heatWeight: state.heatByDamage ? Math.max(0.15, (m.percentDamage ?? 20) / 100) : 1,
          selected: selectedSet.has(m.id),
          highlighted: m.id === state.highlightedId,
        },
      })),
    };
  };

  const clearClusterLabels = () => {
    for (const mk of clusterMarkers.values()) mk.remove();
    clusterMarkers.clear();
  };

  const syncClusterLabels = () => {
    if (!mapReady || state.display !== "Clusters" || !map.getLayer("sde-cluster-circles")) {
      clearClusterLabels();
      return;
    }
    const visible = map.queryRenderedFeatures({ layers: ["sde-cluster-circles"] });
    const seen = new Set();
    for (const f of visible) {
      const cid = f.properties.cluster_id;
      if (cid == null || seen.has(cid)) continue;
      seen.add(cid);
      const count = f.properties.point_count_abbreviated ?? f.properties.point_count;
      let mk = clusterMarkers.get(cid);
      if (!mk) {
        const el = document.createElement("div");
        el.className = "cluster-count";
        el.textContent = String(count);
        mk = new maplibregl.Marker({ element: el, anchor: "center" }).setLngLat(f.geometry.coordinates).addTo(map);
        clusterMarkers.set(cid, mk);
      } else {
        mk.setLngLat(f.geometry.coordinates);
        if (mk.getElement().textContent !== String(count)) mk.getElement().textContent = String(count);
      }
    }
    for (const [cid, mk] of clusterMarkers) {
      if (!seen.has(cid)) {
        mk.remove();
        clusterMarkers.delete(cid);
      }
    }
  };

  const syncStructures = (structuresOnly = false) => {
    const fc = buildStructureFc();
    map.getSource("sde-structures")?.setData(fc);
    if (!structuresOnly) map.getSource("sde-clusters")?.setData(fc);

    const isMarkers = state.display === "Markers";
    const isClusters = state.display === "Clusters";
    const isHeatmap = state.display === "Heatmap";

    map.setLayoutProperty("sde-markers", "visibility", isMarkers ? "visible" : "none");
    map.setLayoutProperty("sde-marker-labels", "visibility", isMarkers ? "visible" : "none");
    map.setLayoutProperty("sde-selected-rings", "visibility", isMarkers ? "visible" : "none");
    map.setLayoutProperty("sde-highlight-ring", "visibility", isMarkers ? "visible" : "none");
    map.setLayoutProperty("sde-cluster-circles", "visibility", isClusters ? "visible" : "none");
    map.setLayoutProperty("sde-cluster-unclustered", "visibility", isClusters ? "visible" : "none");
    map.setLayoutProperty("sde-heatmap", "visibility", isHeatmap ? "visible" : "none");
    if (!isClusters) clearClusterLabels();
  };

  const syncTheme = () => {
    const accent = state.accent || "#1769c2";
    document.documentElement.dataset.theme = state.theme === "dark" ? "dark" : "light";
    document.documentElement.dataset.contrast = state.highContrast ? "high" : "normal";
    document.documentElement.dataset.motion = dur(1) ? "on" : "off";
    document.documentElement.style.setProperty("--accent", accent);
    const bg = state.theme === "dark" ? "#151921" : "#eef1f5";
    const outline = state.theme === "dark" ? "#1e2430" : "#ffffff";
    if (map.getLayer("bg")) map.setPaintProperty("bg", "background-color", bg);
    for (const lid of ["sde-markers", "sde-cluster-circles", "sde-cluster-unclustered"]) {
      if (map.getLayer(lid)) map.setPaintProperty(lid, "circle-stroke-color", outline);
    }
    if (map.getLayer("sde-selected-rings")) map.setPaintProperty("sde-selected-rings", "circle-stroke-color", accent);
    if (map.getLayer("sde-highlight-ring")) {
      map.setPaintProperty("sde-highlight-ring", "circle-stroke-color", accent);
      map.setPaintProperty("sde-highlight-ring", "circle-color", hexToRgba(accent, 0.16));
    }
    if (map.getLayer("sde-sketch-fill")) map.setPaintProperty("sde-sketch-fill", "fill-color", accent);
    if (map.getLayer("sde-sketch-line")) map.setPaintProperty("sde-sketch-line", "line-color", accent);
    if (map.getLayer("sde-sketch-pts")) map.setPaintProperty("sde-sketch-pts", "circle-stroke-color", accent);
    if (map.getLayer("sde-basemap")) map.setPaintProperty("sde-basemap", "raster-brightness-max", state.theme === "dark" ? 0.86 : 1.0);
    const lc = labelColors();
    for (const l of map.getStyle().layers) {
      if (l.type === "symbol") {
        map.setPaintProperty(l.id, "text-color", lc.text);
        map.setPaintProperty(l.id, "text-halo-color", lc.halo);
      }
    }
  };

  const renderSketch = (previewLngLat = null) => {
    const src = map.getSource("sde-sketch");
    if (!src) return;
    if (sketchPoints.length === 0) {
      src.setData(emptyFc());
      return;
    }
    const pts = previewLngLat && !sketchFinished ? [...sketchPoints, previewLngLat] : sketchPoints;
    const features = sketchPoints.map((p) => ({ type: "Feature", geometry: { type: "Point", coordinates: p }, properties: {} }));
    if (state.sketch === "Polygon" && pts.length >= 3) {
      features.push({ type: "Feature", geometry: { type: "Polygon", coordinates: [[...pts, pts[0]]] }, properties: {} });
    } else if (pts.length >= 2) {
      features.push({ type: "Feature", geometry: { type: "LineString", coordinates: pts }, properties: {} });
    }
    src.setData({ type: "FeatureCollection", features });
  };

  const renderHud = () => {
    window.sdeHud?.render(state, {
      post,
      canvas: map.getCanvas(),
      draggingMarkerId,
      onClearSketch: () => {
        sketchPoints.length = 0;
        sketchFinished = false;
        renderSketch();
      },
    });
  };

  window.sdeHud?.wire({
    post,
    zoomIn: () => map.zoomIn({ duration: dur(180) }),
    zoomOut: () => map.zoomOut({ duration: dur(180) }),
    resetNorth: () => map.resetNorth({ duration: dur(220) }),
  });

  const reportView = () => {
    const b = map.getBounds();
    const c = map.getCenter();
    document.getElementById("reset-north").hidden = Math.abs(map.getBearing()) < 0.5;
    post({
      type: "viewChanged",
      centerLat: c.lat,
      centerLon: c.lng,
      zoom: map.getZoom(),
      minLon: b.getWest(),
      minLat: b.getSouth(),
      maxLon: b.getEast(),
      maxLat: b.getNorth(),
    });
  };

  const markerQueryBox = (pt) => [[pt.x - 10, pt.y - 10], [pt.x + 10, pt.y + 10]];

  map.on("mousemove", (e) => {
    if (draggingMarkerId) {
      const m = (state.markers || []).find((x) => x.id === draggingMarkerId);
      if (m) {
        m.lat = e.lngLat.lat;
        m.lon = e.lngLat.lng;
        if (!dragRafPending) {
          dragRafPending = true;
          requestAnimationFrame(() => {
            dragRafPending = false;
            syncStructures(true);
          });
        }
      }
      return;
    }
    if (state.sketch !== "None" && sketchPoints.length > 0 && !sketchFinished) {
      renderSketch([e.lngLat.lng, e.lngLat.lat]);
    }
    if (boxStart) return;

    const layers = [];
    if (state.display === "Markers" && map.getLayer("sde-markers")) layers.push("sde-markers");
    if (state.display === "Clusters") {
      if (map.getLayer("sde-cluster-circles")) layers.push("sde-cluster-circles");
      if (map.getLayer("sde-cluster-unclustered")) layers.push("sde-cluster-unclustered");
    }
    const hits = layers.length ? map.queryRenderedFeatures(markerQueryBox(e.point), { layers }) : [];
    if (hits.length > 0) {
      const f = hits[0];
      map.getCanvas().style.cursor = state.mode === "SetLocation" ? "grab" : "pointer";
      if (f.properties.point_count) {
        hoverPopup.setLngLat(f.geometry.coordinates)
          .setHTML(`<div class="popup-address">${f.properties.point_count} structures</div><div class="popup-detail">Click to zoom in</div>`)
          .addTo(map);
      } else {
        hoverPopup.setLngLat(f.geometry.coordinates)
          .setHTML(`<div class="popup-address">${escapeHtml(f.properties.address || "(no address)")}</div><div class="popup-detail">${escapeHtml(f.properties.detail || "")}</div>`)
          .addTo(map);
      }
    } else {
      hoverPopup.remove();
      const isCross = state.mode === "SetLocation" || state.sketch !== "None";
      map.getCanvas().style.cursor = isCross ? "crosshair" : "";
    }
  });

  const mapEl = document.getElementById("map");
  const boxEl = document.getElementById("box-select");

  mapEl.addEventListener("mousedown", (ev) => {
    if (ev.button !== 0) return;
    const rect = mapEl.getBoundingClientRect();
    const pt = { x: ev.clientX - rect.left, y: ev.clientY - rect.top };

    if (state.mode === "SetLocation" && state.display === "Markers" && map.getLayer("sde-markers")) {
      const hits = map.queryRenderedFeatures(markerQueryBox(pt), { layers: ["sde-markers"] });
      if (hits.length > 0) {
        draggingMarkerId = hits[0].properties.id;
        map.dragPan.disable();
        map.getCanvas().style.cursor = "grabbing";
        ev.preventDefault();
        return;
      }
    }

    const shouldBox = (state.mode === "Select" && state.sketch === "None") || (ev.metaKey && state.mode === "Browse");
    if (shouldBox) {
      boxStart = pt;
      // Shift adds in any mode; in Select, ⌘ adds too (the hint and the user guide say ⌘). In Open, ⌘ only starts the box.
      boxAdditive = ev.shiftKey || (ev.metaKey && state.mode === "Select");
      map.dragPan.disable();
      boxEl.hidden = false;
      boxEl.style.left = `${pt.x}px`;
      boxEl.style.top = `${pt.y}px`;
      boxEl.style.width = "0px";
      boxEl.style.height = "0px";
      ev.preventDefault();
    }
  }, true);

  window.addEventListener("mousemove", (ev) => {
    if (!boxStart) return;
    const rect = mapEl.getBoundingClientRect();
    const x = Math.max(0, Math.min(rect.width, ev.clientX - rect.left));
    const y = Math.max(0, Math.min(rect.height, ev.clientY - rect.top));
    boxEl.style.left = `${Math.min(boxStart.x, x)}px`;
    boxEl.style.top = `${Math.min(boxStart.y, y)}px`;
    boxEl.style.width = `${Math.abs(x - boxStart.x)}px`;
    boxEl.style.height = `${Math.abs(y - boxStart.y)}px`;
  });

  window.addEventListener("mouseup", (ev) => {
    if (draggingMarkerId) {
      const id = draggingMarkerId;
      draggingMarkerId = null;
      suppressNextClick = true;
      setTimeout(() => { suppressNextClick = false; }, 0);
      map.dragPan.enable();
      const rect = mapEl.getBoundingClientRect();
      const ll = map.unproject([ev.clientX - rect.left, ev.clientY - rect.top]);
      post({ type: "markerDragged", id, lat: ll.lat, lon: ll.lng });
      return;
    }
    if (!boxStart) return;
    const rect = mapEl.getBoundingClientRect();
    const end = {
      x: Math.max(0, Math.min(rect.width, ev.clientX - rect.left)),
      y: Math.max(0, Math.min(rect.height, ev.clientY - rect.top)),
    };
    const start = boxStart;
    const additive = boxAdditive;
    boxStart = null;
    boxEl.hidden = true;
    map.dragPan.enable();

    if (Math.hypot(end.x - start.x, end.y - start.y) < 5) return;
    suppressNextClick = true;
    setTimeout(() => { suppressNextClick = false; }, 0);

    const bbox = [[Math.min(start.x, end.x), Math.min(start.y, end.y)], [Math.max(start.x, end.x), Math.max(start.y, end.y)]];
    if (state.selectLayer) {
      const layers = [`fl-${state.selectLayer}-fill`, `fl-${state.selectLayer}-line`, `fl-${state.selectLayer}-pts`].filter((id) => map.getLayer(id));
      const hits = layers.length ? map.queryRenderedFeatures(bbox, { layers }) : [];
      const ids = [...new Set(hits.map((h) => h.properties._id).filter((id) => id != null))];
      post({ type: "featuresSelected", layerKey: state.selectLayer, featureIds: ids, add: additive });
    } else {
      // Geographic bounds check works across Markers, Clusters, and Heatmap modes.
      const p1 = map.unproject(bbox[0]);
      const p2 = map.unproject(bbox[1]);
      const minLon = Math.min(p1.lng, p2.lng);
      const maxLon = Math.max(p1.lng, p2.lng);
      const minLat = Math.min(p1.lat, p2.lat);
      const maxLat = Math.max(p1.lat, p2.lat);
      const ids = (state.markers || [])
        .filter((m) => m.lon >= minLon && m.lon <= maxLon && m.lat >= minLat && m.lat <= maxLat)
        .map((m) => m.id);
      post({ type: "areaSelected", ids, add: additive });
    }
  });

  map.on("click", (e) => {
    if (suppressNextClick) {
      suppressNextClick = false;
      return;
    }
    if (state.sketch !== "None") {
      if (sketchFinished) {
        sketchPoints.length = 0;
        sketchFinished = false;
      }
      sketchPoints.push([e.lngLat.lng, e.lngLat.lat]);
      renderSketch();
      post({ type: "sketchChanged", points: sketchPoints.map(([lon, lat]) => ({ lat, lon })), finished: false });
      return;
    }

    if (state.display === "Clusters" && map.getLayer("sde-cluster-circles")) {
      const chits = map.queryRenderedFeatures(markerQueryBox(e.point), { layers: ["sde-cluster-circles"] });
      if (chits.length > 0) {
        const clusterId = chits[0].properties.cluster_id;
        map.getSource("sde-clusters").getClusterExpansionZoom(clusterId).then((zoom) => {
          map.easeTo({ center: chits[0].geometry.coordinates, zoom: Math.min(18, zoom + 0.5), duration: dur(240) });
        }).catch(() => {});
        return;
      }
    }

    const markerLayers = [];
    if (state.display === "Markers" && map.getLayer("sde-markers")) markerLayers.push("sde-markers");
    if (state.display === "Clusters" && map.getLayer("sde-cluster-unclustered")) markerLayers.push("sde-cluster-unclustered");
    if (markerLayers.length > 0) {
      const mhits = map.queryRenderedFeatures(markerQueryBox(e.point), { layers: markerLayers });
      if (mhits.length > 0) {
        post({ type: "markerClicked", id: mhits[0].properties.id, add: !!(e.originalEvent && (e.originalEvent.shiftKey || e.originalEvent.metaKey)) });
        return;
      }
    }

    const featureLayerIds = [];
    for (let i = activeFeatureLayerKeys.length - 1; i >= 0; i--) {
      const k = activeFeatureLayerKeys[i];
      for (const suffix of ["-pts", "-line", "-fill"]) {
        const id = `fl-${k}${suffix}`;
        if (map.getLayer(id)) featureLayerIds.push(id);
      }
    }
    if (featureLayerIds.length > 0 && state.mode !== "SetLocation") {
      const fhits = map.queryRenderedFeatures(markerQueryBox(e.point), { layers: featureLayerIds });
      if (fhits.length > 0) {
        const lid = fhits[0].layer.id;
        const layerKey = lid.replace(/^fl-/, "").replace(/-(pts|line|fill)$/, "");
        const featureId = fhits[0].properties._id;
        if (featureId != null) {
          post({ type: "featureClicked", layerKey, featureId, add: !!(e.originalEvent && (e.originalEvent.shiftKey || e.originalEvent.metaKey)) });
          return;
        }
      }
    }

    post({ type: "mapClicked", lat: e.lngLat.lat, lon: e.lngLat.lng });
  });

  map.on("dblclick", (e) => {
    if (state.sketch !== "None" && sketchPoints.length >= 2) {
      e.preventDefault();
      // Remove duplicate final vertex appended by the second click of a double-click.
      while (sketchPoints.length >= 3) {
        const a = sketchPoints[sketchPoints.length - 1];
        const b = sketchPoints[sketchPoints.length - 2];
        if (Math.hypot(a[0] - b[0], a[1] - b[1]) < 1e-6) {
          sketchPoints.pop();
        } else {
          break;
        }
      }
      sketchFinished = true;
      renderSketch();
      post({ type: "sketchChanged", points: sketchPoints.map(([lon, lat]) => ({ lat, lon })), finished: true });
    }
  });

  map.on("moveend", () => {
    reportView();
    syncClusterLabels();
  });
  map.on("render", syncClusterLabels);
  map.on("rotate", () => {
    document.getElementById("reset-north").hidden = Math.abs(map.getBearing()) < 0.5;
  });

  const postSketch = (finished) =>
    post({ type: "sketchChanged", points: sketchPoints.map(([lon, lat]) => ({ lat, lon })), finished });

  // One zoom step per press. MapLibre's keyboard handler also zooms on = + − while the map has focus, which zoomed
  // twice; taking the keys here (capture phase, before the map) gives one step wherever the focus is.
  window.addEventListener("keydown", (e) => {
    if (e.metaKey || e.ctrlKey || e.altKey || !(e.key === "=" || e.key === "+" || e.key === "-")) return;
    e.stopPropagation();
    e.preventDefault();
    if (e.key === "-") map.zoomOut({ duration: dur(150) });
    else map.zoomIn({ duration: dur(150) });
  }, true);

  window.addEventListener("keydown", (e) => {
    if (e.metaKey) {
      const k = e.key.toLowerCase();
      if (k === "k" || k === "s" || k === "1" || k === "2" || k === "3" || k === "n") {
        e.preventDefault();
        post({ type: "shortcut", key: k, shift: e.shiftKey, alt: e.altKey, ctrl: e.ctrlKey });
      }
      return;
    }
    if (e.key === "Escape") {
      if (sketchPoints.length > 0) {
        sketchPoints.length = 0;
        sketchFinished = false;
        renderSketch();
        post({ type: "clearMeasurement" });
      } else {
        post({ type: "shortcut", key: "escape" });
      }
    } else if (e.key === "0") {
      post({ type: "fitAll" });
    } else if (e.key === "Enter" && state.sketch !== "None" && sketchPoints.length >= 2) {
      sketchFinished = true;
      renderSketch();
      postSketch(true);
    } else if ((e.key === "Backspace" || e.key === "Delete") && state.sketch !== "None" && sketchPoints.length > 0) {
      // As in the Classic map: Delete takes back the last point (the Measure hint promises it).
      e.preventDefault();
      sketchPoints.pop();
      sketchFinished = false;
      renderSketch();
      postSketch(false);
    }
  });

  const escapeHtml = (str) =>
    String(str).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

  window.sdeMap = {
    applyState(next) {
      const prevSketch = state.sketch;
      const draggedPos = draggingMarkerId ? (state.markers || []).find((m) => m.id === draggingMarkerId) : null;
      state = Object.assign({}, state, next);
      if (draggedPos && state.markers) {
        const target = state.markers.find((m) => m.id === draggingMarkerId);
        if (target) {
          target.lat = draggedPos.lat;
          target.lon = draggedPos.lon;
        }
      }
      if (prevSketch !== state.sketch) {
        sketchPoints.length = 0;
        sketchFinished = false;
      }
      if (!mapReady) return;
      syncTheme();
      syncBasemap();
      syncTileOverlays();
      syncFeatureLayers();
      syncRasters();
      syncStructures();
      renderSketch();
      renderHud();
    },

    fitBounds(minLon, minLat, maxLon, maxLat, maxZoom = 17, padding = 48) {
      if (!mapReady || !Number.isFinite(minLon) || !Number.isFinite(maxLon) || minLon > maxLon || minLat > maxLat) return;
      if (Math.abs(maxLon - minLon) < 1e-6 && Math.abs(maxLat - minLat) < 1e-6) {
        map.easeTo({ center: [minLon, minLat], zoom: Math.min(maxZoom, 16), duration: dur(240) });
      } else {
        map.fitBounds([[minLon, minLat], [maxLon, maxLat]], { padding, maxZoom, duration: dur(240) });
      }
    },

    centerOn(lat, lon, zoom) {
      if (!mapReady) return;
      const opts = { center: [lon, lat], duration: dur(240) };
      if (typeof zoom === "number" && Number.isFinite(zoom)) opts.zoom = zoom;
      else if (map.getZoom() < 15) opts.zoom = 16;
      map.easeTo(opts);
    },

    setViewImmediate(lat, lon, zoom) {
      if (!mapReady) return;
      map.jumpTo({ center: [lon, lat], zoom });
    },

    clearSketch() {
      sketchPoints.length = 0;
      sketchFinished = false;
      renderSketch();
    },

    retryTiles() {
      if (!mapReady) return;
      activeBasemapKey = null;
      syncBasemap();
      for (const id of activeTileOverlayKeys) {
        if (map.getLayer(id)) map.removeLayer(id);
        if (map.getSource(id)) map.removeSource(id);
      }
      activeTileOverlayKeys = [];
      syncTileOverlays();
    },

    captureSnapshot() {
      let dataUrl = null;
      if (mapReady) {
        try {
          dataUrl = map.getCanvas().toDataURL("image/png");
        } catch (_) {
          dataUrl = null;
        }
      }
      post({ type: "snapshot", dataUrl });
    },
  };

  map.on("load", async () => {
    // A glyph drawn before the font arrives is drawn in a fallback font and kept, so wait for it (it is local).
    try {
      await document.fonts.load(`16px ${LABEL_FONT}`);
    } catch (_) {
      // Labels fall back to the system font.
    }
    mapReady = true;
    ensureCoreSourcesAndLayers();
    syncTheme();
    syncBasemap();
    syncTileOverlays();
    syncFeatureLayers();
    syncRasters();
    syncStructures();
    renderHud();
    post({ type: "ready" });
  });
})();
