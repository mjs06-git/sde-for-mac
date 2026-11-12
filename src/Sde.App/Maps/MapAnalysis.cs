using Avalonia.Media;
using SkiaSharp;
using Sde.Core.Assessments;
using Sde.Core.Depth;
using Sde.Gis.Analysis;

namespace Sde.App.Maps;

/// <summary>How the Map page draws the structures: a dot each, clusters of nearby dots, or a heat map.</summary>
public enum StructureDisplay
{
	/// <summary>A dot for each structure, in its result's colour (or its style's).</summary>
	Markers,

	/// <summary>Nearby structures drawn as one circle with their number, coloured by the worst result among them.</summary>
	Clusters,

	/// <summary>A heat map of where the damage is (or where the structures are), over the basemap.</summary>
	Heatmap,
}

/// <summary>A structure as the analysis layers see it: where it is, its percent damaged (NaN when none) and its result class.</summary>
public readonly record struct AnalysisPoint(Guid Id, double Latitude, double Longitude, double PercentDamaged, DeterminationClass Class);

/// <summary>
/// One cluster as the map draws it: its centre in Web Mercator metres, how many structures it holds, its fill (the
/// worst result's colour) and the colour of the count, and the area its members cover (a click zooms to it). A
/// cluster of one is drawn as that structure's own dot, so <see cref="Single"/> names it.
/// </summary>
public sealed record MapClusterMark(double X, double Y, int Count, IBrush Fill, IBrush Ink, MapExtent Members, Guid? Single = null)
{
	/// <summary>The worst result among the members, which gives the fill (so print can colour it in the light theme).</summary>
	public DeterminationClass Worst { get; init; }
}

/// <summary>A picture laid over the basemap (the heat map, the water surface): PNG bytes and the Web Mercator rectangle it covers.</summary>
public sealed class MapRasterOverlay
{
	/// <summary>Which analysis layer it is (<see cref="MapAnalysis.HeatKey"/> or <see cref="MapAnalysis.SurfaceKey"/>).</summary>
	public string Key { get; init; }

	public byte[] Png { get; init; }

	public int PixelWidth { get; init; }

	public int PixelHeight { get; init; }

	/// <summary>The rectangle covered, in Web Mercator metres.</summary>
	public double MinX { get; init; }

	public double MinY { get; init; }

	public double MaxX { get; init; }

	public double MaxY { get; init; }

	/// <summary>0 to 1.</summary>
	public double Opacity { get; init; } = 1;
}

/// <summary>A water-surface picture with what its legend needs: the class edges in feet, the datum, and the marks used.</summary>
public sealed class MapSurfaceResult
{
	/// <summary>The picture, or null when no cell in view is near a mark.</summary>
	public MapRasterOverlay Overlay { get; init; }

	/// <summary>The edges of the legend classes, lowest first (n classes have n + 1 edges); empty when there is no data in view.</summary>
	public double[] Edges { get; init; } = Array.Empty<double>();

	/// <summary>The vertical datum the elevations are in, as the marks name it; null when they don't say.</summary>
	public string DatumName { get; init; }

	public int MarksUsed { get; init; }

	public int MarksLeftOut { get; init; }
}

/// <summary>
/// The Map page's analysis layers, worked out off the UI thread from plain data: clusters of structures
/// (<see cref="MarkerClusterer"/>), the heat map (<see cref="HeatmapRaster"/>) and the water surface from high-water
/// marks (<see cref="DepthSurface"/>). Nothing here knows the map engine; MapCanvas draws the results and the printer
/// draws the same results on paper. The sizes in pixels and the heat ramp's stop positions are kept here, in one place.
/// </summary>
public static class MapAnalysis
{
	/// <summary>Structures closer than this on screen (in pixels) join one cluster.</summary>
	public const double ClusterRadiusPixels = 46;

	/// <summary>How far a structure's glow reaches on the heat map, in screen pixels.</summary>
	public const int HeatRadiusPixels = 34;

	/// <summary>The heat map is worked out on a grid this many screen pixels per cell, then smoothed when drawn.</summary>
	public const int HeatCellPixels = 2;

	/// <summary>The water surface is estimated once per cell of this many screen pixels.</summary>
	public const int SurfaceCellPixels = 4;

	/// <summary>How many classes the water surface's legend has.</summary>
	public const int SurfaceClasses = 5;

	/// <summary>Where along the heat ramp (0 coolest, 1 hottest) the four MapHeatStop colours sit.</summary>
	public static readonly double[] HeatStopPositions = { 0, 0.3, 0.62, 1 };

	/// <summary>The theme colours of the heat ramp's stops, coolest first (Tokens.axaml).</summary>
	public static readonly string[] HeatStopKeys = { "MapHeatStop0Color", "MapHeatStop1Color", "MapHeatStop2Color", "MapHeatStop3Color" };

	/// <summary>The theme brushes of the water surface's classes, lowest first (Tokens.axaml).</summary>
	public static readonly string[] SurfaceClassKeys = { "MapSurfaceClass0", "MapSurfaceClass1", "MapSurfaceClass2", "MapSurfaceClass3", "MapSurfaceClass4" };

	/// <summary>The heat map's layer key.</summary>
	public const string HeatKey = "analysis:heat";

	/// <summary>The water surface's layer key.</summary>
	public const string SurfaceKey = "analysis:surface";

	/// <summary>The theme brush of a result class's dot (Status...Dot, the bright dot colours).</summary>
	public static string DotKey(DeterminationClass c) => "Status" + c + "Dot";

	/// <summary>How bad a result is, for a cluster's colour: substantial, then near the threshold, then not substantial, then undetermined and incomplete.</summary>
	public static int Severity(DeterminationClass c) => c switch
	{
		DeterminationClass.Substantial => 4,
		DeterminationClass.Close => 3,
		DeterminationClass.NotSubstantial => 2,
		DeterminationClass.Undetermined => 1,
		_ => 0,
	};

	/// <summary>The worst result among a cluster's members (their categories are result class names).</summary>
	public static DeterminationClass WorstClass(MarkerCluster cluster)
	{
		DeterminationClass worst = DeterminationClass.Incomplete;
		foreach (string category in cluster.CategoryCounts.Keys)
		{
			if (Enum.TryParse(category, out DeterminationClass c) && Severity(c) > Severity(worst))
			{
				worst = c;
			}
		}
		return worst;
	}

	/// <summary>The clusterer's input: each structure in Web Mercator metres, with its percent damaged and its result class.</summary>
	public static List<ClusterPoint> ClusterInput(IReadOnlyList<AnalysisPoint> points)
	{
		List<ClusterPoint> input = new List<ClusterPoint>(points.Count);
		foreach (AnalysisPoint p in points)
		{
			(double x, double y) = WebMercator.ToMetres(p.Longitude, p.Latitude);
			input.Add(new ClusterPoint(p.Id.ToString(), x, y, p.PercentDamaged, p.Class.ToString()));
		}
		return input;
	}

	/// <summary>Clusters the structures for a map showing <paramref name="resolution"/> metres per pixel.</summary>
	public static IReadOnlyList<MarkerCluster> Cluster(IReadOnlyList<AnalysisPoint> points, double resolution) =>
		MarkerClusterer.Cluster(ClusterInput(points), ClusterRadiusPixels * resolution);

	/// <summary>A heat point's weight: the percent damaged (0 to 100; structures without one count nothing), or 1 each.</summary>
	public static double HeatWeight(AnalysisPoint p, bool byDamage) =>
		!byDamage ? 1 : double.IsNaN(p.PercentDamaged) ? 0 : Math.Clamp(p.PercentDamaged, 0, 100);

	/// <summary>
	/// The heat map for a view: a rectangle in Web Mercator metres shown at <paramref name="screenWidth"/> by
	/// <paramref name="screenHeight"/> pixels. Null when no structure counts.
	/// </summary>
	public static MapRasterOverlay Heat(IReadOnlyList<AnalysisPoint> points, bool byDamage, double minX, double minY, double maxX, double maxY,
		double screenWidth, double screenHeight, ColorStop[] ramp, byte maxAlpha)
	{
		int w = Math.Max(1, (int)Math.Ceiling(screenWidth / HeatCellPixels));
		int h = Math.Max(1, (int)Math.Ceiling(screenHeight / HeatCellPixels));
		List<HeatPoint> heat = new List<HeatPoint>(points.Count);
		foreach (AnalysisPoint p in points)
		{
			double weight = HeatWeight(p, byDamage);
			if (weight > 0)
			{
				(double x, double y) = WebMercator.ToMetres(p.Longitude, p.Latitude);
				heat.Add(new HeatPoint(x, y, weight));
			}
		}
		if (heat.Count == 0 || !(maxX > minX) || !(maxY > minY))
		{
			return null;
		}
		HeatmapGrid grid = HeatmapRaster.Compute(heat, minX, minY, maxX, maxY, w, h, Math.Max(1, HeatRadiusPixels / HeatCellPixels));
		if (grid.MaxRawDensity <= 0)
		{
			return null;
		}
		byte[] rgba = HeatmapRaster.ToRgba(grid, ramp, maxAlpha, premultiplied: true);
		return new MapRasterOverlay { Key = HeatKey, Png = Png(rgba, w, h), PixelWidth = w, PixelHeight = h, MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY };
	}

	/// <summary>
	/// The water surface for a view: estimated once per <see cref="SurfaceCellPixels"/> cell, cut into
	/// <see cref="SurfaceClasses"/> equal classes between the lowest and highest elevation in view, each in its colour.
	/// The cells are evenly spaced in latitude and longitude and the picture is laid on the map's Web Mercator rectangle;
	/// across a town the two differ by much less than a cell.
	/// </summary>
	public static MapSurfaceResult Surface(IReadOnlyList<HighWaterMark> marks, double minX, double minY, double maxX, double maxY,
		double screenWidth, double screenHeight, Color[] classColours, double opacity)
	{
		(double west, double south) = WebMercator.FromMetres(minX, minY);
		(double east, double north) = WebMercator.FromMetres(maxX, maxY);
		int w = Math.Max(1, (int)Math.Ceiling(screenWidth / SurfaceCellPixels));
		int h = Math.Max(1, (int)Math.Ceiling(screenHeight / SurfaceCellPixels));
		if (marks == null || marks.Count == 0 || !(east > west) || !(north > south))
		{
			return new MapSurfaceResult();
		}
		WaterSurfaceGrid grid = DepthSurface.Compute(marks, west, south, east, north, w, h);
		double[] edges = ClassBreaks.Compute(grid.ElevationFt, SurfaceClasses, ClassMethod.EqualInterval);
		if (grid.CellsWithData == 0 || edges.Length < 2)
		{
			return new MapSurfaceResult { DatumName = grid.DatumName, MarksUsed = grid.MarksUsed, MarksLeftOut = grid.MarksLeftOut };
		}
		byte[] rgba = new byte[w * h * 4];
		for (int i = 0; i < grid.ElevationFt.Length; i++)
		{
			int c = ClassBreaks.ClassOf(grid.ElevationFt[i], edges);
			if (c < 0)
			{
				continue;
			}
			Color colour = classColours[Math.Min(c, classColours.Length - 1)];
			int o = i * 4;
			rgba[o] = colour.R;
			rgba[o + 1] = colour.G;
			rgba[o + 2] = colour.B;
			rgba[o + 3] = 255;
		}
		return new MapSurfaceResult
		{
			Overlay = new MapRasterOverlay { Key = SurfaceKey, Png = Png(rgba, w, h), PixelWidth = w, PixelHeight = h, MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY, Opacity = opacity },
			Edges = edges,
			DatumName = grid.DatumName,
			MarksUsed = grid.MarksUsed,
			MarksLeftOut = grid.MarksLeftOut,
		};
	}

	/// <summary>"580.0 to 582.5" for a class of the water surface's legend.</summary>
	public static string ClassLabel(double[] edges, int i) =>
		edges[i].ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " to " + edges[i + 1].ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

	/// <summary>The water surface's legend title: what it is, in feet, and in which datum (it is an elevation, not a depth).</summary>
	public static string SurfaceTitle(string datumName) => $"Water surface (ft, {(string.IsNullOrWhiteSpace(datumName) ? "datum not given" : datumName.Trim())})";

	/// <summary>Encodes premultiplied RGBA pixels as a PNG, for the map engine's raster layer and for print.</summary>
	public static byte[] Png(byte[] rgba, int width, int height)
	{
		SKImageInfo info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
		using SKBitmap bitmap = new SKBitmap(info);
		System.Runtime.InteropServices.Marshal.Copy(rgba, 0, bitmap.GetPixels(), Math.Min(rgba.Length, info.BytesSize));
		using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
		return data.ToArray();
	}

	/// <summary>The cluster count's colour: whichever of the two inks has more contrast with the circle's fill.</summary>
	public static IBrush InkFor(IBrush fill, IBrush lightInk, IBrush darkInk)
	{
		Color f = fill is ISolidColorBrush s ? s.Color : Colors.Gray;
		Color l = lightInk is ISolidColorBrush ls ? ls.Color : Colors.White;
		Color d = darkInk is ISolidColorBrush ds ? ds.Color : Colors.Black;
		return Contrast(f, l) >= Contrast(f, d) ? lightInk : darkInk;
	}

	/// <summary>The WCAG contrast ratio of two colours (1 to 21).</summary>
	public static double Contrast(Color a, Color b)
	{
		double la = Luminance(a);
		double lb = Luminance(b);
		return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
	}

	private static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

	private static double Channel(byte v)
	{
		double s = v / 255.0;
		return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
	}
}
