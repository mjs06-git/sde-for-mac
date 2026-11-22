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
			int nearestLimit = Math.Max(1, o.NearestCount);
			double radius = o.RadiusMeters;
			double power = o.Power;
			bool noDatumAtAll = datumName == null;
			MarkEntry[] entries = new MarkEntry[near.Count];
			for (int i = 0; i < near.Count; i++)
			{
				HighWaterMark m = near[i];
				entries[i] = new MarkEntry(m.Latitude, m.Longitude, m.ElevationFt, (double)m.ElevationFt, HighWaterMarks.QualityWeight(m.Quality), i);
			}
			double radiusLatDeg = (radius + 1.0) / GeoMath.MetersPerDegreeLatitude;
			Parallel.For(0, height, row =>
			{
				double lat = north - (row + 0.5) * dy;
				List<MarkEntry> rowMarks = new List<MarkEntry>();
				for (int i = 0; i < entries.Length; i++)
				{
					if (Math.Abs(entries[i].Latitude - lat) * GeoMath.MetersPerDegreeLatitude <= radius + 1.0)
					{
						rowMarks.Add(entries[i]);
					}
				}
				if (rowMarks.Count == 0)
				{
					return;
				}
				rowMarks.Sort(static (a, b) => a.Longitude.CompareTo(b.Longitude));
				double cosLat = Math.Cos((Math.Abs(lat) + radiusLatDeg) * Math.PI / 180.0);
				bool useWindow = Math.Abs(lat) + radiusLatDeg < 89.0 && cosLat > 0.01;
				double lonMargin = useWindow ? radiusLatDeg / cosLat + 1e-6 : 360.0;
				if (useWindow && (west - lonMargin < -180.0 || east + lonMargin > 180.0))
				{
					useWindow = false;
				}
				MarkHit[] best = new MarkHit[nearestLimit];
				double[] rawWeights = new double[nearestLimit];
				int lo = 0;
				int hi = 0;
				int countMarks = rowMarks.Count;
				int rowOffset = row * width;
				for (int col = 0; col < width; col++)
				{
					double lon = west + (col + 0.5) * dx;
					int scanStart;
					int scanEnd;
					if (useWindow)
					{
						double minLon = lon - lonMargin;
						double maxLon = lon + lonMargin;
						while (lo < countMarks && rowMarks[lo].Longitude < minLon)
						{
							lo++;
						}
						if (hi < lo)
						{
							hi = lo;
						}
						while (hi < countMarks && rowMarks[hi].Longitude <= maxLon)
						{
							hi++;
						}
						scanStart = lo;
						scanEnd = hi;
					}
					else
					{
						scanStart = 0;
						scanEnd = countMarks;
					}
					int found = 0;
					for (int i = scanStart; i < scanEnd; i++)
					{
						MarkEntry m = rowMarks[i];
						double d = GeoMath.Meters(lat, lon, m.Latitude, m.Longitude);
						if (d > radius)
						{
							continue;
						}
						if (found < nearestLimit)
						{
							int pos = found;
							while (pos > 0 && IsCloser(d, m.Order, best[pos - 1]))
							{
								best[pos] = best[pos - 1];
								pos--;
							}
							best[pos] = new MarkHit(m, d);
							found++;
						}
						else if (IsCloser(d, m.Order, best[nearestLimit - 1]))
						{
							int pos = nearestLimit - 1;
							while (pos > 0 && IsCloser(d, m.Order, best[pos - 1]))
							{
								best[pos] = best[pos - 1];
								pos--;
							}
							best[pos] = new MarkHit(m, d);
						}
					}
					if (found == 0)
					{
						continue;
					}
					double total = 0;
					decimal minElev = best[0].Mark.ElevationFt;
					decimal maxElev = minElev;
					for (int i = 0; i < found; i++)
					{
						double dist = best[i].Meters < HighWaterMarks.MinimumDistanceMeters ? HighWaterMarks.MinimumDistanceMeters : best[i].Meters;
						double w = power == 2.0
							? best[i].Mark.QualityWeight / (dist * dist)
							: best[i].Mark.QualityWeight / Math.Pow(dist, power);
						rawWeights[i] = w;
						total += w;
						decimal elev = best[i].Mark.ElevationFt;
						if (elev < minElev)
						{
							minElev = elev;
						}
						if (elev > maxElev)
						{
							maxElev = elev;
						}
					}
					double est = 0;
					for (int i = 0; i < found; i++)
					{
						est += best[i].Mark.ElevationDouble * rawWeights[i] / total;
					}
					decimal spread = maxElev - minElev;
					double nearest = best[0].Meters;
					EstimateConfidence conf =
						found == 1 || nearest > 500 || spread > 2.0m || noDatumAtAll ? EstimateConfidence.Low
						: found >= 3 && nearest <= 200 && spread <= 1.0m ? EstimateConfidence.High
						: EstimateConfidence.Medium;
					elevation[rowOffset + col] = (float)Math.Round((decimal)est, 2, MidpointRounding.AwayFromZero);
					confidence[rowOffset + col] = conf;
				}
			});
		}
		return new WaterSurfaceGrid(width, height, west, south, east, north, elevation, confidence, datumName, near.Count, leftOut);
	}

	private static bool IsCloser(double meters, int order, MarkHit existing) =>
		meters < existing.Meters || (meters == existing.Meters && order < existing.Mark.Order);

	private readonly record struct MarkEntry(double Latitude, double Longitude, decimal ElevationFt, double ElevationDouble, double QualityWeight, int Order);

	private readonly record struct MarkHit(MarkEntry Mark, double Meters);

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
		if (method == ClassMethod.EqualInterval)
		{
			double loVal = double.PositiveInfinity;
			double hiVal = double.NegativeInfinity;
			bool any = false;
			foreach (float v in values ?? Enumerable.Empty<float>())
			{
				if (float.IsNaN(v) || float.IsInfinity(v))
				{
					continue;
				}
				any = true;
				if (v < loVal)
				{
					loVal = v;
				}
				if (v > hiVal)
				{
					hiVal = v;
				}
			}
			if (!any)
			{
				return Array.Empty<double>();
			}
			if (loVal == hiVal)
			{
				return new[] { loVal, hiVal };
			}
			List<double> equalEdges = new List<double>(classes + 1) { loVal };
			for (int i = 1; i < classes; i++)
			{
				double edge = loVal + (hiVal - loVal) * i / classes;
				if (edge > equalEdges[^1] && edge < hiVal)
				{
					equalEdges.Add(edge);
				}
			}
			equalEdges.Add(hiVal);
			return equalEdges.ToArray();
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
		List<double> edges = new List<double>(classes + 1) { lo };
		for (int i = 1; i < classes; i++)
		{
			double edge = Quantile(sorted, (double)i / classes);
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
