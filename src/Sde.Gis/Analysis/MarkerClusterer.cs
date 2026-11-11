namespace Sde.Gis.Analysis;

/// <summary>
/// A point to cluster on the map: an id, a position in the map's projected coordinates (any unit, as long as the
/// cluster radius uses the same one), a number such as the percent damaged, and a category such as the result status.
/// </summary>
/// <param name="Id">The point's identifier, returned in the cluster's member list.</param>
/// <param name="X">Easting in map units.</param>
/// <param name="Y">Northing in map units.</param>
/// <param name="Value">A number to summarise (maximum and mean); NaN when the point has none, and it is then left out of both.</param>
/// <param name="Category">A label to count per cluster; null counts as an empty label.</param>
public sealed record ClusterPoint(string Id, double X, double Y, double Value, string Category);

/// <summary>One cluster of nearby points. A cluster of one is just that point.</summary>
public sealed class MarkerCluster
{
	/// <summary>The centre of the cluster in map units: the average position of its members (the point itself for a single).</summary>
	public double X { get; init; }

	public double Y { get; init; }

	/// <summary>How many points are in the cluster.</summary>
	public int Count { get; init; }

	/// <summary>The members' ids, sorted ordinally.</summary>
	public IReadOnlyList<string> MemberIds { get; init; }

	/// <summary>How many members have each category (a null category is counted under the empty string).</summary>
	public IReadOnlyDictionary<string, int> CategoryCounts { get; init; }

	/// <summary>The largest member value; NaN when no member has one.</summary>
	public double MaxValue { get; init; }

	/// <summary>The average of the member values that have one; NaN when none do.</summary>
	public double MeanValue { get; init; }
}

/// <summary>
/// Groups map points that sit close together so a crowded map can show one marker for many structures. Greedy
/// distance clustering on a grid: points are taken in a fixed order (by position, then id), each point not yet taken
/// starts a cluster and takes every free point within the radius of it. The result does not depend on the order the
/// points were given in, and it takes about n log n time (the sort), so 50,000 points is fast.
/// </summary>
public static class MarkerClusterer
{
	/// <summary>
	/// Clusters the points. <paramref name="radius"/> is in map units (the app passes pixels times the map's resolution);
	/// a radius of zero or less, or one that is not a number, leaves every point on its own. Points with a position that is
	/// not finite are left out. Clusters come out in the order of their first point (west to east, then south to north).
	/// </summary>
	public static IReadOnlyList<MarkerCluster> Cluster(IEnumerable<ClusterPoint> points, double radius)
	{
		List<ClusterPoint> sorted = (points ?? Enumerable.Empty<ClusterPoint>())
			.Where(p => p != null && double.IsFinite(p.X) && double.IsFinite(p.Y))
			.ToList();
		sorted.Sort(Compare);
		int n = sorted.Count;
		List<MarkerCluster> clusters = new List<MarkerCluster>();
		if (n == 0)
		{
			return clusters;
		}

		bool single = !(radius > 0) || !double.IsFinite(radius);
		Dictionary<(long, long), List<int>> cells = new Dictionary<(long, long), List<int>>();
		if (!single)
		{
			for (int i = 0; i < n; i++)
			{
				(long, long) key = Cell(sorted[i].X, sorted[i].Y, radius);
				if (!cells.TryGetValue(key, out List<int> list))
				{
					list = new List<int>();
					cells[key] = list;
				}
				list.Add(i);
			}
		}

		bool[] taken = new bool[n];
		double r2 = radius * radius;
		List<int> members = new List<int>();
		for (int i = 0; i < n; i++)
		{
			if (taken[i])
			{
				continue;
			}
			taken[i] = true;
			members.Clear();
			members.Add(i);
			if (!single)
			{
				ClusterPoint seed = sorted[i];
				(long cx, long cy) = Cell(seed.X, seed.Y, radius);
				for (long dx = -1; dx <= 1; dx++)
				{
					for (long dy = -1; dy <= 1; dy++)
					{
						if (!cells.TryGetValue((cx + dx, cy + dy), out List<int> list))
						{
							continue;
						}
						foreach (int j in list)
						{
							if (taken[j])
							{
								continue;
							}
							double ex = sorted[j].X - seed.X;
							double ey = sorted[j].Y - seed.Y;
							if (ex * ex + ey * ey <= r2)
							{
								taken[j] = true;
								members.Add(j);
							}
						}
					}
				}
				members.Sort();   // a fixed order for the sums below, whichever cell was looked at first
			}
			clusters.Add(Build(sorted, members));
		}
		return clusters;
	}

	private static (long, long) Cell(double x, double y, double size) => ((long)Math.Floor(x / size), (long)Math.Floor(y / size));

	private static int Compare(ClusterPoint a, ClusterPoint b)
	{
		int c = a.X.CompareTo(b.X);
		if (c != 0)
		{
			return c;
		}
		c = a.Y.CompareTo(b.Y);
		if (c != 0)
		{
			return c;
		}
		c = string.CompareOrdinal(a.Id, b.Id);
		if (c != 0)
		{
			return c;
		}
		c = string.CompareOrdinal(a.Category, b.Category);
		return c != 0 ? c : a.Value.CompareTo(b.Value);
	}

	private static MarkerCluster Build(List<ClusterPoint> sorted, List<int> members)
	{
		double sx = 0;
		double sy = 0;
		double max = double.NaN;
		double sum = 0;
		int valued = 0;
		SortedDictionary<string, int> counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
		List<string> ids = new List<string>(members.Count);
		foreach (int m in members)
		{
			ClusterPoint p = sorted[m];
			sx += p.X;
			sy += p.Y;
			ids.Add(p.Id);
			string category = p.Category ?? "";
			counts[category] = counts.TryGetValue(category, out int c) ? c + 1 : 1;
			if (!double.IsNaN(p.Value))
			{
				max = double.IsNaN(max) ? p.Value : Math.Max(max, p.Value);
				sum += p.Value;
				valued++;
			}
		}
		ids.Sort(StringComparer.Ordinal);
		int count = members.Count;
		return new MarkerCluster
		{
			X = count == 1 ? sorted[members[0]].X : sx / count,
			Y = count == 1 ? sorted[members[0]].Y : sy / count,
			Count = count,
			MemberIds = ids,
			CategoryCounts = counts,
			MaxValue = max,
			MeanValue = valued == 0 ? double.NaN : sum / valued,
		};
	}
}
