using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Sde.App.Maps;
using Sde.App.ViewModels;
using Sde.Reports;

namespace Sde.App.Views;

/// <summary>
/// The Map page. The <see cref="MapCanvas"/> draws and reports clicks; this class hands it the dots and layers, turns
/// its events into view-model calls, and keeps the two in step (basemap, mode, the structure picked in the list, the
/// area in view). When the page closes, the map engine is stopped.
/// </summary>
public partial class MapView : UserControl
{
	private MapViewModel _vm;
	private bool _fitted;
	private readonly MapAnalysisRunner _analysis;

	public MapView()
	{
		InitializeComponent();
		_analysis = new MapAnalysisRunner(Surface, key => this.TryFindResource(key, ActualThemeVariant, out object r) ? r : null);
		_analysis.SurfaceChanged += result => _vm?.ShowSurfaceClasses(result);
		Surface.ClusterClicked += extent => Surface.FitTo(extent, 18);
		Surface.MarkerClicked += (id, add) => _vm?.ClickStructure(id, add);
		Surface.FeatureClicked += (layer, feature, add) => _vm?.ClickFeature(layer, feature, add);
		Surface.MapClicked += (lat, lon) => _vm?.ClickMap(lat, lon);
		Surface.AreaSelected += (ids, add) => _vm?.SelectArea(ids.ToList(), add);
		Surface.FeaturesSelected += (layer, features, add) => _vm?.SelectFeatures(layer, features, add);
		Surface.SketchChanged += (points, finished) => _vm?.SketchChanged(points, finished);
		Surface.EditFeaturePicked += feature => _vm?.PickEditFeature(feature);
		Surface.EditShapeChanged += (shape, what) => _vm?.ChangeEditShape(shape, what);
		Surface.EditPointAdded += point => _vm?.AddEditPoint(point);
		Surface.EditDeleteRequested += () => _vm?.DeleteEditFeatureCommand.Execute(null);
		Surface.EditRefused += reason =>
		{
			if (_vm != null)
			{
				_vm.Message = reason;
			}
		};
		Surface.MarkerDragged += (id, to) => _ = _vm?.DragStructureAsync(id, to);
		Surface.ViewChanged += OnViewChanged;
		Surface.SizeChanged += OnCanvasSizeChanged;
		ZoomInButton.Click += (_, _) => Surface.ZoomBy(1);
		ZoomOutButton.Click += (_, _) => Surface.ZoomBy(-1);
		ActualThemeVariantChanged += (_, _) =>
		{
			Rebuild();
			BuildHeatRamp();
		SelectionBar.SizeChanged += (_, _) => PlaceSelectionBar();
		LegendCard.SizeChanged += (_, _) => PlaceSelectionBar();
		MapArea.SizeChanged += (_, _) => PlaceSelectionBar();
			_analysis.Refresh();
		};
		BuildHeatRamp();
		PointList.DoubleTapped += (_, _) => _vm?.OpenSelectedCommand.Execute(null);
		TableGrid.SelectionChanged += OnTableSelectionChanged;
		TableGrid.DoubleTapped += (_, _) => _vm?.ZoomToRowCommand.Execute(TableGrid.SelectedItem as AttributeRowViewModel);
	}

	private bool _fillingTable;

	/// <summary>Rows picked in the table are selected on the map (not while the table is being filled).</summary>
	private void OnTableSelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_vm != null && !_fillingTable && TableGrid.SelectedItems.Count > 0)
		{
			_vm.SelectTableRows(TableGrid.SelectedItems.OfType<AttributeRowViewModel>().ToList());
		}
	}

	/// <summary>Makes the table's columns for the layer it shows: one per field, sorting by the field's value.</summary>
	private void BuildTableColumns()
	{
		_fillingTable = true;
		TableGrid.Columns.Clear();
		bool editable = _vm?.TableEditable == true;
		foreach (AttributeColumn column in _vm?.TableColumns ?? Array.Empty<AttributeColumn>())
		{
			TableGrid.Columns.Add(new DataGridTextColumn
			{
				Header = column.Header,
				// While the layer is being edited, a typed value goes back to the row, which checks it.
				Binding = new Avalonia.Data.Binding($"[{column.Index}]") { Mode = editable ? Avalonia.Data.BindingMode.TwoWay : Avalonia.Data.BindingMode.OneWay },
				CustomSortComparer = new AttributeComparer(column.Index),
				CanUserSort = true,
				IsReadOnly = !editable,
				MaxWidth = 320,
			});
		}
		_fillingTable = false;
	}

	/// <summary>The map control, for tests that need its screen positions.</summary>
	public MapCanvas Map => Surface;

	/// <summary>What keeps the analysis layers (clusters, heat map, water surface) up to date, for tests.</summary>
	public MapAnalysisRunner Analysis => _analysis;

	private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
	{
		if (!_fitted && e.NewSize.Width > 20 && e.NewSize.Height > 20)
		{
			_fitted = true;
			Surface.FitToMarkers();
		}
		OnViewChanged();
	}

	private void OnViewChanged()
	{
		if (_vm != null && Surface.Bounds.Width > 0)
		{
			_vm.ViewExtent = Surface.Extent;
			_analysis.ViewChanged();
		}
	}

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		Detach();
		_vm = DataContext as MapViewModel;
		if (_vm == null)
		{
			return;
		}
		_vm.MarkersChanged += Rebuild;
		_vm.LayersChanged += ShowLayers;
		_vm.FitRequested += Surface.FitToMarkers;
		_vm.CenterRequested += Center;
		_vm.ZoomToRequested += ZoomTo;
		_vm.RetryRequested += Surface.RetryTiles;
		_vm.SelectionChanged += ShowSelection;
		_vm.TableColumnsChanged += BuildTableColumns;
		_vm.ClearSketchRequested += Surface.ClearSketch;
		_vm.ShowViewRequested += ShowView;
		_vm.MoveAnswered += Surface.ClearPendingMove;
		_vm.PropertyChanged += VmChanged;
		_vm.AnalysisChanged += UpdateAnalysis;
		_vm.PrintScene = BuildPrintScene;
		_vm.WatchMarks();
		Surface.Tiles = _vm.Tiles;
		Surface.Basemap = _vm.Basemap;
		ApplyMode();
		Rebuild();
		ShowLayers();
		BuildTableColumns();
	}

	protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnDetachedFromVisualTree(e);
		Detach();
		// The page closed: stop the analysis work and the map engine (its redraw loop, downloads and layers).
		_analysis.Stop();
		Surface.Dispose();
	}

	private void Detach()
	{
		if (_vm == null)
		{
			return;
		}
		_vm.MarkersChanged -= Rebuild;
		_vm.LayersChanged -= ShowLayers;
		_vm.FitRequested -= Surface.FitToMarkers;
		_vm.CenterRequested -= Center;
		_vm.ZoomToRequested -= ZoomTo;
		_vm.RetryRequested -= Surface.RetryTiles;
		_vm.SelectionChanged -= ShowSelection;
		_vm.TableColumnsChanged -= BuildTableColumns;
		_vm.ClearSketchRequested -= Surface.ClearSketch;
		_vm.ShowViewRequested -= ShowView;
		_vm.MoveAnswered -= Surface.ClearPendingMove;
		_vm.PropertyChanged -= VmChanged;
		_vm.AnalysisChanged -= UpdateAnalysis;
		if (_vm.PrintScene == BuildPrintScene)
		{
			_vm.PrintScene = null;
		}
		Surface.Tiles = null;
		_vm = null;
	}

	private void VmChanged(object sender, PropertyChangedEventArgs e)
	{
		switch (e.PropertyName)
		{
			case nameof(MapViewModel.Basemap):
				Surface.Basemap = _vm.Basemap;
				break;
			case nameof(MapViewModel.Mode):
			case nameof(MapViewModel.Sketch):
			case nameof(MapViewModel.SelectLayerKey):
			case nameof(MapViewModel.EditLayer):
			case nameof(MapViewModel.EditTool):
			case nameof(MapViewModel.SnapToShapes):
				ApplyMode();
				break;
			case nameof(MapViewModel.EditFeature):
				Surface.EditFeature = _vm.EditFeature;
				break;
			case nameof(MapViewModel.Selected):
				Surface.Highlighted = _vm.Selected?.Id;
				break;
		}
	}

	/// <summary>What a click and a drag do on the map in the page's mode.</summary>
	private void ApplyMode()
	{
		Surface.Drag = _vm.IsSelect ? MapDrag.Box : MapDrag.Pan;
		Surface.SelectLayer = _vm.IsSelect ? _vm.SelectLayerKey : null;
		Surface.EditLayer = _vm.IsEdit ? _vm.EditLayer?.Key : null;
		Surface.EditTool = _vm.EditTool;
		Surface.EditFeature = _vm.IsEdit ? _vm.EditFeature : null;
		Surface.Snap = _vm.SnapToShapes;
		Surface.MarkersDraggable = _vm.IsSetLocation;
		if (Surface.Sketch != _vm.Sketch)
		{
			Surface.Sketch = _vm.Sketch;
		}
		bool cross = _vm.IsSetLocation || _vm.Sketch != MapSketch.None || _vm.IsEdit && _vm.EditTool == MapEditTool.AddPoint;
		Surface.Cursor = new Avalonia.Input.Cursor(cross ? Avalonia.Input.StandardCursorType.Cross : Avalonia.Input.StandardCursorType.Arrow);
	}

	// ───────────── The toolbar's More menu: Background Map ─────────────

	/// <summary>A basemap's row in More › Background Map: its name for VoiceOver, and a tick if it is the one shown.</summary>
	private void OnBasemapItemPrepared(object sender, ContainerPreparedEventArgs e)
	{
		if (e.Container is MenuItem item && item.DataContext is Basemap basemap)
		{
			Avalonia.Automation.AutomationProperties.SetName(item, basemap.Name);
			Tick(item);
		}
	}

	/// <summary>The submenu opened: the tick follows the basemap (it can be changed in Settings too).</summary>
	private void OnBasemapMenuOpened(object sender, RoutedEventArgs e)
	{
		foreach (MenuItem item in (sender as MenuItem)?.GetRealizedContainers().OfType<MenuItem>() ?? Enumerable.Empty<MenuItem>())
		{
			Tick(item);
		}
	}

	private void Tick(MenuItem item) => item.Icon = _vm != null && ReferenceEquals(item.DataContext, _vm.Basemap) ? new Sde.App.Controls.Icon { Kind = "Check", Size = 12 } : null;

	/// <summary>A basemap was chosen in More › Background Map.</summary>
	private void OnBasemapChosen(object sender, RoutedEventArgs e)
	{
		if (_vm != null && e.Source is MenuItem { DataContext: Basemap basemap })
		{
			_vm.Basemap = basemap;
		}
	}

	/// <summary>Shows exactly the area of a bookmark.</summary>
	private void ShowView(MapExtent extent) => Surface.FitTo(extent, MapCanvas.MaxZoom, margin: 0);

	private void Center(MapPointViewModel p) => Surface.CenterOn(p.Latitude, p.Longitude);

	private void ZoomTo(MapExtent extent) => Surface.FitTo(extent);

	private void ShowLayers()
	{
		if (_vm != null)
		{
			Surface.ShowLayers(_vm.MapLayers());
		}
	}

	/// <summary>Rings the selected structures.</summary>
	private void ShowSelection()
	{
		if (_vm != null)
		{
			Surface.SelectedMarkers = _vm.SelectedStructures();
		}
	}

	/// <summary>
	/// Hands the visible structures to the map: each in the colour of its result class (the theme's), or in its
	/// category's colour when the structures are coloured by something else, with its label when labels are on.
	/// </summary>
	private void Rebuild()
	{
		if (_vm == null)
		{
			return;
		}
		(List<MapMarker> markers, IReadOnlyDictionary<Guid, string> labels) = BuildMarkers(ActualThemeVariant);
		Surface.MarkerLabels = labels == null ? null : _vm.StructuresLayer.Labels;
		Surface.Markers = markers;
		Surface.Highlighted = _vm.Selected?.Id;
		ShowSelection();
		UpdateAnalysis();
	}

	// ───────────── Analysis: clusters, heat map, water surface ─────────────

	/// <summary>The structures, the way they are shown or the surface changed: the analysis layers follow (nothing runs while all are off).</summary>
	private void UpdateAnalysis()
	{
		if (_vm != null)
		{
			_analysis.Update(_vm.AnalysisInputs());
		}
	}

	/// <summary>
	/// The selection bar sits at the bottom centre of the map; where it would run into the legend card (a narrow map,
	/// many actions) it rises above the legend instead, so nothing is covered or cut off.
	/// </summary>
	private void PlaceSelectionBar()
	{
		double room = (SelectionBar.Parent as Control)?.Bounds.Width ?? MapArea.Bounds.Width;
		double barLeft = (room - SelectionBar.Bounds.Width) / 2;
		double legendRight = LegendCard.Bounds.Right + 8;
		bool collides = LegendCard.IsVisible && barLeft < legendRight;
		Thickness wanted = collides ? new Thickness(0, 0, 0, LegendCard.Bounds.Height + 10) : new Thickness(0);
		if (SelectionBar.Margin != wanted)
		{
			SelectionBar.Margin = wanted;
		}
	}

	/// <summary>The heat map's ramp in the legend: the theme's stop colours at the positions the map uses, the coolest end faint.</summary>
	private void BuildHeatRamp()
	{
		LinearGradientBrush ramp = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative) };
		for (int i = 0; i < MapAnalysis.HeatStopKeys.Length; i++)
		{
			Color c = this.TryFindResource(MapAnalysis.HeatStopKeys[i], ActualThemeVariant, out object r) && r is Color found ? found : Colors.SteelBlue;
			if (i == 0)
			{
				c = Color.FromArgb(60, c.R, c.G, c.B);
			}
			ramp.GradientStops.Add(new GradientStop(c, MapAnalysis.HeatStopPositions[i]));
		}
		HeatRamp.Background = ramp;
	}

	/// <summary>
	/// The structure dots to draw: each in the colour of its result class (the theme's), or in its category's colour
	/// when the structures are coloured by something else, with its label when labels are on.
	/// </summary>
	private (List<MapMarker> Markers, IReadOnlyDictionary<Guid, string> Labels) BuildMarkers(ThemeVariant theme)
	{
		Dictionary<string, IBrush> brushes = new();
		IReadOnlyDictionary<Guid, string> fills = _vm.ColoursByResult ? null : _vm.StructureFills();
		IReadOnlyDictionary<Guid, string> labels = _vm.StructureLabels();
		List<MapMarker> markers = new(_vm.Visible.Count);
		bool shown = _vm.StructuresLayer?.IsVisible ?? true;
		foreach (MapPointViewModel p in shown ? (IEnumerable<MapPointViewModel>)_vm.Visible : Array.Empty<MapPointViewModel>())
		{
			string key;
			if (fills == null)
			{
				key = "Status" + p.Class + "Fg";
			}
			else if (!fills.TryGetValue(p.Id, out key))
			{
				// Its category is unticked in the style.
				continue;
			}
			if (!brushes.TryGetValue(key, out IBrush fill))
			{
				fill = fills == null ? ThemeBrushFor(key, theme) : LegendLineViewModel.Brush(key);
				brushes[key] = fill;
			}
			markers.Add(new MapMarker(p.Id, p.Latitude, p.Longitude, fill, labels != null && labels.TryGetValue(p.Id, out string text) ? text : null));
		}
		return (markers, labels);
	}

	private IBrush ThemeBrushFor(string key, ThemeVariant theme) => this.TryFindResource(key, theme, out object r) && r is IBrush b ? b : Brushes.Gray;

	// ───────────── Printing ─────────────

	/// <summary>The map as it is on screen, in the light theme's colours (paper is white), with its legend.</summary>
	/// <summary>The analysis pictures for a print: the heat map is drawn again in the light theme's colours, since paper is white.</summary>
	private List<MapRasterOverlay> PrintRasters()
	{
		MapRasterOverlay lightHeat = _analysis.HeatForPrint(key => Application.Current!.TryGetResource(key, ThemeVariant.Light, out object r) ? r : null);
		return Surface.Rasters.Select(r => r.Key == MapAnalysis.HeatKey && lightHeat != null ? lightHeat : r).ToList();
	}

	private MapPrintScene BuildPrintScene()
	{
		if (_vm == null)
		{
			return null;
		}
		(List<MapMarker> markers, IReadOnlyDictionary<Guid, string> labels) = BuildMarkers(ThemeVariant.Light);
		// The picture is drawn off the UI thread: brushes that belong to no thread.
		Dictionary<IBrush, IBrush> frozen = new Dictionary<IBrush, IBrush>();
		for (int i = 0; i < markers.Count; i++)
		{
			IBrush fill = markers[i].Fill ?? Brushes.Gray;
			if (!frozen.TryGetValue(fill, out IBrush still))
			{
				Color c = fill is ISolidColorBrush solid ? solid.Color : Colors.Gray;
				still = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(c, fill.Opacity);
				frozen[fill] = still;
			}
			markers[i] = markers[i] with { Fill = still };
		}
		(double x, double y, double resolution) = Surface.ViewInMetres;
		IReadOnlyList<object> layers = _vm.MapLayers();
		return new MapPrintScene
		{
			CenterX = x,
			CenterY = y,
			Resolution = resolution,
			ScreenWidth = Surface.Bounds.Width,
			ScreenHeight = Surface.Bounds.Height,
			Basemap = _vm.Basemap,
			Overlays = layers.OfType<MapTileLayer>().Where(l => l.Visible).ToList(),
			Layers = layers.OfType<MapFeatureLayer>().Where(l => l.Visible).ToList(),
			Markers = markers,
			MarkerLabels = labels == null ? null : _vm.StructuresLayer.Labels,
			Clusters = PrintClusters(),
			MarkersHidden = Surface.MarkersHidden,
			Rasters = PrintRasters(),
			Legend = PrintLegend(),
		};
	}

	/// <summary>The clusters on screen in the light theme's colours, with brushes that belong to no thread (print draws off the UI thread).</summary>
	private List<MapClusterMark> PrintClusters()
	{
		if (Surface.Clusters == null)
		{
			return null;
		}
		IBrush light = Frozen(ThemeBrushFor("MapClusterInkLight", ThemeVariant.Light));
		IBrush dark = Frozen(ThemeBrushFor("MapClusterInkDark", ThemeVariant.Light));
		return Surface.Clusters.Select(c =>
		{
			IBrush fill = Frozen(ThemeBrushFor(MapAnalysis.DotKey(c.Worst), ThemeVariant.Light));
			return c with { Fill = fill, Ink = MapAnalysis.InkFor(fill, light, dark) };
		}).ToList();
	}

	private static IBrush Frozen(IBrush brush) =>
		new Avalonia.Media.Immutable.ImmutableSolidColorBrush(brush is ISolidColorBrush s ? s.Color : Colors.Gray, brush?.Opacity ?? 1);

	/// <summary>The printed legend: the structures (by result, or by the field they are coloured by), then each layer shown, top first.</summary>
	private List<MapLegendGroup> PrintLegend()
	{
		List<MapLegendGroup> groups = new List<MapLegendGroup>();
		string dotOutline = Hex(ThemeBrushFor("MapLegendDotOutline", ThemeVariant.Light));
		if ((_vm.StructuresLayer?.IsVisible ?? true) && _vm.IsClusters)
		{
			List<MapLegendItem> items = _vm.Classes.Where(c => c.IsOn && c.Count > 0)
				.Select(c => new MapLegendItem(c.Label, MapLegendShape.Dot, Hex(ThemeBrushFor(MapAnalysis.DotKey(c.Class), ThemeVariant.Light)), dotOutline)).ToList();
			groups.Add(new MapLegendGroup("Structures in clusters, by worst result", items));
		}
		else if ((_vm.StructuresLayer?.IsVisible ?? true) && _vm.IsHeatmap)
		{
			List<MapLegendItem> items = new List<MapLegendItem>();
			string[] words = { "Fewest", "", "", "Most" };
			for (int i = 0; i < MapAnalysis.HeatStopKeys.Length; i++)
			{
				Color c = this.TryFindResource(MapAnalysis.HeatStopKeys[i], ActualThemeVariant, out object r) && r is Color found ? found : Colors.SteelBlue;
				if (words[i].Length > 0)
				{
					items.Add(new MapLegendItem(words[i], MapLegendShape.Area, $"#{c.R:X2}{c.G:X2}{c.B:X2}{(i == 0 ? 0x50 : 0xCC):X2}", $"#{c.R:X2}{c.G:X2}{c.B:X2}FF"));
				}
			}
			groups.Add(new MapLegendGroup("Heat map of structures, " + _vm.HeatLegendNote.ToLowerInvariant(), items));
		}
		else if (_vm.StructuresLayer?.IsVisible ?? true)
		{
			List<MapLegendItem> items = _vm.ColoursByResult
				? _vm.Classes.Where(c => c.IsOn && c.Count > 0).Select(c => new MapLegendItem(c.Label, MapLegendShape.Dot, Hex(ThemeBrushFor(c.ColourKey, ThemeVariant.Light)), dotOutline)).ToList()
				: _vm.StructureLegend.Select(l => new MapLegendItem(l.Label, MapLegendShape.Dot, Hex(l.Fill), dotOutline)).ToList();
			string by = _vm.ColoursByResult ? "result" : (_vm.StructureLegendTitle ?? "").Replace("COLOURED BY ", "", StringComparison.Ordinal).ToLowerInvariant();
			groups.Add(new MapLegendGroup("Structures, by " + by, items));
		}
		foreach (MapLayerViewModel layer in _vm.Layers.Where(l => !l.IsStructures && l.IsVisible))
		{
			if (layer.IsTiles)
			{
				groups.Add(new MapLegendGroup(layer.Name, new[]
				{
					new MapLegendItem("Flood hazard zones from FEMA's National Flood Hazard Layer", MapLegendShape.Area,
						Hex(ThemeBrushFor("MapFloodZoneFill", ThemeVariant.Light)), Hex(ThemeBrushFor("MapFloodZoneStroke", ThemeVariant.Light))),
				}));
			}
			else if (layer.Data != null)
			{
				MapLegendShape shape = layer.GeometryKind switch
				{
					Sde.Gis.Features.GeometryKind.Point => MapLegendShape.Dot,
					Sde.Gis.Features.GeometryKind.Line => MapLegendShape.Line,
					_ => MapLegendShape.Area,
				};
				groups.Add(new MapLegendGroup(layer.Name, layer.Legend.Select(l => new MapLegendItem(l.Label, shape, Hex(l.Fill), Hex(l.Stroke))).ToList()));
			}
		}
		// The water surface, as on screen: an elevation in the marks' datum (not a depth), in its classes for the area shown.
		if (_vm.ShowsSurface && _vm.HasSurfaceLegend)
		{
			groups.Add(new MapLegendGroup(_vm.SurfaceLegendTitle, _vm.SurfaceLegend
				.Select(l => new MapLegendItem(l.Label, MapLegendShape.Area, Hex(ThemeBrushFor(l.ColourKey, ThemeVariant.Light)), Hex(ThemeBrushFor(l.ColourKey, ThemeVariant.Light)))).ToList()));
		}
		return groups;
	}

	/// <summary>"#RRGGBBAA" for a solid brush (grey otherwise).</summary>
	private static string Hex(IBrush brush)
	{
		Color c = brush is ISolidColorBrush s ? s.Color : Colors.Gray;
		return $"#{c.R:X2}{c.G:X2}{c.B:X2}{(byte)Math.Round(c.A * (brush?.Opacity ?? 1)):X2}";
	}
}
