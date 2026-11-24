using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Sde.App.Controls;
using Sde.App.Maps;
using Sde.App.ViewModels;
using Sde.Core.Assessments;
using Sde.Gis.Features;

namespace Sde.App.Views;

/// <summary>
/// Option B hybrid map bridge: hosts MapLibre GL JS inside a native macOS <c>WKWebView</c> (<see cref="MacWebView"/>)
/// backed by a loopback-only <see cref="MapWebServer"/>, while keeping <see cref="MapCanvas"/> available for Classic
/// mode, headless tests, vertex editing, and off-screen 200-DPI PDF printing.
/// </summary>
public partial class MapView
{
	private static readonly JsonSerializerOptions WebJson = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

	private MapWebServer _webServer;
	private Task _webServerTask;
	private bool _webSupported;
	private bool _preferWebMap = true;
	private bool _webJsReady;
	private bool _webPushQueued;
	private int _rasterRevision;
	private List<(string Key, byte[] Png)> _lastRasterRefs = new();
	private int _openFlyouts;
	private MainWindow _hostWindow;
	private MainViewModel _hostMain;
	private Bitmap _backdropBitmap;
	private bool _snapshotInFlight;

	/// <summary>
	/// True when the native MapLibre WKWebView should be active on screen (real macOS window, user hasn't switched
	/// back to Classic Mapsui, and not currently in Edit mode where MapCanvas's vertex editor runs).
	/// </summary>
	private bool IsWebMapActive => _webSupported && _preferWebMap && (_vm == null || !_vm.IsEdit);

	private void InitWebMapHooks()
	{
		WebMap.MessageReceived += OnWebMessage;
		AttachedToVisualTree += OnMapAttachedToVisualTree;
		if (BookmarksButton.Flyout is FlyoutBase bf)
		{
			bf.Opened += OnToolbarFlyoutOpened;
			bf.Closed += OnToolbarFlyoutClosed;
		}
		if (MoreButton.Flyout is FlyoutBase mf)
		{
			mf.Opened += OnToolbarFlyoutOpened;
			mf.Closed += OnToolbarFlyoutClosed;
		}
	}

	private void OnMapAttachedToVisualTree(object sender, VisualTreeAttachmentEventArgs e)
	{
		TopLevel top = TopLevel.GetTopLevel(this);
		_webSupported = MacWebView.IsSupportedFor(top);
		MapEngineItem.IsVisible = _webSupported;
		if (!_webSupported)
		{
			return;
		}

		AttachAirspaceWatchers(top as MainWindow);
		UpdateWebMapModeVisuals();
		if (IsWebMapActive)
		{
			_ = EnsureWebServerAndLoadAsync();
		}
	}

	private void OnToggleMapEngine(object sender, RoutedEventArgs e)
	{
		if (!_webSupported)
		{
			return;
		}
		_preferWebMap = !_preferWebMap;
		UpdateWebMapModeVisuals();
		if (IsWebMapActive)
		{
			_ = EnsureWebServerAndLoadAsync();
			QueueWebPush();
			SyncWebMapViewportFromSurface();
		}
	}

	private void UpdateWebMapModeVisuals()
	{
		bool active = IsWebMapActive;
		MapEngineItem.Icon = _preferWebMap ? new Icon { Kind = "Check", Size = 12 } : null;
		WebMap.IsVisible = active;
		ClassicMapFloats.IsVisible = !active;
		ClassicAttributionHost.IsVisible = !active;
		if (!active)
		{
			WebMapBackdrop.IsVisible = false;
			WebMap.SetNativeHidden(true);
		}
		else
		{
			UpdateAirspaceOcclusion();
			QueueWebPush();
		}
	}

	private Task EnsureWebServerAndLoadAsync()
	{
		if (_webServer != null)
		{
			_webServer.Tiles = _vm?.Tiles;
			return Task.CompletedTask;
		}
		_webServerTask ??= StartWebServerCoreAsync();
		return _webServerTask;
	}

	private async Task StartWebServerCoreAsync()
	{
		try
		{
			MapWebServer server = await MapWebServer.StartAsync();
			if (_vm == null && !IsLoaded)
			{
				await server.DisposeAsync();
				return;
			}
			_webServer = server;
			_webServer.Tiles = _vm?.Tiles;
			WebMap.Navigate(_webServer.EntryUrl);
		}
		catch (Exception ex)
		{
			Services.AppLog.Write("MapWebServer failed to start; falling back to Classic map", ex);
			_preferWebMap = false;
			UpdateWebMapModeVisuals();
		}
		finally
		{
			if (_webServer == null)
			{
				_webServerTask = null;
			}
		}
	}

	private void ShutdownWebMap()
	{
		DetachAirspaceWatchers();
		_webJsReady = false;
		_snapshotInFlight = false;
		_webServerTask = null;
		_lastRasterRefs.Clear();
		WebMapBackdrop.Source = null;
		_backdropBitmap?.Dispose();
		_backdropBitmap = null;
		MapWebServer server = _webServer;
		_webServer = null;
		if (server != null)
		{
			_ = server.DisposeAsync();
		}
	}

	// ───────────── Airspace handling (hiding WKWebView cleanly behind Avalonia modals/flyouts) ─────────────

	private void AttachAirspaceWatchers(MainWindow window)
	{
		DetachAirspaceWatchers();
		_hostWindow = window;
		if (_hostWindow == null)
		{
			return;
		}
		_hostMain = _hostWindow.DataContext as MainViewModel;
		if (_hostMain != null)
		{
			_hostMain.Sheets.PropertyChanged += OnOverlayStateChanged;
			_hostMain.Palette.PropertyChanged += OnOverlayStateChanged;
			_hostMain.Jobs.PropertyChanged += OnOverlayStateChanged;
		}
		_hostWindow.Toasts.Items.CollectionChanged += OnToastsChanged;
		_hostWindow.DropOverlayView.PropertyChanged += OnDropOverlayPropertyChanged;
		if (_hostWindow.FindControl<DropDownButton>("NewButton")?.Flyout is FlyoutBase nf)
		{
			nf.Opened += OnToolbarFlyoutOpened;
			nf.Closed += OnToolbarFlyoutClosed;
		}
	}

	private void DetachAirspaceWatchers()
	{
		if (_hostMain != null)
		{
			_hostMain.Sheets.PropertyChanged -= OnOverlayStateChanged;
			_hostMain.Palette.PropertyChanged -= OnOverlayStateChanged;
			_hostMain.Jobs.PropertyChanged -= OnOverlayStateChanged;
			_hostMain = null;
		}
		if (_hostWindow != null)
		{
			_hostWindow.Toasts.Items.CollectionChanged -= OnToastsChanged;
			_hostWindow.DropOverlayView.PropertyChanged -= OnDropOverlayPropertyChanged;
			if (_hostWindow.FindControl<DropDownButton>("NewButton")?.Flyout is FlyoutBase nf)
			{
				nf.Opened -= OnToolbarFlyoutOpened;
				nf.Closed -= OnToolbarFlyoutClosed;
			}
			_hostWindow = null;
		}
		_openFlyouts = 0;
	}

	private void OnOverlayStateChanged(object sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(Services.SheetHost.HasSheet) or nameof(ViewModels.Shell.CommandPaletteViewModel.IsOpen) or nameof(Services.JobRunner.IsBusy))
		{
			UpdateAirspaceOcclusion();
		}
	}

	private void OnToastsChanged(object sender, NotifyCollectionChangedEventArgs e) => UpdateAirspaceOcclusion();

	private void OnDropOverlayPropertyChanged(object sender, AvaloniaPropertyChangedEventArgs e)
	{
		if (e.Property == IsVisibleProperty)
		{
			UpdateAirspaceOcclusion();
		}
	}

	private void OnToolbarFlyoutOpened(object sender, EventArgs e)
	{
		_openFlyouts++;
		UpdateAirspaceOcclusion();
	}

	private void OnToolbarFlyoutClosed(object sender, EventArgs e)
	{
		_openFlyouts = Math.Max(0, _openFlyouts - 1);
		UpdateAirspaceOcclusion();
	}

	private void UpdateAirspaceOcclusion()
	{
		if (!IsWebMapActive)
		{
			return;
		}
		bool occluded = _openFlyouts > 0
			|| (_hostMain?.Sheets.HasSheet ?? false)
			|| (_hostMain?.Palette.IsOpen ?? false)
			|| (_hostMain?.Jobs.IsBusy ?? false)
			|| (_hostWindow?.Toasts.Items.Count ?? 0) > 0
			|| (_hostWindow?.DropOverlayView.IsVisible ?? false);

		WebMap.SetNativeHidden(occluded);
		WebMapBackdrop.IsVisible = occluded && _backdropBitmap != null;
	}

	private void RequestSnapshot()
	{
		if (!IsWebMapActive || !_webJsReady || _snapshotInFlight)
		{
			return;
		}
		_snapshotInFlight = true;
		WebMap.ExecuteScript("window.sdeMap && window.sdeMap.captureSnapshot();");
	}

	// ───────────── C# → JS State Sync ─────────────

	private void QueueWebPush()
	{
		if (!IsWebMapActive || !_webJsReady || _vm == null || _webPushQueued)
		{
			return;
		}
		_webPushQueued = true;
		Dispatcher.UIThread.Post(PushStateToWebMap, DispatcherPriority.Render);
	}

	private void PushStateToWebMap()
	{
		_webPushQueued = false;
		if (!IsWebMapActive || !_webJsReady || _vm == null || _webServer == null)
		{
			return;
		}

		_webServer.Tiles = _vm.Tiles;
		_webServer.RegisterTileSource(_vm.Basemap);

		IReadOnlyList<object> mapLayers = _vm.MapLayers();
		List<MapTileLayer> tileLayers = mapLayers.OfType<MapTileLayer>().ToList();
		List<MapFeatureLayer> featureLayers = mapLayers.OfType<MapFeatureLayer>().ToList();
		foreach (MapTileLayer tl in tileLayers)
		{
			_webServer.RegisterTileSource(tl.Source);
		}
		_webServer.SetFeatureLayers(featureLayers);

		// Expose non-heatmap rasters (e.g. water surface) as image overlays; heatmap itself runs natively in MapLibre WebGL.
		List<MapRasterOverlay> rasters = Surface.Rasters
			.Where(r => r.Key != MapAnalysis.HeatKey && r.Png is { Length: > 0 })
			.ToList();
		if (RastersChanged(rasters))
		{
			_webServer.SetRasters(rasters);
			_rasterRevision++;
		}

		(List<MapMarker> builtMarkers, _) = BuildMarkers(ActualThemeVariant);
		Dictionary<Guid, MapPointViewModel> pointById = new(_vm.Visible.Count);
		foreach (MapPointViewModel p in _vm.Visible)
		{
			pointById[p.Id] = p;
		}
		List<object> markersPayload = new(builtMarkers.Count);
		foreach (MapMarker m in builtMarkers)
		{
			pointById.TryGetValue(m.Id, out MapPointViewModel pt);
			markersPayload.Add(new
			{
				id = m.Id.ToString(),
				lat = m.Latitude,
				lon = m.Longitude,
				color = CssColor(m.Fill),
				label = m.Label ?? "",
				address = pt?.Address ?? "",
				detail = pt?.Detail ?? "",
				severity = pt == null ? 0 : SeverityOf(pt.Class),
				percentDamage = pt?.Row.PercentDamage ?? 0m,
			});
		}

		List<object> legendRows = new();
		if (_vm.ShowsResultLegend)
		{
			foreach (MapLegendLine line in _vm.ResultLegend)
			{
				legendRows.Add(new
				{
					label = line.Label,
					color = CssColor(ThemeBrushFor(line.ColourKey, ActualThemeVariant)),
					count = line.Count,
				});
			}
		}
		else if (_vm.IsMarkers && _vm.HasStructureLegend)
		{
			foreach (LegendLineViewModel line in _vm.StructureLegend)
			{
				legendRows.Add(new
				{
					label = line.Label,
					color = CssColor(line.Fill),
					count = (string)null,
				});
			}
		}

		List<object> surfaceRows = new();
		if (_vm.ShowsSurface && _vm.HasSurfaceLegend)
		{
			foreach (MapLegendLine line in _vm.SurfaceLegend)
			{
				surfaceRows.Add(new
				{
					label = line.Label,
					color = CssColor(ThemeBrushFor(line.ColourKey, ActualThemeVariant)),
				});
			}
		}

		object payload = new
		{
			theme = ActualThemeVariant == ThemeVariant.Dark ? "dark" : "light",
			accent = CssColor(ThemeBrushFor("AccentBg", ActualThemeVariant)),
			basemap = new
			{
				id = _vm.Basemap.Id,
				maxZoom = _vm.Basemap.MaxZoom,
				hasTiles = _vm.Basemap.HasTiles,
				retina = _vm.Basemap.HasRetinaTiles,
				attribution = _vm.Basemap.Attribution ?? "",
			},
			display = _vm.Display.ToString(),
			heatByDamage = _vm.HeatByDamage,
			mode = _vm.Mode.ToString(),
			sketch = _vm.Sketch.ToString(),
			selectLayer = _vm.IsSelect ? _vm.SelectLayerKey : null,
			selectableLayers = _vm.SelectableLayers.Select(l => new { key = l.IsStructures ? null : l.Key, name = l.Name }).ToArray(),
			placeChoices = _vm.PlaceChoices.Select(p => new { id = p.Id.ToString(), address = p.Address }).ToArray(),
			placeTargetId = _vm.PlaceTarget?.Id.ToString(),
			measureArea = _vm.MeasureArea,
			measurementLine = _vm.MeasurementLine ?? "",
			hint = _vm.Hint ?? "",
			legendTitle = _vm.LegendTitle ?? "Structures",
			countText = _vm.CountText ?? "",
			legendOpen = _vm.IsLegendOpen,
			legendRows,
			heatLegendNote = _vm.HeatLegendNote ?? "",
			surfaceLegendTitle = _vm.SurfaceLegendTitle ?? "",
			surfaceLegendRows = surfaceRows,
			hasChecked = _vm.HasChecked,
			selectionText = _vm.SelectionText ?? "",
			hasLayerSelection = _vm.HasLayerSelection,
			layerSelectionText = _vm.LayerSelectionText ?? "",
			statusMessage = _vm.StatusMessage ?? "",
			attribution = _vm.Attribution ?? "",
			onMapCount = _vm.OnMapCount,
			markers = markersPayload,
			selectedIds = _vm.SelectedStructures().Select(id => id.ToString()).ToArray(),
			highlightedId = _vm.Selected?.Id.ToString(),
			tileOverlays = tileLayers.Select(tl => new
			{
				key = tl.Key,
				sourceId = tl.Source.Id,
				maxZoom = tl.Source.MaxZoom,
				retina = tl.Source.HasRetinaTiles,
				visible = tl.Visible,
				opacity = tl.Display?.Opacity ?? 0.75,
			}).ToArray(),
			featureLayers = featureLayers.Select(fl => new
			{
				key = fl.Key,
				visible = fl.Visible,
				opacity = fl.Display?.Opacity ?? 1.0,
				revision = fl.Revision + (fl.Edits?.Revision ?? 0) * 100003,
				selectionRevision = fl.SelectionRevision,
			}).ToArray(),
			rasters = rasters.Select(r =>
			{
				(double minLon, double minLat) = WebMercator.FromMetres(r.MinX, r.MinY);
				(double maxLon, double maxLat) = WebMercator.FromMetres(r.MaxX, r.MaxY);
				return new
				{
					key = r.Key,
					minLon,
					minLat,
					maxLon,
					maxLat,
					opacity = r.Opacity,
					rev = _rasterRevision,
				};
			}).ToArray(),
		};

		string json = JsonSerializer.Serialize(payload, WebJson);
		WebMap.ExecuteScript($"window.sdeMap && window.sdeMap.applyState({json});");
		RequestSnapshot();
	}

	private bool RastersChanged(List<MapRasterOverlay> rasters)
	{
		if (rasters.Count == _lastRasterRefs.Count)
		{
			bool same = true;
			for (int i = 0; i < rasters.Count; i++)
			{
				if (rasters[i].Key != _lastRasterRefs[i].Key || !ReferenceEquals(rasters[i].Png, _lastRasterRefs[i].Png))
				{
					same = false;
					break;
				}
			}
			if (same)
			{
				return false;
			}
		}
		_lastRasterRefs = rasters.Select(r => (r.Key, r.Png)).ToList();
		return true;
	}

	private void WebFitToMarkers()
	{
		if (!IsWebMapActive || !_webJsReady || _vm == null)
		{
			return;
		}
		List<MapPointViewModel> pts = _vm.Visible.Where(p => p.HasLocation).ToList();
		if (pts.Count == 0)
		{
			return;
		}
		double minLon = pts.Min(p => p.Longitude);
		double maxLon = pts.Max(p => p.Longitude);
		double minLat = pts.Min(p => p.Latitude);
		double maxLat = pts.Max(p => p.Latitude);
		WebFitExtent(new MapExtent(minLon, minLat, maxLon, maxLat), 17, 56);
	}

	private void WebFitExtent(MapExtent extent, double maxZoom = 18, int padding = 48)
	{
		if (!IsWebMapActive || !_webJsReady || extent.IsEmpty && extent.MinLatitude == 0 && extent.MinLongitude == 0)
		{
			return;
		}
		string script = string.Create(
			System.Globalization.CultureInfo.InvariantCulture,
			$"window.sdeMap && window.sdeMap.fitBounds({extent.MinLongitude:R},{extent.MinLatitude:R},{extent.MaxLongitude:R},{extent.MaxLatitude:R},{maxZoom:R},{padding});");
		WebMap.ExecuteScript(script);
	}

	private void WebCenterOn(double lat, double lon)
	{
		if (!IsWebMapActive || !_webJsReady)
		{
			return;
		}
		string script = string.Create(
			System.Globalization.CultureInfo.InvariantCulture,
			$"window.sdeMap && window.sdeMap.centerOn({lat:R},{lon:R});");
		WebMap.ExecuteScript(script);
	}

	private void SyncWebMapViewportFromSurface()
	{
		if (!IsWebMapActive || !_webJsReady)
		{
			return;
		}
		string script = string.Create(
			System.Globalization.CultureInfo.InvariantCulture,
			$"window.sdeMap && window.sdeMap.setViewImmediate({Surface.CenterLatitude:R},{Surface.CenterLongitude:R},{Surface.Zoom:R});");
		WebMap.ExecuteScript(script);
	}

	private void WebClearSketch()
	{
		if (IsWebMapActive && _webJsReady)
		{
			WebMap.ExecuteScript("window.sdeMap && window.sdeMap.clearSketch();");
		}
	}

	private void WebRetryTiles()
	{
		if (IsWebMapActive && _webJsReady)
		{
			WebMap.ExecuteScript("window.sdeMap && window.sdeMap.retryTiles();");
		}
	}

	// ───────────── JS → C# Message Handling ─────────────

	private void OnWebMessage(string json)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return;
		}
		try
		{
			using JsonDocument doc = JsonDocument.Parse(json);
			JsonElement root = doc.RootElement;
			string type = root.TryGetProperty("type", out JsonElement t) ? t.GetString() : null;
			switch (type)
			{
				case "ready":
					_webJsReady = true;
					_snapshotInFlight = false;
					PushStateToWebMap();
					if (_vm != null && _vm.Visible.Any(p => p.HasLocation))
					{
						WebFitToMarkers();
					}
					break;

				case "viewChanged":
					if (_vm != null)
					{
						double cLat = root.GetProperty("centerLat").GetDouble();
						double cLon = root.GetProperty("centerLon").GetDouble();
						double zoom = root.GetProperty("zoom").GetDouble();
						double minLon = root.GetProperty("minLon").GetDouble();
						double minLat = root.GetProperty("minLat").GetDouble();
						double maxLon = root.GetProperty("maxLon").GetDouble();
						double maxLat = root.GetProperty("maxLat").GetDouble();
						_vm.ViewExtent = new MapExtent(minLon, minLat, maxLon, maxLat);
						// Keep hidden MapCanvas synced so Print Map… (200-DPI MapPrinter) and Edit mode match exactly.
						Surface.SetView(cLat, cLon, zoom);
						_analysis.ViewChanged();
						RequestSnapshot();
					}
					break;

				case "markerClicked":
					if (_vm != null && Guid.TryParse(root.GetProperty("id").GetString(), out Guid mid))
					{
						bool add = root.TryGetProperty("add", out JsonElement a) && a.GetBoolean();
						_vm.ClickStructure(mid, add);
					}
					break;

				case "featureClicked":
					if (_vm != null)
					{
						string layerKey = root.GetProperty("layerKey").GetString();
						long fid = root.GetProperty("featureId").GetInt64();
						bool add = root.TryGetProperty("add", out JsonElement a) && a.GetBoolean();
						Feature feat = FindFeature(layerKey, fid);
						if (feat != null)
						{
							_vm.ClickFeature(layerKey, feat, add);
						}
					}
					break;

				case "mapClicked":
					if (_vm != null)
					{
						_vm.ClickMap(root.GetProperty("lat").GetDouble(), root.GetProperty("lon").GetDouble());
					}
					break;

				case "areaSelected":
					if (_vm != null)
					{
						List<Guid> ids = new();
						foreach (JsonElement el in root.GetProperty("ids").EnumerateArray())
						{
							if (Guid.TryParse(el.GetString(), out Guid id))
							{
								ids.Add(id);
							}
						}
						bool add = root.TryGetProperty("add", out JsonElement a) && a.GetBoolean();
						_vm.SelectArea(ids, add);
					}
					break;

				case "featuresSelected":
					if (_vm != null)
					{
						string layerKey = root.GetProperty("layerKey").GetString();
						HashSet<long> fids = new();
						foreach (JsonElement el in root.GetProperty("featureIds").EnumerateArray())
						{
							fids.Add(el.GetInt64());
						}
						bool add = root.TryGetProperty("add", out JsonElement a) && a.GetBoolean();
						List<Feature> matches = FindFeatures(layerKey, fids);
						_vm.SelectFeatures(layerKey, matches, add);
					}
					break;

				case "markerDragged":
					if (_vm != null && Guid.TryParse(root.GetProperty("id").GetString(), out Guid dragId))
					{
						LatLon to = new(root.GetProperty("lat").GetDouble(), root.GetProperty("lon").GetDouble());
						_ = _vm.DragStructureAsync(dragId, to);
					}
					break;

				case "sketchChanged":
					if (_vm != null)
					{
						List<LatLon> pts = new();
						foreach (JsonElement el in root.GetProperty("points").EnumerateArray())
						{
							pts.Add(new LatLon(el.GetProperty("lat").GetDouble(), el.GetProperty("lon").GetDouble()));
						}
						bool finished = root.TryGetProperty("finished", out JsonElement f) && f.GetBoolean();
						_vm.SketchChanged(pts, finished);
						QueueWebPush();
					}
					break;

				case "setMode":
					if (_vm != null && Enum.TryParse(root.GetProperty("mode").GetString(), out MapMode mode))
					{
						_vm.SetModeCommand.Execute(mode);
					}
					break;

				case "togglePolygonSelect":
					if (_vm != null)
					{
						_vm.IsPolygonSelect = !_vm.IsPolygonSelect;
					}
					break;

				case "setSelectTarget":
					if (_vm != null)
					{
						string key = root.TryGetProperty("key", out JsonElement k) ? k.GetString() : null;
						_vm.SelectTarget = string.IsNullOrEmpty(key)
							? _vm.StructuresLayer
							: _vm.SelectableLayers.FirstOrDefault(l => l.Key == key) ?? _vm.StructuresLayer;
					}
					break;

				case "selectByAttribute":
					_vm?.SelectByAttributeCommand.Execute(null);
					break;

				case "selectByLocation":
					_vm?.SelectByLocationCommand.Execute(null);
					break;

				case "setPlaceTarget":
					if (_vm != null && Guid.TryParse(root.GetProperty("id").GetString(), out Guid placeId))
					{
						_vm.PlaceTarget = _vm.PlaceChoices.FirstOrDefault(p => p.Id == placeId);
					}
					break;

				case "setFieldOnSelection":
					_vm?.SetFieldOnSelectionCommand.Execute(null);
					break;

				case "assignSelection":
					_vm?.AssignSelectionCommand.Execute(null);
					break;

				case "copyValues":
					_vm?.CopyValuesCommand.Execute(null);
					break;

				case "setMeasureArea":
					if (_vm != null)
					{
						_vm.MeasureArea = root.GetProperty("area").GetBoolean();
					}
					break;

				case "clearMeasurement":
					_vm?.ClearMeasurementCommand.Execute(null);
					break;

				case "toggleLegend":
					_vm?.ToggleLegendCommand.Execute(null);
					break;

				case "showChecked":
					_vm?.ShowCheckedCommand.Execute(null);
					break;

				case "clearChecked":
					_vm?.ClearCheckedCommand.Execute(null);
					break;

				case "showSelectionInTable":
					_vm?.ShowSelectionInTableCommand.Execute(null);
					break;

				case "clearLayerSelection":
					_vm?.ClearSelectionCommand.Execute(null);
					break;

				case "fitAll":
					_vm?.FitToStructuresCommand.Execute(null);
					break;

				case "shortcut":
					HandleForwardedShortcut(root);
					break;

				case "snapshot":
					_snapshotInFlight = false;
					ApplyBackdropSnapshot(root.TryGetProperty("dataUrl", out JsonElement u) ? u.GetString() : null);
					break;
			}
		}
		catch (Exception ex)
		{
			Services.AppLog.Write("MapWebView message handling failed", ex);
		}
	}

	private void HandleForwardedShortcut(JsonElement root)
	{
		if (_hostMain == null)
		{
			return;
		}
		string key = root.TryGetProperty("key", out JsonElement k) ? k.GetString() : null;
		bool shift = root.TryGetProperty("shift", out JsonElement s) && s.GetBoolean();
		switch (key)
		{
			case "k":
				_hostMain.Palette.OpenCommand.Execute(null);
				break;
			case "1":
				_hostMain.LibraryView = 0;
				break;
			case "2":
				_hostMain.LibraryView = 1;
				break;
			case "3":
				_hostMain.LibraryView = 2;
				break;
			case "s":
				_hostMain.SaveCommand.Execute(null);
				break;
			case "n":
				_hostMain.Structures.NewStructureCommand.Execute(shift ? "2" : "1");
				break;
			case "escape":
				_hostMain.EscapeCommand.Execute(null);
				break;
		}
	}

	private void ApplyBackdropSnapshot(string dataUrl)
	{
		const string prefix = "data:image/png;base64,";
		if (string.IsNullOrEmpty(dataUrl) || !dataUrl.StartsWith(prefix, StringComparison.Ordinal))
		{
			return;
		}
		try
		{
			byte[] bytes = Convert.FromBase64String(dataUrl[prefix.Length..]);
			using MemoryStream ms = new(bytes);
			Bitmap next = new(ms);
			Bitmap old = _backdropBitmap;
			_backdropBitmap = next;
			WebMapBackdrop.Source = next;
			old?.Dispose();
		}
		catch
		{
			// Best effort backdrop cache.
		}
	}

	private Feature FindFeature(string layerKey, long id)
	{
		MapFeatureLayer layer = _vm?.MapLayers().OfType<MapFeatureLayer>().FirstOrDefault(l => l.Key == layerKey);
		if (layer?.Data == null)
		{
			return null;
		}
		return layer.Data.Features.FirstOrDefault(f => f.Id == id)
			?? layer.Edits?.Drawn?.FirstOrDefault(f => f.Id == id);
	}

	private List<Feature> FindFeatures(string layerKey, HashSet<long> ids)
	{
		MapFeatureLayer layer = _vm?.MapLayers().OfType<MapFeatureLayer>().FirstOrDefault(l => l.Key == layerKey);
		if (layer?.Data == null || ids.Count == 0)
		{
			return new List<Feature>();
		}
		return layer.Data.Features.Where(f => ids.Contains(f.Id)).ToList();
	}

	private static int SeverityOf(DeterminationClass c) => c switch
	{
		DeterminationClass.Substantial => 4,
		DeterminationClass.Close => 3,
		DeterminationClass.NotSubstantial => 2,
		DeterminationClass.Undetermined => 1,
		_ => 0,
	};

	private static string CssColor(IBrush brush)
	{
		Color c = brush is ISolidColorBrush s ? s.Color : Colors.Gray;
		return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
	}
}
