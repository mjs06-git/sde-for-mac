using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Maps;
using Sde.Core.Depth;

namespace Sde.App.ViewModels;

/// <summary>One line of the map's legend card: a colour (a theme brush's name) and what it means.</summary>
public sealed record MapLegendLine(string Label, string ColourKey, string Count = null)
{
	public bool HasCount => Count != null;
}

/// <summary>
/// How the structures are shown (dots, clusters or a heat map), the water surface from high-water marks, the legend
/// card over the map, the inspector beside it and the attribute drawer's height. The analysis itself is worked out by
/// the page (Maps/MapAnalysisRunner) from <see cref="AnalysisInputs"/>.
/// </summary>
public sealed partial class MapViewModel
{
	private bool _watchingMarks;

	/// <summary>How the structures are drawn: Markers (a dot each), Clusters or Heat map.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ShowAsIndex), nameof(IsMarkers), nameof(IsClusters), nameof(IsHeatmap), nameof(LegendTitle), nameof(ShowsResultLegend))]
	public partial StructureDisplay Display { get; set; }

	/// <summary>The "Show as" control: 0 markers, 1 clusters, 2 heat map.</summary>
	public int ShowAsIndex
	{
		get => (int)Display;
		set => Display = (StructureDisplay)Math.Clamp(value, 0, 2);
	}

	public bool IsMarkers => Display == StructureDisplay.Markers;

	public bool IsClusters => Display == StructureDisplay.Clusters;

	public bool IsHeatmap => Display == StructureDisplay.Heatmap;

	/// <summary>True to weight the heat map by percent damaged; false counts each structure once.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HeatWeightIndex), nameof(HeatLegendNote))]
	public partial bool HeatByDamage { get; set; } = true;

	/// <summary>The heat map's weighting control: 0 by percent damaged, 1 one per structure.</summary>
	public int HeatWeightIndex
	{
		get => HeatByDamage ? 0 : 1;
		set => HeatByDamage = value == 0;
	}

	/// <summary>What the heat map counts, under its ramp in the legend.</summary>
	public string HeatLegendNote => HeatByDamage ? "Weighted by percent damaged" : "One per structure";

	/// <summary>The legend card's title for the way the structures are shown.</summary>
	public string LegendTitle => Display switch
	{
		StructureDisplay.Clusters => "Clusters, by worst result",
		StructureDisplay.Heatmap => "Heat map",
		_ => ColoursByResult ? "Structures" : StructureLegendTitle ?? "Structures",
	};

	/// <summary>True when the legend lists the result classes (dots by result, or clusters).</summary>
	public bool ShowsResultLegend => IsClusters || IsMarkers && ColoursByResult;

	/// <summary>The legend's lines for the result classes that are shown and on the map, in the dot colours.</summary>
	public IReadOnlyList<MapLegendLine> ResultLegend => Classes
		.Where(c => c.IsOn && c.Count > 0)
		.Select(c => new MapLegendLine(c.Label, IsClusters ? MapAnalysis.DotKey(c.Class) : c.ColourKey, c.Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)))
		.ToList();

	/// <summary>Whether the legend card is open (it folds down to its title).</summary>
	[ObservableProperty]
	public partial bool IsLegendOpen { get; set; } = true;

	[RelayCommand]
	private void ToggleLegend() => IsLegendOpen = !IsLegendOpen;

	// ───────────── The water surface ─────────────

	/// <summary>True to draw the water surface estimated from the high-water marks loaded this session.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ShowsSurface))]
	public partial bool ShowWaterSurface { get; set; }

	/// <summary>True when high-water marks have been loaded this session (in the Depth tool), so the surface can be drawn.</summary>
	public bool CanShowWaterSurface => _app.HighWaterMarks.HasMarks;

	/// <summary>True while the surface is on and there are marks to draw it from.</summary>
	public bool ShowsSurface => ShowWaterSurface && CanShowWaterSurface;

	/// <summary>Where the marks came from, under the surface's switch ("42 marks from marks.csv").</summary>
	public string WaterSurfaceSource => CanShowWaterSurface
		? $"{_app.HighWaterMarks.Marks.Count:N0} mark{(_app.HighWaterMarks.Marks.Count == 1 ? "" : "s")} from {_app.HighWaterMarks.Source ?? "the Depth tool"}"
		: null;

	/// <summary>The surface's legend title ("Water surface (ft, NAVD88)"): an elevation in the marks' datum, never a depth.</summary>
	[ObservableProperty]
	public partial string SurfaceLegendTitle { get; set; } = MapAnalysis.SurfaceTitle(null);

	/// <summary>The surface's legend classes for the area in view, lowest first.</summary>
	public ObservableCollection<MapLegendLine> SurfaceLegend { get; } = new();

	/// <summary>True when the surface has classes to show (some of the area in view is near a mark).</summary>
	public bool HasSurfaceLegend => SurfaceLegend.Count > 0;

	/// <summary>One grey line when the surface is on but nothing in view is near a mark.</summary>
	public bool SurfaceOutOfView => ShowsSurface && SurfaceLegend.Count == 0;

	/// <summary>The page worked out the surface for the area in view: its classes go in the legend.</summary>
	public void ShowSurfaceClasses(MapSurfaceResult result)
	{
		SurfaceLegend.Clear();
		if (result != null)
		{
			SurfaceLegendTitle = MapAnalysis.SurfaceTitle(result.DatumName);
			for (int i = 0; i + 1 < result.Edges.Length; i++)
			{
				SurfaceLegend.Add(new MapLegendLine(MapAnalysis.ClassLabel(result.Edges, i), MapAnalysis.SurfaceClassKeys[Math.Min(i, MapAnalysis.SurfaceClassKeys.Length - 1)]));
			}
		}
		OnPropertyChanged(nameof(HasSurfaceLegend));
		OnPropertyChanged(nameof(SurfaceOutOfView));
	}

	/// <summary>The analysis layers should be worked out again (the mode, the weighting, the surface or the structures changed).</summary>
	public event Action AnalysisChanged;

	/// <summary>What the page works the analysis layers out from now.</summary>
	public MapAnalysisInputs AnalysisInputs()
	{
		WatchMarks();
		bool shown = StructuresLayer?.IsVisible ?? true;
		List<AnalysisPoint> points = new List<AnalysisPoint>(Visible.Count);
		if (shown && Display != StructureDisplay.Markers)
		{
			foreach (MapPointViewModel p in Visible)
			{
				points.Add(new AnalysisPoint(p.Id, p.Latitude, p.Longitude, p.Row.PercentDamage is decimal pd ? (double)pd : double.NaN, p.Class));
			}
		}
		return new MapAnalysisInputs
		{
			Display = shown ? Display : StructureDisplay.Markers,
			Points = points,
			HeatByDamage = HeatByDamage,
			SurfaceMarks = ShowsSurface ? _app.HighWaterMarks.Marks : null,
		};
	}

	/// <summary>Follows the marks loaded in the Depth tool and the inspector's setting (once, the first time the page asks).</summary>
	public void WatchMarks()
	{
		if (_watchingMarks)
		{
			return;
		}
		_watchingMarks = true;
		_main.Structures.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(StructuresViewModel.IsInspectorVisible))
			{
				OnPropertyChanged(nameof(IsInspectorVisible));
			}
		};
		_app.HighWaterMarks.Changed += () =>
		{
			OnPropertyChanged(nameof(CanShowWaterSurface));
			OnPropertyChanged(nameof(ShowsSurface));
			OnPropertyChanged(nameof(WaterSurfaceSource));
			OnPropertyChanged(nameof(SurfaceOutOfView));
			if (!CanShowWaterSurface)
			{
				ShowSurfaceClasses(null);
			}
			AnalysisChanged?.Invoke();
		};
	}

	partial void OnDisplayChanged(StructureDisplay value) => AnalysisChanged?.Invoke();

	partial void OnHeatByDamageChanged(bool value) => AnalysisChanged?.Invoke();

	partial void OnShowWaterSurfaceChanged(bool value)
	{
		if (!value)
		{
			ShowSurfaceClasses(null);
		}
		OnPropertyChanged(nameof(SurfaceOutOfView));
		AnalysisChanged?.Invoke();
	}

	/// <summary>The legend's result lines follow the classes ticked and the counts (called when the structures are read or ticked).</summary>
	private void LegendChanged()
	{
		OnPropertyChanged(nameof(ResultLegend));
		OnPropertyChanged(nameof(ShowsResultLegend));
		OnPropertyChanged(nameof(LegendTitle));
	}

	// ───────────── The inspector and the drawer ─────────────

	/// <summary>The window, for the inspector's shared width.</summary>
	public MainViewModel Shell => _main;

	/// <summary>Whether the inspector shows beside the map: one setting with the Structures page's inspector (View › Show Inspector).</summary>
	public bool IsInspectorVisible => _main.Structures.IsInspectorVisible;

	[RelayCommand]
	private void ToggleInspector() => _main.Structures.ToggleInspectorCommand.Execute(null);

	/// <summary>The attribute drawer's height, kept between the MapDrawerMinHeight and MapDrawerMaxHeight tokens.</summary>
	public double TableHeight
	{
		get => _tableHeight;
		set => SetProperty(ref _tableHeight, Math.Clamp(double.IsFinite(value) ? value : DefaultTableHeight, MinTableHeight, MaxTableHeight));
	}

	private double _tableHeight = DefaultTableHeight;

	public static double DefaultTableHeight => Token("MapDrawerHeight", 260);

	public static double MinTableHeight => Token("MapDrawerMinHeight", 140);

	public static double MaxTableHeight => Token("MapDrawerMaxHeight", 620);

	/// <summary>A size token from Styles/Tokens.axaml, or <paramref name="fallback"/> before the app's resources load.</summary>
	private static double Token(string key, double fallback) =>
		Avalonia.Application.Current != null && Avalonia.Application.Current.TryFindResource(key, out object value) && value is double d ? d : fallback;

	/// <summary>A double-click on the drawer's top edge: back to its default height.</summary>
	[RelayCommand]
	private void ResetTableHeight() => TableHeight = DefaultTableHeight;
}
