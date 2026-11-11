using Avalonia.Media;
using Avalonia.Threading;
using Sde.Core.Assessments;
using Sde.Core.Depth;
using Sde.Gis.Analysis;

namespace Sde.App.Maps;

/// <summary>What the analysis layers are worked out from: the display mode, the structures, the heat map's weighting and the marks.</summary>
public sealed class MapAnalysisInputs
{
	public StructureDisplay Display { get; init; }

	/// <summary>The structures shown on the map.</summary>
	public IReadOnlyList<AnalysisPoint> Points { get; init; } = Array.Empty<AnalysisPoint>();

	/// <summary>True to weight the heat map by percent damaged, false for one per structure.</summary>
	public bool HeatByDamage { get; init; } = true;

	/// <summary>The high-water marks to draw the water surface from, or null for no surface.</summary>
	public IReadOnlyList<HighWaterMark> SurfaceMarks { get; init; }

	/// <summary>True when any analysis layer is on (otherwise nothing is ever worked out).</summary>
	public bool AnyOn => Display != StructureDisplay.Markers || SurfaceMarks != null;
}

/// <summary>
/// Keeps a <see cref="MapCanvas"/>'s analysis layers up to date: clusters (again when the zoom changes), the heat map
/// and the water surface (again for the area in view whenever the map stops moving). The work runs on the thread pool,
/// a short while after the last change, and a newer change cancels older work, so panning stays smooth and only the
/// last answer is drawn. Nothing runs while every analysis layer is off. Colours come from the theme on the UI thread.
/// </summary>
public sealed class MapAnalysisRunner
{
	/// <summary>How long the map has to stay still before the layers are worked out again.</summary>
	public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(180);

	private readonly MapCanvas _map;
	private readonly Dispatcher _ui;
	private readonly Func<string, object> _resource;
	private MapAnalysisInputs _inputs = new MapAnalysisInputs();
	private CancellationTokenSource _pending;
	private int _generation;
	private double _clusteredAt = double.NaN;
	private IReadOnlyList<(MarkerCluster Cluster, MapExtent Members)> _clusters;
	private MapRasterOverlay _heat;
	private MapSurfaceResult _surface;
	private bool _stopped;

	/// <param name="map">The map to draw on.</param>
	/// <param name="resource">Looks up a resource (a brush, a colour, a number) in the theme the map shows now.</param>
	public MapAnalysisRunner(MapCanvas map, Func<string, object> resource)
	{
		_map = map;
		_resource = resource;
		_ui = Dispatcher.UIThread;
	}

	/// <summary>True while work is waiting or running.</summary>
	public bool IsBusy { get; private set; }

	/// <summary>How many times each layer has been worked out and drawn (for tests).</summary>
	public int ClusterRuns { get; private set; }

	public int HeatRuns { get; private set; }

	public int SurfaceRuns { get; private set; }

	/// <summary>The clusters drawn now, or null when the structures are not clustered.</summary>
	public IReadOnlyList<MarkerCluster> Clusters => _clusters?.Select(c => c.Cluster).ToList();

	/// <summary>The heat map drawn now, or null.</summary>
	public MapRasterOverlay Heat => _heat;

	/// <summary>The water surface drawn now (with its legend's classes), or null.</summary>
	public MapSurfaceResult Surface => _surface;

	/// <summary>The water surface was worked out again (its legend changes with the area in view); null when it was turned off.</summary>
	public event Action<MapSurfaceResult> SurfaceChanged;

	/// <summary>The inputs changed: what is off is cleared at once, what is on is worked out now.</summary>
	public void Update(MapAnalysisInputs inputs)
	{
		_inputs = inputs ?? new MapAnalysisInputs();
		_stopped = false;
		_clusteredAt = double.NaN;
		if (_inputs.Display != StructureDisplay.Clusters)
		{
			_clusters = null;
		}
		if (_inputs.Display != StructureDisplay.Heatmap)
		{
			_heat = null;
		}
		if (_inputs.SurfaceMarks == null && _surface != null)
		{
			_surface = null;
			SurfaceChanged?.Invoke(null);
		}
		_map.MarkersHidden = _inputs.Display == StructureDisplay.Heatmap;
		Draw();
		Schedule(TimeSpan.Zero);
	}

	/// <summary>The map moved or zoomed: the layers that depend on the view are worked out again once it is still.</summary>
	public void ViewChanged()
	{
		if (_inputs.AnyOn && !_stopped)
		{
			Schedule(Debounce);
		}
	}

	/// <summary>The theme changed: everything shown is coloured again.</summary>
	public void Refresh()
	{
		_clusteredAt = double.NaN;
		if (_inputs.AnyOn && !_stopped)
		{
			Schedule(TimeSpan.Zero);
		}
	}

	/// <summary>Cancels any work (the page closed). <see cref="Update"/> starts again.</summary>
	public void Stop()
	{
		_stopped = true;
		_pending?.Cancel();
		_pending = null;
		IsBusy = false;
	}

	/// <summary>Hands what there is now to the map.</summary>
	private void Draw()
	{
		_map.Clusters = _inputs.Display == StructureDisplay.Clusters && _clusters != null ? Marks(_clusters) : null;
		List<MapRasterOverlay> rasters = new List<MapRasterOverlay>();
		if (_surface?.Overlay != null)
		{
			rasters.Add(_surface.Overlay);
		}
		if (_heat != null)
		{
			rasters.Add(_heat);
		}
		_map.Rasters = rasters;
	}

	/// <summary>The clusters as the map draws them: the worst result's dot colour, and the count in whichever ink reads better on it.</summary>
	private List<MapClusterMark> Marks(IReadOnlyList<(MarkerCluster Cluster, MapExtent Members)> clusters)
	{
		IBrush light = _resource("MapClusterInkLight") as IBrush ?? Brushes.White;
		IBrush dark = _resource("MapClusterInkDark") as IBrush ?? Brushes.Black;
		Dictionary<DeterminationClass, (IBrush Fill, IBrush Ink)> colours = new Dictionary<DeterminationClass, (IBrush, IBrush)>();
		List<MapClusterMark> marks = new List<MapClusterMark>(clusters.Count);
		foreach ((MarkerCluster c, MapExtent members) in clusters)
		{
			DeterminationClass worst = MapAnalysis.WorstClass(c);
			if (!colours.TryGetValue(worst, out (IBrush Fill, IBrush Ink) pair))
			{
				IBrush fill = _resource(MapAnalysis.DotKey(worst)) as IBrush ?? Brushes.Gray;
				pair = (fill, MapAnalysis.InkFor(fill, light, dark));
				colours[worst] = pair;
			}
			Guid? single = c.Count == 1 && Guid.TryParse(c.MemberIds[0], out Guid id) ? id : null;
			marks.Add(new MapClusterMark(c.X, c.Y, c.Count, pair.Fill, pair.Ink, members, single) { Worst = worst });
		}
		return marks;
	}

	private void Schedule(TimeSpan delay)
	{
		_pending?.Cancel();
		_pending = null;
		if (!_inputs.AnyOn || _stopped)
		{
			IsBusy = false;
			return;
		}
		(double cx, double cy, double resolution) = _map.ViewInMetres;
		double width = _map.Bounds.Width;
		double height = _map.Bounds.Height;
		if (width < 10 || height < 10 || !(resolution > 0))
		{
			IsBusy = false;
			return;
		}
		CancellationTokenSource cts = new CancellationTokenSource();
		_pending = cts;
		int generation = ++_generation;
		IsBusy = true;

		// Everything the work needs, read here on the UI thread.
		MapAnalysisInputs inputs = _inputs;
		bool cluster = inputs.Display == StructureDisplay.Clusters && !(Math.Abs(resolution - _clusteredAt) < resolution * 1e-6);
		bool heat = inputs.Display == StructureDisplay.Heatmap;
		bool surface = inputs.SurfaceMarks != null;
		double minX = cx - width / 2 * resolution;
		double maxX = cx + width / 2 * resolution;
		double minY = cy - height / 2 * resolution;
		double maxY = cy + height / 2 * resolution;
		ColorStop[] ramp = HeatRamp();
		byte maxAlpha = (byte)Math.Clamp(_resource("MapHeatMaxAlpha") is double a ? a : 205, 0, 255);
		Color[] classColours = MapAnalysis.SurfaceClassKeys.Select(k => _resource(k) is ISolidColorBrush b ? b.Color : Colors.SteelBlue).ToArray();
		double surfaceOpacity = _resource("MapSurfaceOpacity") is double o ? o : 0.6;
		CancellationToken token = cts.Token;

		_ = Task.Run(async () =>
		{
			try
			{
				if (delay > TimeSpan.Zero)
				{
					await Task.Delay(delay, token).ConfigureAwait(false);
				}
				token.ThrowIfCancellationRequested();
				IReadOnlyList<(MarkerCluster, MapExtent)> clusters = cluster ? ClusterWithExtents(inputs.Points, resolution) : null;
				token.ThrowIfCancellationRequested();
				MapRasterOverlay heatMap = heat ? MapAnalysis.Heat(inputs.Points, inputs.HeatByDamage, minX, minY, maxX, maxY, width, height, ramp, maxAlpha) : null;
				token.ThrowIfCancellationRequested();
				MapSurfaceResult water = surface ? MapAnalysis.Surface(inputs.SurfaceMarks, minX, minY, maxX, maxY, width, height, classColours, surfaceOpacity) : null;
				token.ThrowIfCancellationRequested();
				_ui.Post(() => Finish(generation, inputs, cluster ? resolution : double.NaN, clusters, heat, heatMap, surface, water));
			}
			catch (OperationCanceledException)
			{
				// A newer change took over.
			}
			catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OutOfMemoryException)
			{
				Services.AppLog.Write("Map analysis", ex);
				_ui.Post(() =>
				{
					if (generation == _generation)
					{
						IsBusy = false;
					}
				});
			}
		}, token);
	}

	/// <summary>Draws the answer if it is still the latest one asked for.</summary>
	private void Finish(int generation, MapAnalysisInputs inputs, double clusteredAt, IReadOnlyList<(MarkerCluster, MapExtent)> clusters,
		bool heat, MapRasterOverlay heatMap, bool surface, MapSurfaceResult water)
	{
		if (generation != _generation || !ReferenceEquals(inputs, _inputs) || _stopped)
		{
			return;
		}
		IsBusy = false;
		if (clusters != null)
		{
			_clusters = clusters;
			_clusteredAt = clusteredAt;
			ClusterRuns++;
		}
		if (heat)
		{
			_heat = heatMap;
			HeatRuns++;
		}
		if (surface)
		{
			_surface = water;
			SurfaceRuns++;
		}
		Draw();
		if (surface)
		{
			SurfaceChanged?.Invoke(water);
		}
	}

	/// <summary>The clusters, each with the rectangle its members cover (longitude and latitude).</summary>
	private static IReadOnlyList<(MarkerCluster, MapExtent)> ClusterWithExtents(IReadOnlyList<AnalysisPoint> points, double resolution)
	{
		Dictionary<string, AnalysisPoint> byId = new Dictionary<string, AnalysisPoint>(points.Count);
		foreach (AnalysisPoint p in points)
		{
			byId[p.Id.ToString()] = p;
		}
		List<(MarkerCluster, MapExtent)> result = new List<(MarkerCluster, MapExtent)>();
		foreach (MarkerCluster c in MapAnalysis.Cluster(points, resolution))
		{
			double minLon = double.MaxValue, minLat = double.MaxValue, maxLon = double.MinValue, maxLat = double.MinValue;
			foreach (string id in c.MemberIds)
			{
				if (byId.TryGetValue(id, out AnalysisPoint p))
				{
					minLon = Math.Min(minLon, p.Longitude);
					maxLon = Math.Max(maxLon, p.Longitude);
					minLat = Math.Min(minLat, p.Latitude);
					maxLat = Math.Max(maxLat, p.Latitude);
				}
			}
			result.Add((c, new MapExtent(minLon, minLat, maxLon, maxLat)));
		}
		return result;
	}

	/// <summary>
	/// The heat map shown now, drawn again in the colours of <paramref name="resource"/> (the light theme's, for a print on
	/// white paper). The same view and points as on screen; null when no heat map is shown.
	/// </summary>
	public MapRasterOverlay HeatForPrint(Func<string, object> resource)
	{
		if (_heat == null || _inputs == null || _inputs.Display != StructureDisplay.Heatmap)
		{
			return _heat;
		}
		byte maxAlpha = (byte)Math.Clamp(resource("MapHeatMaxAlpha") is double a ? a : 205, 0, 255);
		return MapAnalysis.Heat(_inputs.Points, _inputs.HeatByDamage, _heat.MinX, _heat.MinY, _heat.MaxX, _heat.MaxY,
			_heat.PixelWidth * MapAnalysis.HeatCellPixels, _heat.PixelHeight * MapAnalysis.HeatCellPixels, HeatRamp(resource), maxAlpha) ?? _heat;
	}

	/// <summary>The heat ramp in the theme's colours (Tokens.axaml), at <see cref="MapAnalysis.HeatStopPositions"/>.</summary>
	private ColorStop[] HeatRamp() => HeatRamp(_resource);

	private static ColorStop[] HeatRamp(Func<string, object> resource)
	{
		ColorStop[] stops = new ColorStop[MapAnalysis.HeatStopKeys.Length];
		for (int i = 0; i < stops.Length; i++)
		{
			Color c = resource(MapAnalysis.HeatStopKeys[i]) is Color found ? found : Colors.SteelBlue;
			stops[i] = new ColorStop(MapAnalysis.HeatStopPositions[i], c.R, c.G, c.B);
		}
		return stops;
	}
}
