using Sde.Core.Depth;
using Sde.Core.Geo;

namespace Sde.Gis.Analysis;

/// <summary>
/// A grid of estimated water-surface elevations. This is the height of the water above the marks' vertical datum (for
/// example NAVD88), in feet. It is NOT a depth: depth is the water surface minus the ground, and no ground elevation
/// is used here. Cells with no high-water mark within reach hold <see cref="NoData"/> (NaN).
/// </summary>
public sealed class WaterSurfaceGrid
{
	/// <summary>The value of a cell with no estimate.</summary>
	public static readonly float NoData = float.NaN;

	public WaterSurfaceGrid(int width, int height, double west, double south, double east, double north, float[] elevationFt,
		EstimateConfidence[] confidence, string datumName, int marksUsed, int marksLeftOut)
	{
		Width = width;
		Height = height;
		West = west;
		South = south;
		East = east;
		North = north;
		ElevationFt = elevationFt;
		Confidence = confidence;
		DatumName = datumName;
		MarksUsed = marksUsed;
		MarksLeftOut = marksLeftOut;
		float min = float.NaN;
		float max = float.NaN;
		int count = 0;
		foreach (float v in elevationFt)
		{
			if (float.IsNaN(v))
			{
				continue;
			}
			count++;
			min = float.IsNaN(min) ? v : Math.Min(min, v);
			max = float.IsNaN(max) ? v : Math.Max(max, v);
		}
		MinFt = min;
		MaxFt = max;
		CellsWithData = count;
	}

	public int Width { get; }

	public int Height { get; }

	/// <summary>The extent in decimal degrees: longitudes west and east, latitudes south and north.</summary>
	public double West { get; }

	public double South { get; }

	public double East { get; }

	public double North { get; }

	/// <summary>
	/// Water-surface elevation in feet above <see cref="DatumName"/> (not depth), row by row, the first row being the
	/// north edge; each value is for the centre of its cell. <see cref="NoData"/> where no mark is within reach.
	/// </summary>
	public float[] ElevationFt { get; }

	/// <summary>
	/// How far to trust each cell, with the same meaning as for a single structure's estimate
	/// (<see cref="HighWaterMarks.Estimate"/>); <see cref="EstimateConfidence.None"/> for cells with no data.
	/// </summary>
	public EstimateConfidence[] Confidence { get; }

	/// <summary>The vertical datum of the marks the surface is built from, as the first of them wrote it; null when they don't say.</summary>
	public string DatumName { get; }

	/// <summary>The lowest elevation on the grid, in feet; NaN when there is no data.</summary>
	public float MinFt { get; }

	/// <summary>The highest elevation on the grid, in feet; NaN when there is no data.</summary>
	public float MaxFt { get; }

	/// <summary>How many cells have an estimate.</summary>
	public int CellsWithData { get; }

	/// <summary>How many marks went into the surface.</summary>
	public int MarksUsed { get; }

	/// <summary>How many usable marks were left out because they use a different vertical datum from the rest.</summary>
	public int MarksLeftOut { get; }

	/// <summary>The elevation at a cell (column, row from the north).</summary>
	public float this[int column, int row] => ElevationFt[row * Width + column];
}

/// <summary>
/// Builds a water-surface elevation grid from high-water marks, cell by cell, with exactly the method and defaults used
/// for a single structure: <see cref="HighWaterMarks.Estimate"/> (the nearest 5 marks within 1,000 m, weighted by
/// quality divided by distance squared, distances on the sphere in metres, elevations in feet). So a cell centre and a
/// structure at the same place get the same number.
/// </summary>
public static class DepthSurface
{
	/// <summary>
	/// Estimates the water-surface elevation for every cell of an extent. The extent is in decimal degrees (the
	/// marks' own latitude and longitude); the app converts from its map projection. Cells farther than the options'
	/// radius (1,000 m by default) from every mark are <see cref="WaterSurfaceGrid.NoData"/>.
	/// Marks in different vertical datums are not mixed: the datum with the most usable marks is used (ties go to the
	/// datum named first in alphabetical order), and marks in other datums are counted in
	/// <see cref="WaterSurfaceGrid.MarksLeftOut"/>; marks that don't name a datum are used only when none does.
	/// The result is the same whatever order the marks come in.
	/// </summary>
	/// <param name="marks">The high-water marks.</param>
	/// <param name="west">West edge, longitude in degrees.</param>
	/// <param name="south">South edge, latitude in degrees.</param>
	/// <param name="east">East edge.</param>
	/// <param name="north">North edge.</param>
	/// <param name="width">Columns (1 or more).</param>
	/// <param name="height">Rows (1 or more).</param>
	/// <param name="options">The same options as the per-structure estimate; null for its defaults.</param>
	public static WaterSurfaceGrid Compute(IEnumerable<HighWaterMark> marks, double west, double south, double east, double north, int width, int height, HighWaterMarkOptions options = null)
	{
		if (width < 1 || height < 1)
		{
			throw new ArgumentOutOfRangeException(nameof(width), "The grid must be at least one cell wide and high.");
		}
		if (!(east > west) || !(north > south) || !double.IsFinite(west) || !double.IsFinite(east) || !double.IsFinite(south) || !double.IsFinite(north))
		{
			throw new ArgumentException("The extent must have a positive width and height.");
		}
		HighWaterMarkOptions o = options ?? new HighWaterMarkOptions();

		// Usable marks, in a fixed order so the result never depends on the input order.
		List<HighWaterMark> usable = (marks ?? Enumerable.Empty<HighWaterMark>())
			.Where(m => m != null && IsPosition(m.Latitude, m.Longitude) && (m.Quality == null || m.Quality <= o.WorstQuality))
			.OrderBy(m => m.Latitude).ThenBy(m => m.Longitude).ThenBy(m => m.ElevationFt).ThenBy(m => m.Quality ?? 0)
			.ThenBy(m => m.Id, StringComparer.Ordinal).ToList();

		// One datum for the whole surface.
		string datumName = null;
		List<HighWaterMark> chosen = usable;
		List<HighWaterMark> named = usable.Where(m => m.Datum != VerticalDatum.Unknown).ToList();
		if (named.Count > 0)
		{
			HighWaterMark lead = named
				.GroupBy(m => m.Datum == VerticalDatum.Other ? "other:" + m.DatumName.Trim().ToUpperInvariant() : m.Datum.ToString())
				.OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
				.First().First();
			datumName = lead.DatumName;
			chosen = named.Where(m => !VerticalDatums.Differ(m.DatumName, datumName)).ToList();
		}
		int leftOut = usable.Count - chosen.Count;

		// Only marks that can reach some cell matter: those within the radius of the extent.
		List<HighWaterMark> near = chosen.Where(m =>
		{
			double lat = Math.Clamp(m.Latitude, south, north);
			double lon = Math.Clamp(m.Longitude, west, east);
			return GeoMath.Meters(m.Latitude, m.Longitude, lat, lon) <= o.RadiusMeters;
		}).ToList();

		float[] elevation = new float[width * height];
		EstimateConfidence[] confidence = new EstimateConfidence[width * height];
		Array.Fill(elevation, WaterSurfaceGrid.NoData);
		if (near.Count > 0)
		{
			double dx = (east - west) / width;
			double dy = (north - south) / height;
			Parallel.For(0, height, row =>
			{
				double lat = north - (row + 0.5) * dy;
				for (int col = 0; col < width; col++)
				{
					WaterSurfaceEstimate e = HighWaterMarks.Estimate(near, lat, west + (col + 0.5) * dx, o);
					if (e.ElevationFt is decimal ft)
					{
						elevation[row * width + col] = (float)ft;
						confidence[row * width + col] = e.Confidence;
					}
				}
			});
		}
		return new WaterSurfaceGrid(width, height, west, south, east, north, elevation, confidence, datumName, near.Count, leftOut);
	}

	/// <summary>The same test as for a single structure's marks: in range and not the 0, 0 placeholder (see <see cref="GeoMath.IsPosition"/>).</summary>
	private static bool IsPosition(double latitude, double longitude) =>
		double.IsFinite(latitude) && double.IsFinite(longitude) && Math.Abs(latitude) <= 90 && Math.Abs(longitude) <= 180 && !(latitude == 0 && longitude == 0);
}

/// <summary>How to cut a range of values into legend classes.</summary>
public enum ClassMethod
{
	/// <summary>Classes of equal width between the lowest and highest value.</summary>
	EqualInterval,

	/// <summary>Classes holding about the same number of cells each.</summary>
	Quantile,
}

/// <summary>Class breaks for a legend of a grid of numbers, without contour lines.</summary>
public static class ClassBreaks
{
	/// <summary>
	/// The edges of up to <paramref name="classes"/> classes: the lowest value, each break, the highest value, so n
	/// classes give n + 1 edges, in increasing order. NaN values (no data) are ignored. Duplicate edges (all values
	/// the same, or a quantile break repeated) are merged, so fewer classes come back; with no values the array is
	/// empty, and with all values the same it is that value twice (one class).
	/// </summary>
	public static double[] Compute(IEnumerable<float> values, int classes, ClassMethod method)
	{
		if (classes < 1)
		{
			throw new ArgumentOutOfRangeException(nameof(classes), "There must be at least one class.");
		}
		float[] sorted = (values ?? Enumerable.Empty<float>()).Where(v => !float.IsNaN(v) && !float.IsInfinity(v)).ToArray();
		if (sorted.Length == 0)
		{
			return Array.Empty<double>();
		}
		Array.Sort(sorted);
		double lo = sorted[0];
		double hi = sorted[^1];
		if (lo == hi)
		{
			return new[] { lo, hi };
		}
		List<double> edges = new List<double> { lo };
		for (int i = 1; i < classes; i++)
		{
			double edge = method == ClassMethod.EqualInterval
				? lo + (hi - lo) * i / classes
				: Quantile(sorted, (double)i / classes);
			if (edge > edges[^1] && edge < hi)
			{
				edges.Add(edge);
			}
		}
		edges.Add(hi);
		return edges.ToArray();
	}

	/// <summary>The class (0-based) a value falls in; a value on an edge goes to the higher class, the highest value to the top class. -1 for NaN, no edges or a value outside the edges.</summary>
	public static int ClassOf(double value, double[] edges)
	{
		if (edges == null || edges.Length < 2 || double.IsNaN(value) || value < edges[0] || value > edges[^1])
		{
			return -1;
		}
		for (int i = edges.Length - 2; i >= 0; i--)
		{
			if (value >= edges[i])
			{
				return i;
			}
		}
		return 0;
	}

	/// <summary>The value at fraction p (0 to 1) of a sorted array, interpolating between neighbours (the usual "type 7" quantile).</summary>
	private static double Quantile(float[] sorted, double p)
	{
		double pos = p * (sorted.Length - 1);
		int i = (int)Math.Floor(pos);
		double f = pos - i;
		return i + 1 < sorted.Length ? sorted[i] + (sorted[i + 1] - sorted[i]) * f : sorted[i];
	}
}
