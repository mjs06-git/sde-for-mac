using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Sde.App.Controls;
using Sde.App.Maps;
using Sde.App.Services;
using Sde.App.ViewModels;
using Sde.App.Views;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Depth;
using Sde.Core.Records;
using Sde.Data;
using Sde.Gis.Analysis;
using Sde.Reports;
using SkiaSharp;
using Page = Sde.App.ViewModels.Page;

namespace Sde.App.Tests;

/// <summary>
/// The Map page's floating controls and its analysis layers: the structures as clusters or a heat map, the water
/// surface from high-water marks, the selection bar, the attribute drawer, printing, and the engine stopping.
/// </summary>
public class MapAnalysisLayersTests
{
	/// <summary>
	/// Sixty made-up structures along five made-up streets near the samples, with a spread of percent damaged: worst by
	/// the river, lighter uphill, a few with no assessment yet. Coordinates are synthetic.
	/// </summary>
	internal static void SeedStreets(SdeStore store)
	{
		// (street, first latitude, first longitude, step north, step east, houses, lowest and highest share damaged)
		(string Street, double Lat, double Lon, double DLat, double DLon, int Count, double Low, double High)[] streets =
		{
			("Alder", 39.0990, -84.5225, 0.00005, 0.00048, 14, 58, 96),
			("Birch", 39.1004, -84.5222, 0.00004, 0.00047, 12, 38, 70),
			("Cedar", 39.1020, -84.5218, 0.00002, 0.00046, 12, 20, 52),
			("Dogwood", 39.1034, -84.5140, 0.00042, 0.00003, 12, 4, 34),
			("Elm", 39.1098, -84.5032, 0.00010, 0.00045, 10, 36, 84),
		};
		int n = 0;
		foreach ((string street, double lat, double lon, double dLat, double dLon, int count, double low, double high) in streets)
		{
			for (int k = 0; k < count; k++, n++)
			{
				PropertyRecord p = store.SaveProperty(new PropertyRecord
				{
					OwnerFirstName = "Sample", OwnerLastName = "Owner " + (n + 1),
					StreetNumber = (100 + 10 * k).ToString(), StreetName = street, StreetSuffixID = 172,
					City = SampleDatabase.Town, StateID = 36, NFIPCID = "990001", NFIPCommunityName = "Exampleville, Town of",
					StructureTypeID = 1, ResidenceTypeID = 92, FoundationTypeID = 45, SuperStructureTypeID = 130,
					RoofCoveringTypeID = 95, ExteriorFinishTypeID = 25, HVACTypeID = 49, StoryTypeID = 104,
					YearOfConstruction = 1962, StructureQualityID = 54, SquareFootage = 1200 + 20 * n,
					Latitude = (decimal)Math.Round(lat + dLat * k + (k % 3 - 1) * 0.00006, 6),
					Longitude = (decimal)Math.Round(lon + dLon * k, 6),
				});
				if (n % 15 == 7)
				{
					continue;   // no assessment yet: incomplete
				}
				AssessmentFile f = store.CreateAssessment(p.PropertyID, applyDefaults: false);
				AssessmentRecord a = f.Assessment;
				a.InspectorName = SampleDatabase.Inspector;
				a.DateDamageOccurred = new DateTime(2026, 9, 14);
				a.DateofInspection = new DateTime(2026, 9, 16);
				a.CauseofDamageID = LookupIds.CauseFlood;
				a.BaseCost = 118.75m;
				a.GeographicAdjustment = 0.93m;
				store.Calculator.NormalizeRows(f);
				store.Calculator.ApplyDepreciationRating(f, 386);
				double share = low + (high - low) * ((k * 7) % count) / Math.Max(1, count - 1);
				decimal damage = (decimal)Math.Round(share * 0.758);
				foreach (ElementPercentageRecord e in f.ElementPercentages)
				{
					e.UserDamage = Math.Min(100, damage);
				}
				store.SaveAssessment(f);
			}
		}
	}

	/// <summary>Made-up high-water marks around the streets, in NAVD88, the water higher to the north-east.</summary>
	internal static List<HighWaterMark> Marks() => new List<HighWaterMark>
	{
		new HighWaterMark { Id = "M1", Latitude = 39.0985, Longitude = -84.5225, ElevationFt = 486.2m, DatumName = "NAVD88", Quality = 2 },
		new HighWaterMark { Id = "M2", Latitude = 39.0992, Longitude = -84.5180, ElevationFt = 486.9m, DatumName = "NAVD88", Quality = 2 },
		new HighWaterMark { Id = "M3", Latitude = 39.1000, Longitude = -84.5160, ElevationFt = 487.3m, DatumName = "NAVD88", Quality = 3 },
		new HighWaterMark { Id = "M4", Latitude = 39.1012, Longitude = -84.5205, ElevationFt = 487.0m, DatumName = "NAVD88", Quality = 2 },
		new HighWaterMark { Id = "M5", Latitude = 39.1025, Longitude = -84.5170, ElevationFt = 487.8m, DatumName = "NAVD88", Quality = 1 },
		new HighWaterMark { Id = "M6", Latitude = 39.1040, Longitude = -84.5135, ElevationFt = 488.4m, DatumName = "NAVD88", Quality = 2 },
		new HighWaterMark { Id = "M7", Latitude = 39.1060, Longitude = -84.5120, ElevationFt = 488.9m, DatumName = "NAVD88", Quality = 3 },
		new HighWaterMark { Id = "M8", Latitude = 39.1095, Longitude = -84.5040, ElevationFt = 489.6m, DatumName = "NAVD88", Quality = 2 },
		new HighWaterMark { Id = "M9", Latitude = 39.1105, Longitude = -84.4995, ElevationFt = 490.1m, DatumName = "NAVD88", Quality = 2 },
	};

	/// <summary>The Map page with the sample structures and the sixty on the streets, over the (fake) topographic basemap.</summary>
	private static (UiSession Ui, MapView View) Streets(ThemeVariant theme = null)
	{
		UiSession ui = new UiSession(theme);
		ScreenTourTests.AddSamples(ui.Store);
		SeedStreets(ui.Store);
		ui.Services.RaiseDataChanged();
		ui.Main.Map.Basemap = Basemaps.UsgsTopo;
		MapView view = MapTestSupport.ShowMap(ui);
		view.Map.SetView(39.1035, -84.5130, 15.4);
		MapTestSupport.Settle(ui, view);
		return (ui, view);
	}

	/// <summary>Waits for the analysis work to be drawn.</summary>
	private static void Analyse(UiSession ui, MapView view, int timeoutMs = 15000)
	{
		ui.Pump();
		System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
		while (view.Analysis.IsBusy && clock.ElapsedMilliseconds < timeoutMs)
		{
			ui.Pump();
			Thread.Sleep(10);
		}
		Assert.False(view.Analysis.IsBusy, "The analysis did not finish.");
		MapTestSupport.Settle(ui, view);
	}

	/// <summary>A brush of the theme showing now.</summary>
	private static IBrush ThemeBrush(string key)
	{
		Assert.True(Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out object value), key);
		return (IBrush)value;
	}

	private static T Named<T>(UiSession ui, string name) where T : Control => ui.Window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

	[AvaloniaFact]
	public void TheMap_FillsThePage_WithFloatingControls_ThatLeaveTheMapItsClicks()
	{
		(UiSession ui, MapView view) = Streets();
		using (ui)
		{
			// The map runs to the content panel's edges; the inspector sits beside it.
			Control area = Named<Control>(ui, "MapArea");
			Assert.Equal(area.Bounds.Size, view.Map.Bounds.Size);
			Assert.Equal(0, view.Map.TranslatePoint(new Point(0, 0), view)!.Value.X, 1);
			Border inspector = Named<Border>(ui, "Inspector");
			Assert.True(inspector.IsEffectivelyVisible);
			Assert.Equal(ui.Main.InspectorWidth, inspector.Bounds.Width, 1);
			Assert.True(Named<ResizeHandle>(ui, "InspectorResizer").IsEffectivelyVisible);

			// The palette, the zoom card and the legend float over the map as cards.
			foreach (string name in new[] { "ToolPalette", "ZoomCard", "LegendCard" })
			{
				Border card = Named<Border>(ui, name);
				Assert.True(card.IsEffectivelyVisible, name);
				Assert.Contains("mapFloat", card.Classes);
			}
			Assert.False(Named<Border>(ui, "SelectionBar").IsEffectivelyVisible);
			Assert.False(Named<Border>(ui, "TableDrawer").IsEffectivelyVisible);

			// Empty map between the cards hits the map, not a float.
			Point middle = view.Map.TranslatePoint(new Point(view.Map.Bounds.Width / 2, view.Map.Bounds.Height / 2), ui.Window)!.Value;
			IInputElement hit = ui.Window.InputHitTest(middle);
			Assert.True(hit is Visual v && (ReferenceEquals(v, view.Map) || v.FindAncestorOfType<MapCanvas>() != null), hit?.GetType().Name);

			// The inspector toggle hides the pane (one setting with Structures'), and the map widens.
			double before = view.Map.Bounds.Width;
			ui.Main.Map.ToggleInspectorCommand.Execute(null);
			ui.Pump();
			Assert.False(ui.Main.Structures.IsInspectorVisible);
			Assert.False(inspector.IsEffectivelyVisible);
			Assert.True(view.Map.Bounds.Width > before);
			ui.Main.Map.ToggleInspectorCommand.Execute(null);
			ui.Pump();
			Assert.True(inspector.IsEffectivelyVisible);
			Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
			ui.Snapshot("56l-map-floating");
		}
	}

	[AvaloniaFact]
	public void TheFloatingControls_InTheDarkTheme()
	{
		(UiSession ui, MapView view) = Streets(ThemeVariant.Dark);
		using (ui)
		{
			ui.Main.Map.Display = StructureDisplay.Clusters;
			Analyse(ui, view);
			ui.Snapshot("56m-dark-map-clusters");
		}
	}

	[AvaloniaFact]
	public void TheSelectionBar_ComesWithASelection_AndGoesWithClear()
	{
		(UiSession ui, MapView view) = Streets();
		using (ui)
		{
			MapViewModel map = ui.Main.Map;
			Border bar = Named<Border>(ui, "SelectionBar");
			Assert.False(bar.IsEffectivelyVisible);
			List<Guid> ids = map.Visible.Where(p => p.AssessmentID != null).Take(12).Select(p => p.Id).ToList();
			map.SelectArea(ids, add: false);
			ui.Pump();
			Assert.True(bar.IsEffectivelyVisible);
			Assert.Equal("12 structures selected", map.SelectionText);
			List<Button> buttons = bar.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
			Assert.Equal(new object[] { "Show in Structures", "Set a Field…", "Assign to Team…", "More", "Clear" }, buttons.Select(b => b.Content));
			Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
			ui.Snapshot("56n-map-selection-bar");

			buttons.Last().Command!.Execute(null);
			ui.Pump();
			Assert.False(map.HasChecked);
			// Clearing leaves its one-line result in the bar until the next action.
			Assert.True(bar.IsEffectivelyVisible);
			map.Message = null;
			ui.Pump();
			Assert.False(bar.IsEffectivelyVisible);
		}
	}

	[AvaloniaFact]
	public void ShowAs_SwitchesBetweenMarkersClustersAndHeatMap()
	{
		(UiSession ui, MapView view) = Streets();
		using (ui)
		{
			MapViewModel map = ui.Main.Map;
			ListBox showAs = Named<ListBox>(ui, "ShowAsControl");
			Assert.Equal(0, showAs.SelectedIndex);
			Assert.Null(view.Map.Clusters);
			Assert.False(view.Map.MarkersHidden);

			view.Map.ZoomBy(-0.6);
			showAs.SelectedIndex = 1;
			Analyse(ui, view);
			Assert.Equal(StructureDisplay.Clusters, map.Display);
			Assert.NotNull(view.Map.Clusters);
			Assert.True(view.Map.DrawnClusterCount > 0);
			Assert.False(view.Map.HasRaster(MapAnalysis.HeatKey));
			Assert.Equal("Clusters, by worst result", map.LegendTitle);
			ui.Snapshot("56o-map-clusters");

			showAs.SelectedIndex = 2;
			Analyse(ui, view);
			Assert.Equal(StructureDisplay.Heatmap, map.Display);
			Assert.Null(view.Map.Clusters);
			Assert.True(view.Map.MarkersHidden);
			Assert.True(view.Map.HasRaster(MapAnalysis.HeatKey));
			Assert.True(Named<Border>(ui, "HeatRamp").IsEffectivelyVisible);
			ui.Snapshot("56p-map-heat");

			showAs.SelectedIndex = 0;
			Analyse(ui, view);
			Assert.Null(view.Map.Clusters);
			Assert.False(view.Map.MarkersHidden);
			Assert.False(view.Map.HasRaster(MapAnalysis.HeatKey));
			Assert.Equal(60, view.Map.Markers.Count);   // the three samples have no coordinates
		}
	}

	[AvaloniaFact]
	public void Clusters_MatchTheClusterer_AtTwoZooms_AndAClickZoomsIn()
	{
		(UiSession ui, MapView view) = Streets();
		using (ui)
		{
			MapViewModel map = ui.Main.Map;
			map.Display = StructureDisplay.Clusters;
			Analyse(ui, view);
			IReadOnlyList<AnalysisPoint> points = map.AnalysisInputs().Points;
			Assert.Equal(map.Visible.Count, points.Count);

			int[] Expected() => MarkerClusterer.Cluster(MapAnalysis.ClusterInput(points), MapAnalysis.ClusterRadiusPixels * view.Map.ViewInMetres.Resolution)
				.Select(c => c.Count).ToArray();
			int[] far = view.Analysis.Clusters.Select(c => c.Count).ToArray();
			Assert.Equal(Expected(), far);
			Assert.Equal(points.Count, far.Sum());
			Assert.Contains(far, c => c > 1);

			// Zoomed in two steps: smaller clusters, re-clustered once the map stopped.
			int runs = view.Analysis.ClusterRuns;
			view.Map.ZoomBy(2);
			Analyse(ui, view);
			Assert.True(view.Analysis.ClusterRuns > runs);
			int[] near = view.Analysis.Clusters.Select(c => c.Count).ToArray();
			Assert.Equal(Expected(), near);
			Assert.True(near.Length > far.Length);

			// Panning keeps the zoom: no new clusters are worked out.
			runs = view.Analysis.ClusterRuns;
			view.Map.SetView(view.Map.CenterLatitude + 0.0005, view.Map.CenterLongitude, view.Map.Zoom);
			Analyse(ui, view);
			Assert.Equal(runs, view.Analysis.ClusterRuns);

			// Back out: a cluster's colour is its worst result's, and clicking it zooms to its members.
			view.Map.ZoomBy(-2);
			Analyse(ui, view);
			MapClusterMark big = view.Map.Clusters.Where(c => c.Count > 1).OrderByDescending(c => c.Count).First();
			MarkerCluster source = view.Analysis.Clusters.First(c => c.Count == big.Count && Math.Abs(c.X - big.X) < 1e-6);
			Assert.Equal(MapAnalysis.WorstClass(source), big.Worst);
			Assert.Equal(((ISolidColorBrush)ThemeBrush(MapAnalysis.DotKey(big.Worst))).Color, ((ISolidColorBrush)big.Fill).Color);
			double zoom = view.Map.Zoom;
			(double lon, double lat) = WebMercator.FromMetres(big.X, big.Y);
			view.Map.Click(view.Map.ToScreen(lat, lon));
			ui.Pump();
			Assert.True(view.Map.Zoom > zoom);
			Assert.Equal(big.Members.CenterLatitude, view.Map.CenterLatitude, 3);
		}
	}

	[AvaloniaFact]
	public void TheHeatMap_IsOnlyWorkedOutInItsMode_AndAgainAfterTheViewMoves()
	{
		(UiSession ui, MapView view) = Streets();
		using (ui)
		{
			MapViewModel map = ui.Main.Map;
			// Off: moving the map works nothing out.
			view.Map.ZoomBy(-1);
			Analyse(ui, view);
			Assert.Equal(0, view.Analysis.HeatRuns);
			Assert.Equal(0, view.Analysis.ClusterRuns);
			Assert.Null(view.Analysis.Heat);

			map.Display = StructureDisplay.Heatmap;
			Analyse(ui, view);
			Assert.Equal(1, view.Analysis.HeatRuns);
			MapRasterOverlay first = view.Analysis.Heat;
			Assert.NotNull(first);
			(double cx, double cy, double resolution) = view.Map.ViewInMetres;
			Assert.Equal(cx, (first.MinX + first.MaxX) / 2, 0);
			Assert.Equal(view.Map.Bounds.Width * resolution, first.MaxX - first.MinX, 0);

			view.Map.SetView(view.Map.CenterLatitude + 0.002, view.Map.CenterLongitude - 0.002, view.Map.Zoom);
			Analyse(ui, view);
			Assert.True(view.Analysis.HeatRuns >= 2);
			MapRasterOverlay second = view.Analysis.Heat;
			Assert.NotSame(first, second);
			Assert.True(second.MinX < first.MinX);
			Assert.True(view.Map.HasRaster(MapAnalysis.HeatKey));
			Assert.False(view.Map.HasRaster(MapAnalysis.SurfaceKey));

			// The weighting is the user's: one per structure works it out again.
			int runs = view.Analysis.HeatRuns;
			map.HeatWeightIndex = 1;
			Analyse(ui, view);
			Assert.False(map.HeatByDamage);
			Assert.Equal(runs + 1, view.Analysis.HeatRuns);
			Assert.Equal("One per structure", map.HeatLegendNote);
		}
	}

	[AvaloniaFact]
	public void TheWaterSurface_IsOfferedOnlyWithMarks_AndItsLegendHasFiveClasses()
	{
		(UiSession ui, MapView view) = Streets();
		using (ui)
		{
			MapViewModel map = ui.Main.Map;
			map.PanelIndex = 1;
			ui.Pump();
			ToggleSwitch toggle = Named<ToggleSwitch>(ui, "SurfaceSwitch");
			Assert.False(map.CanShowWaterSurface);
			Assert.False(toggle.IsEffectivelyVisible);
			map.ShowWaterSurface = true;
			Analyse(ui, view);
			Assert.False(map.ShowsSurface);
			Assert.Equal(0, view.Analysis.SurfaceRuns);
			map.ShowWaterSurface = false;

			// Marks loaded in the Depth tool this session are offered on the map.
			ui.Services.HighWaterMarks.Set(Marks(), "marks.csv");
			ui.Pump();
			Assert.True(map.CanShowWaterSurface);
			Assert.True(toggle.IsEffectivelyVisible);
			Assert.Equal("9 marks from marks.csv", map.WaterSurfaceSource);
			toggle.IsChecked = true;
			Analyse(ui, view);
			Assert.True(map.ShowsSurface);
			Assert.True(view.Map.HasRaster(MapAnalysis.SurfaceKey));
			MapSurfaceResult surface = view.Analysis.Surface;
			Assert.Equal("NAVD88", surface.DatumName);
			Assert.Equal(9, surface.MarksUsed);
			Assert.Equal("Water surface (ft, NAVD88)", map.SurfaceLegendTitle);
			Assert.Equal(MapAnalysis.SurfaceClasses, map.SurfaceLegend.Count);
			Assert.Equal(surface.Edges.Length - 1, map.SurfaceLegend.Count);
			Assert.Equal(MapAnalysis.ClassLabel(surface.Edges, 0), map.SurfaceLegend[0].Label);
			Assert.Equal(MapAnalysis.SurfaceClassKeys[4], map.SurfaceLegend[4].ColourKey);
			// The classes are the grid's own, from the library.
			(double minX, double minY, double maxX, double maxY) = (surface.Overlay.MinX, surface.Overlay.MinY, surface.Overlay.MaxX, surface.Overlay.MaxY);
			(double west, double south) = WebMercator.FromMetres(minX, minY);
			(double east, double north) = WebMercator.FromMetres(maxX, maxY);
			WaterSurfaceGrid grid = DepthSurface.Compute(Marks(), west, south, east, north, surface.Overlay.PixelWidth, surface.Overlay.PixelHeight);
			Assert.Equal(ClassBreaks.Compute(grid.ElevationFt, 5, ClassMethod.EqualInterval), surface.Edges);
			Assert.True(Named<StackPanel>(ui, "SurfaceLegendPanel").IsEffectivelyVisible);
			ui.Snapshot("56q-map-water-surface");

			// Another database: the marks go, and so does the surface.
			ui.Services.HighWaterMarks.Clear();
			Analyse(ui, view);
			Assert.False(map.CanShowWaterSurface);
			Assert.False(view.Map.HasRaster(MapAnalysis.SurfaceKey));
			Assert.Empty(map.SurfaceLegend);
		}
	}

	[AvaloniaFact]
	public void TheDepthTool_KeepsItsMarks_ForTheMap()
	{
		using UiSession ui = new UiSession();
		string file = Path.Combine(Path.GetTempPath(), "sde-marks-" + Guid.NewGuid().ToString("N") + ".csv");
		File.WriteAllText(file, "id,latitude,longitude,elevation_ft,datum\nA,39.1000,-84.5160,487.3,NAVD88\nB,39.1040,-84.5135,488.4,NAVD88\n");
		try
		{
			ui.Dialogs.Answers.Enqueue(new[] { file });
			Sde.App.ViewModels.Sheets.DepthSheetViewModel sheet = new Sde.App.ViewModels.Sheets.DepthSheetViewModel(ui.Services, Array.Empty<Guid>());
			sheet.ChooseMarksFileCommand.Execute(null);
			ui.Pump();
			Assert.Equal(2, ui.Services.HighWaterMarks.Marks.Count);
			Assert.True(ui.Main.Map.CanShowWaterSurface);
		}
		finally
		{
			File.Delete(file);
		}
	}

	[AvaloniaFact]
	public void APrintFromTheDarkTheme_DrawsTheHeatMapInTheLightColours()
	{
		(UiSession ui, MapView view) = Streets(ThemeVariant.Dark);
		using (ui)
		{
			MapViewModel map = ui.Main.Map;
			map.Basemap = Basemaps.None;
			map.Display = StructureDisplay.Heatmap;
			Analyse(ui, view);
			MapRasterOverlay onScreen = Assert.Single(view.Map.Rasters);
			MapRasterOverlay printed = Assert.Single(map.PrintScene().Rasters);
			Assert.Equal(MapAnalysis.HeatKey, printed.Key);
			Assert.Equal((onScreen.MinX, onScreen.MaxX, onScreen.PixelWidth), (printed.MinX, printed.MaxX, printed.PixelWidth));
			// Same view, different colours: the dark ramp on screen, the light ramp on paper.
			Assert.NotEqual(onScreen.Png, printed.Png);
		}
	}

	[AvaloniaFact]
	public async Task ThePrintedMap_HasTheHeatMapAndItsLegend()
	{
		(UiSession ui, MapView view) = Streets();
		using (ui)
		{
			MapViewModel map = ui.Main.Map;
			map.Basemap = Basemaps.None;
			map.Display = StructureDisplay.Heatmap;
			Analyse(ui, view);
			MapPrintScene scene = map.PrintScene();
			Assert.True(scene.MarkersHidden);
			MapRasterOverlay heat = Assert.Single(scene.Rasters);
			Assert.Equal(MapAnalysis.HeatKey, heat.Key);
			Assert.StartsWith("Heat map of structures", scene.Legend[0].Title);

			(double w, double h) = MapPrintReport.MapFrame(landscape: true);
			MapPrintImage image = await MapPrinter.RenderAsync(scene, map.Tiles, w, h, TimeSpan.FromSeconds(5), CancellationToken.None);
			using SKBitmap bitmap = SKBitmap.Decode(image.Png);
			// Over the worst street the paper is coloured by the heat map, not left white.
			MapPointViewModel alder = map.Visible.Where(p => p.Address.Contains("Alder", StringComparison.Ordinal)).OrderByDescending(p => p.Row.PercentDamage ?? 0).First();
			(double cx, double cy, double resolution) = view.Map.ViewInMetres;
			(double rx, double ry) = WebMercator.ToMetres(alder.Longitude, alder.Latitude);
			double perPixel = Math.Max(scene.ScreenWidth * resolution / (w * 96), scene.ScreenHeight * resolution / (h * 96)) / (MapPrinter.Dpi / 96.0);
			SKColor pixel = bitmap.GetPixel((int)Math.Round(image.PixelWidth / 2.0 + (rx - cx) / perPixel), (int)Math.Round(image.PixelHeight / 2.0 - (ry - cy) / perPixel));
			Assert.True(pixel.Red < 250 || pixel.Green < 250 || pixel.Blue < 250, pixel.ToString());

			// Clusters print too, in the light theme's colours.
			map.Display = StructureDisplay.Clusters;
			Analyse(ui, view);
			MapPrintScene clustered = map.PrintScene();
			Assert.NotNull(clustered.Clusters);
			Assert.Empty(clustered.Rasters);
			Assert.Equal("Structures in clusters, by worst result", clustered.Legend[0].Title);
			Assert.IsType<Avalonia.Media.Immutable.ImmutableSolidColorBrush>(clustered.Clusters[0].Fill);
		}
	}

	[AvaloniaFact]
	public void TheAttributeDrawer_OpensAlongTheBottom_DragsTaller_AndCloses()
	{
		(UiSession ui, MapView view) = Streets();
		using (ui)
		{
			MapViewModel map = ui.Main.Map;
			map.ShowTableCommand.Execute(map.StructuresLayer);
			ui.Pump();
			Border drawer = Named<Border>(ui, "TableDrawer");
			Assert.True(drawer.IsEffectivelyVisible);
			Assert.Equal(MapViewModel.DefaultTableHeight, drawer.Bounds.Height, 1);
			// Over the map, not beside it: the map keeps its size.
			Assert.Equal(Named<Control>(ui, "MapArea").Bounds.Height, view.Map.Bounds.Height, 1);
			// The zoom card sits above the drawer.
			Border zoom = Named<Border>(ui, "ZoomCard");
			Assert.True(zoom.TranslatePoint(new Point(0, zoom.Bounds.Height), view)!.Value.Y <= drawer.TranslatePoint(new Point(0, 0), view)!.Value.Y);

			ResizeHandle handle = Named<ResizeHandle>(ui, "TableResizer");
			Assert.True(handle.IsVertical);
			handle.Drag(-80);
			ui.Pump();
			Assert.Equal(MapViewModel.DefaultTableHeight + 80, map.TableHeight, 1);
			handle.Drag(-5000);
			Assert.Equal(MapViewModel.MaxTableHeight, map.TableHeight, 1);
			map.ResetTableHeightCommand.Execute(null);
			map.TableHeight = MapViewModel.DefaultTableHeight + 60;
			ui.Pump();
			ui.Snapshot("56r-map-attribute-drawer");

			map.CloseTableCommand.Execute(null);
			ui.Pump();
			Assert.False(drawer.IsEffectivelyVisible);
		}
	}

	[AvaloniaFact]
	public void TheEngineAndTheAnalysis_StopWhenThePageCloses()
	{
		(UiSession ui, MapView view) = Streets();
		using (ui)
		{
			ui.Main.Map.Display = StructureDisplay.Heatmap;
			Analyse(ui, view);
			MapCanvas map = view.Map;
			view.Map.ZoomBy(1);   // work waiting when the page closes
			ui.Main.ShowCommand.Execute(Page.Dashboard);
			ui.Pump();
			Assert.True(map.IsDisposed);
			Assert.True(map.RedrawLoopUnhooked);
			Assert.False(view.Analysis.IsBusy);
			Thread.Sleep(400);
			ui.Pump();
			Assert.True(map.IsDisposed);
		}
	}

	[AvaloniaFact]
	public void TheClusterCount_ReadsOnEveryClusterColour_InBothThemes()
	{
		using UiSession ui = new UiSession();
		foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
		{
			Application.Current!.RequestedThemeVariant = theme;
			ui.Pump();
			IBrush light = ThemeBrush("MapClusterInkLight");
			IBrush dark = ThemeBrush("MapClusterInkDark");
			foreach (DeterminationClass c in Enum.GetValues<DeterminationClass>())
			{
				IBrush fill = ThemeBrush(MapAnalysis.DotKey(c));
				IBrush ink = MapAnalysis.InkFor(fill, light, dark);
				double ratio = MapAnalysis.Contrast(((ISolidColorBrush)fill).Color, ((ISolidColorBrush)ink).Color);
				// The count is bold; the purple of "undetermined" is the hardest at 4.47:1.
				Assert.True(ratio >= 4.4, $"{c} in {theme}: {ratio:0.00}");
			}
		}
		Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
	}
}
