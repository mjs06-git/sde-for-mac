using System.Globalization;
using Sde.Core.Geo;

namespace Sde.Core.Field;

/// <summary>A place a field team has to visit: usually a structure, with its position and address.</summary>
/// <param name="Id">What the stop is (a PropertyID or an Assignment ID); the route keeps it as given.</param>
/// <param name="Latitude">Decimal degrees, or null when the structure isn't on the map yet.</param>
/// <param name="Longitude">Decimal degrees, or null.</param>
/// <param name="Address">The address, for the route sheet and for Maps when there's no position.</param>
public sealed record RouteStop(Guid Id, double? Latitude, double? Longitude, string Address = null)
{
	/// <summary>True when the stop has a usable position (both values, in range, and not 0, 0).</summary>
	public bool HasPosition => Latitude is double lat && Longitude is double lon
		&& lat >= -90 && lat <= 90 && lon >= -180 && lon <= 180 && !(lat == 0 && lon == 0);
}

/// <summary>Stops in the order to visit them, and how far that is.</summary>
public sealed class RoutePlan
{
	/// <summary>Every stop: those with a position in route order, then those without one in the order they were given.</summary>
	public IReadOnlyList<RouteStop> Stops { get; init; } = Array.Empty<RouteStop>();

	/// <summary>The straight-line length of the route through the stops with a position, from the start point when one was given.</summary>
	public double Meters { get; init; }

	/// <summary>How many stops have no position (they are at the end of <see cref="Stops"/>).</summary>
	public int WithoutPosition { get; init; }
}

/// <summary>
/// Puts a team's stops in a sensible order: nearest neighbour from the start point, then 2-opt (reversing any stretch
/// of the route that makes it shorter, which removes every crossing), on straight-line distance. Not the shortest
/// possible route, but close, quick for a few hundred stops, and the same every time for the same stops.
/// </summary>
public static class RouteOrder
{
	/// <summary>A change has to save more than this to count, so rounding can't make the search go round in circles.</summary>
	private const double MinimumGainMeters = 1e-6;

	/// <summary>Largest stop count for which a precomputed pairwise distance matrix is allocated (2000 × 2000 = 32 MB).</summary>
	private const int MatrixLimit = 2000;

	/// <summary>
	/// Orders the stops. With a start point (the office, or where the team is), the route starts there; without one,
	/// nearest neighbour starts at the first stop with a position and 2-opt may then turn either end of the route round.
	/// Stops without a position go last, in their original order. Ties go to the stop given first, so the same stops
	/// always give the same route.
	/// </summary>
	public static RoutePlan Plan(IEnumerable<RouteStop> stops, double? startLatitude = null, double? startLongitude = null)
	{
		List<RouteStop> placed = new List<RouteStop>();
		List<RouteStop> unplaced = new List<RouteStop>();
		foreach (RouteStop s in stops ?? Enumerable.Empty<RouteStop>())
		{
			if (s == null)
			{
				continue;
			}
			if (s.HasPosition)
			{
				placed.Add(s);
			}
			else
			{
				unplaced.Add(s);
			}
		}
		RouteStop start = startLatitude is double sla && startLongitude is double slo ? new RouteStop(Guid.Empty, sla, slo) : null;
		if (start != null && !start.HasPosition)
		{
			start = null;
		}

		List<RouteStop> route;
		double meters;
		if (placed.Count >= 2 && placed.Count <= MatrixLimit)
		{
			int n = placed.Count;
			double[] dist = BuildDistanceMatrix(placed);
			double[] fromStart = start == null ? null : BuildStartDistances(placed, start);
			int[] order = NearestNeighbourMatrix(n, dist, fromStart);
			TwoOptMatrix(order, dist, fromStart);
			route = new List<RouteStop>(n + unplaced.Count);
			for (int i = 0; i < n; i++)
			{
				route.Add(placed[order[i]]);
			}
			meters = LengthMatrix(order, dist, fromStart);
		}
		else
		{
			route = NearestNeighbour(placed, start);
			TwoOpt(route, start);
			meters = Length(route, start);
		}
		if (unplaced.Count > 0)
		{
			route.AddRange(unplaced);
		}
		return new RoutePlan
		{
			Stops = route,
			Meters = meters,
			WithoutPosition = unplaced.Count,
		};
	}

	/// <summary>
	/// A link that opens Apple Maps with directions to the stop: to its position when it has one
	/// (<c>https://maps.apple.com/?daddr=39.712345,-84.178901</c>), otherwise to its address; null when it has neither.
	/// </summary>
	public static string MapsLink(RouteStop stop)
	{
		if (stop == null)
		{
			return null;
		}
		if (stop.HasPosition)
		{
			return "https://maps.apple.com/?daddr=" + stop.Latitude.Value.ToString("0.######", CultureInfo.InvariantCulture)
				+ "," + stop.Longitude.Value.ToString("0.######", CultureInfo.InvariantCulture);
		}
		return string.IsNullOrWhiteSpace(stop.Address) ? null : "https://maps.apple.com/?daddr=" + Uri.EscapeDataString(stop.Address.Trim());
	}

	/// <summary>The straight-line length in metres of visiting the stops in order, from <paramref name="start"/> when given.</summary>
	public static double Length(IReadOnlyList<RouteStop> route, RouteStop start = null)
	{
		double total = 0;
		RouteStop previous = start;
		foreach (RouteStop s in route)
		{
			if (previous != null)
			{
				total += Distance(previous, s);
			}
			previous = s;
		}
		return total;
	}

	private static List<RouteStop> NearestNeighbour(List<RouteStop> stops, RouteStop start)
	{
		List<RouteStop> route = new List<RouteStop>(stops.Count);
		List<RouteStop> left = new List<RouteStop>(stops);
		RouteStop current = start;
		if (current == null && left.Count > 0)
		{
			current = left[0];
			left.RemoveAt(0);
			route.Add(current);
		}
		while (left.Count > 0)
		{
			int best = 0;
			double bestDistance = Distance(current, left[0]);
			for (int i = 1; i < left.Count; i++)
			{
				double d = Distance(current, left[i]);
				if (d < bestDistance)
				{
					best = i;
					bestDistance = d;
				}
			}
			current = left[best];
			left.RemoveAt(best);
			route.Add(current);
		}
		return route;
	}

	/// <summary>
	/// Improves an open route (it doesn't return to the start) by reversing stretches: for stops i to j, the legs
	/// (before i, i) and (j, after j) become (before i, j) and (i, after j). The leg before the first stop exists only
	/// with a start point, and the leg after the last stop never does, so either end of the route can turn round too.
	/// Repeats until no reversal shortens it.
	/// </summary>
	private static void TwoOpt(List<RouteStop> route, RouteStop start)
	{
		int n = route.Count;
		if (n < 2)
		{
			return;
		}
		RouteStop At(int k) => k < 0 ? start : k >= n ? null : route[k];
		double Leg(RouteStop a, RouteStop b) => a == null || b == null ? 0 : Distance(a, b);
		int passes = 0;
		bool improved = true;
		while (improved && passes++ < 10 * n + 10)
		{
			improved = false;
			for (int i = 0; i < n - 1; i++)
			{
				for (int j = i + 1; j < n; j++)
				{
					RouteStop before = At(i - 1);
					RouteStop after = At(j + 1);
					double now = Leg(before, route[i]) + Leg(route[j], after);
					double reversed = Leg(before, route[j]) + Leg(route[i], after);
					if (reversed < now - MinimumGainMeters)
					{
						route.Reverse(i, j - i + 1);
						improved = true;
					}
				}
			}
		}
	}

	private static double[] BuildDistanceMatrix(List<RouteStop> stops)
	{
		int n = stops.Count;
		double[] dist = new double[n * n];
		for (int i = 0; i < n; i++)
		{
			RouteStop a = stops[i];
			int row = i * n;
			for (int j = i + 1; j < n; j++)
			{
				double d = Distance(a, stops[j]);
				dist[row + j] = d;
				dist[j * n + i] = d;
			}
		}
		return dist;
	}

	private static double[] BuildStartDistances(List<RouteStop> stops, RouteStop start)
	{
		int n = stops.Count;
		double[] dist = new double[n];
		for (int i = 0; i < n; i++)
		{
			dist[i] = Distance(start, stops[i]);
		}
		return dist;
	}

	private static int[] NearestNeighbourMatrix(int n, double[] dist, double[] fromStart)
	{
		int[] order = new int[n];
		bool[] used = new bool[n];
		int pos = 0;
		int current;
		if (fromStart == null)
		{
			current = 0;
			used[0] = true;
			order[pos++] = 0;
		}
		else
		{
			int best = 0;
			double bestDistance = fromStart[0];
			for (int i = 1; i < n; i++)
			{
				if (fromStart[i] < bestDistance)
				{
					best = i;
					bestDistance = fromStart[i];
				}
			}
			current = best;
			used[best] = true;
			order[pos++] = best;
		}
		while (pos < n)
		{
			int row = current * n;
			int best = -1;
			double bestDistance = double.PositiveInfinity;
			for (int i = 0; i < n; i++)
			{
				if (used[i])
				{
					continue;
				}
				double d = dist[row + i];
				if (best < 0 || d < bestDistance)
				{
					best = i;
					bestDistance = d;
				}
			}
			current = best;
			used[best] = true;
			order[pos++] = best;
		}
		return order;
	}

	private static void TwoOptMatrix(int[] order, double[] dist, double[] fromStart)
	{
		int n = order.Length;
		int passes = 0;
		bool improved = true;
		while (improved && passes++ < 10 * n + 10)
		{
			improved = false;
			for (int i = 0; i < n - 1; i++)
			{
				int beforeIdx = i - 1;
				int beforeRow = beforeIdx >= 0 ? order[beforeIdx] * n : -1;
				int oi = order[i];
				double legBeforeI = beforeIdx >= 0 ? dist[beforeRow + oi] : (fromStart != null ? fromStart[oi] : 0.0);
				for (int j = i + 1; j < n; j++)
				{
					int oj = order[j];
					int afterIdx = j + 1;
					double legJAfter = afterIdx < n ? dist[oj * n + order[afterIdx]] : 0.0;
					double now = legBeforeI + legJAfter;
					double legBeforeJ = beforeIdx >= 0 ? dist[beforeRow + oj] : (fromStart != null ? fromStart[oj] : 0.0);
					double legIAfter = afterIdx < n ? dist[oi * n + order[afterIdx]] : 0.0;
					double reversed = legBeforeJ + legIAfter;
					if (reversed < now - MinimumGainMeters)
					{
						Array.Reverse(order, i, j - i + 1);
						oi = order[i];
						legBeforeI = beforeIdx >= 0 ? dist[beforeRow + oi] : (fromStart != null ? fromStart[oi] : 0.0);
						improved = true;
					}
				}
			}
		}
	}

	private static double LengthMatrix(int[] order, double[] dist, double[] fromStart)
	{
		int n = order.Length;
		double total = fromStart != null ? fromStart[order[0]] : 0.0;
		for (int i = 1; i < n; i++)
		{
			total += dist[order[i - 1] * n + order[i]];
		}
		return total;
	}

	private static double Distance(RouteStop a, RouteStop b) => GeoMath.Meters(a.Latitude.Value, a.Longitude.Value, b.Latitude.Value, b.Longitude.Value);
}
