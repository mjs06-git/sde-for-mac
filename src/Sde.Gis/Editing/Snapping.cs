using NetTopologySuite.Geometries;

namespace Sde.Gis.Editing;

/// <summary>What a point snapped to.</summary>
public enum SnapKind
{
	/// <summary>Nothing was close enough; the point stays where it was.</summary>
	None,

	/// <summary>A corner of a shape (or a point).</summary>
	Vertex,

	/// <summary>The nearest point on an edge of a shape.</summary>
	Edge,
}

/// <summary>
/// Where a point ends up after snapping, and what it snapped to: which candidate (its position in the list given) and,
/// for a corner, which corner, so the caller can take that corner's exact coordinate from another copy of the shape
/// (the map snaps in Web Mercator metres but keeps longitude and latitude).
/// </summary>
public readonly record struct SnapResult(Coordinate At, SnapKind Kind, int Candidate = -1, VertexRef? Vertex = null)
{
	public bool Snapped => Kind != SnapKind.None;
}

/// <summary>
/// Snapping for the map's editor: a point dropped near another shape's corner moves onto that corner, and otherwise a
/// point near an edge moves onto the edge, so shapes drawn next to each other share their boundaries. Corners win over
/// edges within the tolerance, because a corner is what a person aims for. Coordinates are planar (the map uses Web
/// Mercator metres, so the tolerance can be a number of screen pixels times the map's resolution).
/// </summary>
public static class Snapping
{
	/// <summary>
	/// The nearest corner of <paramref name="candidates"/> within <paramref name="tolerance"/> of <paramref name="p"/>;
	/// otherwise the nearest point on an edge within it; otherwise <paramref name="p"/> itself.
	/// </summary>
	public static SnapResult Snap(Coordinate p, IEnumerable<Geometry> candidates, double tolerance)
	{
		ArgumentNullException.ThrowIfNull(p);
		if (candidates == null || !(tolerance > 0))
		{
			return new SnapResult(p, SnapKind.None);
		}
		double px = p.X;
		double py = p.Y;
		double tol2 = tolerance * tolerance;
		bool hasVertex = false;
		double bestVertexX = 0;
		double bestVertexY = 0;
		double vertexDist2 = double.MaxValue;
		int vertexCandidate = -1;
		VertexRef vertexRef = default;
		bool hasEdge = false;
		double bestEdgeX = 0;
		double bestEdgeY = 0;
		double edgeDist2 = double.MaxValue;
		int edgeCandidate = -1;
		int index = -1;
		foreach (Geometry g in candidates)
		{
			index++;
			if (g == null || g.IsEmpty)
			{
				continue;
			}
			Envelope near = g.EnvelopeInternal;
			if (BoxDistanceSquared(near, px, py) > tol2)
			{
				continue;
			}
			GeometryEdit.ScanVertices(g, (VertexRef v, double x, double y) =>
			{
				double dx = x - px;
				double dy = y - py;
				double d2 = dx * dx + dy * dy;
				if (d2 <= tol2 && d2 < vertexDist2)
				{
					hasVertex = true;
					bestVertexX = x;
					bestVertexY = y;
					vertexDist2 = d2;
					vertexCandidate = index;
					vertexRef = v;
				}
			});
			if (hasVertex)
			{
				// Once a corner is within reach, no edge can win; the other shapes may still have a nearer corner.
				continue;
			}
			GeometryEdit.ScanEdges(g, (VertexRef _, double ax, double ay, double bx, double by) =>
			{
				// Reject edges whose bounding box is outside tolerance.
				double minX = ax < bx ? ax : bx;
				double maxX = ax > bx ? ax : bx;
				double minY = ay < by ? ay : by;
				double maxY = ay > by ? ay : by;
				double bdx = px < minX ? minX - px : px > maxX ? px - maxX : 0.0;
				double bdy = py < minY ? minY - py : py > maxY ? py - maxY : 0.0;
				if (bdx * bdx + bdy * bdy > tol2)
				{
					return;
				}
				GeometryEdit.ClosestPointOnSegment(ax, ay, bx, by, px, py, out double ox, out double oy);
				double dx = ox - px;
				double dy = oy - py;
				double d2 = dx * dx + dy * dy;
				if (d2 <= tol2 && d2 < edgeDist2)
				{
					hasEdge = true;
					bestEdgeX = ox;
					bestEdgeY = oy;
					edgeDist2 = d2;
					edgeCandidate = index;
				}
			});
		}
		if (hasVertex)
		{
			return new SnapResult(new Coordinate(bestVertexX, bestVertexY), SnapKind.Vertex, vertexCandidate, vertexRef);
		}
		return hasEdge
			? new SnapResult(new Coordinate(bestEdgeX, bestEdgeY), SnapKind.Edge, edgeCandidate)
			: new SnapResult(p, SnapKind.None);
	}

	private static double BoxDistanceSquared(Envelope box, double px, double py)
	{
		double dx = px < box.MinX ? box.MinX - px : px > box.MaxX ? px - box.MaxX : 0.0;
		double dy = py < box.MinY ? box.MinY - py : py > box.MaxY ? py - box.MaxY : 0.0;
		return dx * dx + dy * dy;
	}
}
