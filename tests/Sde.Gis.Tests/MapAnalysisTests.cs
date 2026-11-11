using System.Diagnostics;
using Sde.Core.Depth;
using Sde.Gis.Analysis;

namespace Sde.Gis.Tests;

/// <summary>Marker clustering, heat map rasters and the high-water-mark water-surface grid, on small synthetic cases worked out by hand.</summary>
public class MapAnalysisTests
{
	// ---- Marker clustering ----

	private static ClusterPoint P(string id, double x, double y, double value = 0, string category = "ok") => new ClusterPoint(id, x, y, value, category);

	[Fact]
	public void Clusters_GroupNearbyPoints_WithCountsCategoriesAndValueStatistics()
	{
		IReadOnlyList<MarkerCluster> clusters = MarkerClusterer.Cluster(new[]
		{
			P("b", 1, 0, 20, "y"), P("c", 100, 0, 5, "x"), P("a", 0, 0, 10, "x"),
		}, 2);

		Assert.Equal(2, clusters.Count);
		MarkerCluster pair = clusters[0];
		Assert.Equal((0.5, 0.0, 2), (pair.X, pair.Y, pair.Count));
		Assert.Equal(new[] { "a", "b" }, pair.MemberIds);
		Assert.Equal(1, pair.CategoryCounts["x"]);
		Assert.Equal(1, pair.CategoryCounts["y"]);
		Assert.Equal((20.0, 15.0), (pair.MaxValue, pair.MeanValue));

		MarkerCluster single = clusters[1];
		Assert.Equal((100.0, 0.0, 1), (single.X, single.Y, single.Count));
		Assert.Equal(new[] { "c" }, single.MemberIds);
		Assert.Equal((5.0, 5.0), (single.MaxValue, single.MeanValue));
	}

	[Fact]
	public void Clusters_AreTheSame_WhateverTheInputOrder()
	{
		Random random = new Random(7);
		List<ClusterPoint> points = Enumerable.Range(0, 2000)
			.Select(i => P("p" + i, random.Next(0, 300), random.Next(0, 300), random.Next(0, 100), random.Next(0, 3).ToString())).ToList();
		string first = Describe(MarkerClusterer.Cluster(points, 12));
		points.Reverse();
		Assert.Equal(first, Describe(MarkerClusterer.Cluster(points, 12)));
		List<ClusterPoint> shuffled = points.OrderBy(p => p.Id.GetHashCode() ^ 12345).ToList();
		Assert.Equal(first, Describe(MarkerClusterer.Cluster(shuffled, 12)));
	}

	private static string Describe(IReadOnlyList<MarkerCluster> clusters) =>
		string.Join("|", clusters.Select(c => $"{c.X:R},{c.Y:R},{c.Count},{string.Join(",", c.MemberIds)},{c.MaxValue:R},{c.MeanValue:R},{string.Join(",", c.CategoryCounts.Select(k => k.Key + "=" + k.Value))}"));

	[Fact]
	public void Clusters_EveryPointLandsInExactlyOneCluster_WithinTheRadiusOfItsFirstMember()
	{
		Random random = new Random(3);
		List<ClusterPoint> points = Enumerable.Range(0, 1500).Select(i => P("p" + i, random.NextDouble() * 500, random.NextDouble() * 500)).ToList();
		IReadOnlyList<MarkerCluster> clusters = MarkerClusterer.Cluster(points, 20);
		Assert.Equal(points.Count, clusters.Sum(c => c.Count));
		Assert.Equal(points.Count, clusters.SelectMany(c => c.MemberIds).Distinct().Count());
	}

	[Fact]
	public void Clusters_EdgeCases()
	{
		Assert.Empty(MarkerClusterer.Cluster(null, 10));
		Assert.Empty(MarkerClusterer.Cluster(new ClusterPoint[0], 10));

		// A zero radius keeps every point apart.
		Assert.Equal(2, MarkerClusterer.Cluster(new[] { P("a", 0, 0), P("b", 0, 0) }, 0).Count);

		// Identical positions merge; the value statistics skip a missing value; a null category counts as "".
		MarkerCluster merged = Assert.Single(MarkerClusterer.Cluster(new[] { P("a", 5, 5, 4), P("b", 5, 5, double.NaN, null), P("c", 5, 5, 8) }, 1));
		Assert.Equal((3, 8.0, 6.0), (merged.Count, merged.MaxValue, merged.MeanValue));
		Assert.Equal(1, merged.CategoryCounts[""]);

		// No values at all: NaN, not zero. Points with a position that is not a number are left out.
		MarkerCluster none = Assert.Single(MarkerClusterer.Cluster(new[] { P("a", 1, 1, double.NaN), P("bad", double.NaN, 0) }, 5));
		Assert.True(double.IsNaN(none.MaxValue));
		Assert.True(double.IsNaN(none.MeanValue));
	}

	[Fact]
	public void Clusters_FiftyThousandPoints_Finish()
	{
		Random random = new Random(11);
		List<ClusterPoint> points = Enumerable.Range(0, 50000).Select(i => P("p" + i, random.NextDouble() * 100000, random.NextDouble() * 100000, random.Next(100))).ToList();
		Stopwatch watch = Stopwatch.StartNew();
		IReadOnlyList<MarkerCluster> clusters = MarkerClusterer.Cluster(points, 500);
		watch.Stop();
		Assert.Equal(50000, clusters.Sum(c => c.Count));
		Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"Took {watch.Elapsed}.");   // a generous limit for a slow shared machine
	}

	// ---- Heat map ----

	[Fact]
	public void Heatmap_OnePoint_PeaksAtItsPixelAndFadesByTheQuarticKernel()
	{
		// An 11 by 11 grid over 0..11, one point in pixel (5, 5), radius 5 pixels.
		HeatmapGrid grid = HeatmapRaster.Compute(new[] { new HeatPoint(5.5, 5.5, 1) }, 0, 0, 11, 11, 11, 11, 5);
		Assert.Equal(1f, grid[5, 5]);
		Assert.Equal((1 - 1 / 25.0) * (1 - 1 / 25.0), grid[6, 5], 5);   // one pixel away: (1 - (1/5)^2)^2
		Assert.Equal(grid[4, 5], grid[6, 5]);
		Assert.Equal(grid[5, 4], grid[5, 6]);
		Assert.Equal(0f, grid[10, 5]);   // five pixels away: at the radius, nothing is left
		Assert.Equal(1.0, grid.MaxRawDensity);
	}

	[Fact]
	public void Heatmap_Weights_AreRelative_AndRowZeroIsTheNorthEdge()
	{
		HeatmapGrid grid = HeatmapRaster.Compute(new[] { new HeatPoint(2.5, 97.5, 1), new HeatPoint(97.5, 2.5, 3) }, 0, 0, 100, 100, 100, 100, 3);
		Assert.Equal(1f, grid[97, 97]);      // the heavy point is in the south-east corner (bottom row)
		Assert.Equal(1 / 3f, grid[2, 2], 5); // the light one in the north-west corner (top row)
		Assert.Equal(3.0, grid.MaxRawDensity);
	}

	[Fact]
	public void Heatmap_Gaussian_FallsMoreSteeplyThanQuarticAtTheSameDistanceAndIsZeroAtTheRadius()
	{
		HeatmapGrid g = HeatmapRaster.Compute(new[] { new HeatPoint(5.5, 5.5, 1) }, 0, 0, 11, 11, 11, 11, 5, HeatKernel.Gaussian);
		Assert.Equal(1f, g[5, 5]);
		Assert.Equal(Math.Exp(-0.5 * 9 / 25.0), g[6, 5], 4);   // sigma is a third of the radius
		Assert.Equal(0f, g[10, 5]);
	}

	[Fact]
	public void Heatmap_PointsJustOutsideTheExtentGlowIn_AndFarOnesAreIgnored()
	{
		HeatmapGrid near = HeatmapRaster.Compute(new[] { new HeatPoint(-1.5, 5.5, 1) }, 0, 0, 11, 11, 11, 11, 5);
		Assert.True(near[0, 5] > 0);
		HeatmapGrid far = HeatmapRaster.Compute(new[] { new HeatPoint(-50, 5.5, 1) }, 0, 0, 11, 11, 11, 11, 5);
		Assert.All(far.Values, v => Assert.Equal(0f, v));
		Assert.Equal(0.0, far.MaxRawDensity);
	}

	[Fact]
	public void Heatmap_EdgeCases()
	{
		Assert.All(HeatmapRaster.Compute(null, 0, 0, 10, 10, 4, 4, 2).Values, v => Assert.Equal(0f, v));
		Assert.All(HeatmapRaster.Compute(new[] { new HeatPoint(1, 1, 0), new HeatPoint(1, 1, -2), new HeatPoint(1, 1, double.NaN) }, 0, 0, 10, 10, 4, 4, 2).Values, v => Assert.Equal(0f, v));

		// All points identical: one hot pixel at 1, the rest fade.
		HeatmapGrid same = HeatmapRaster.Compute(Enumerable.Repeat(new HeatPoint(2.5, 2.5, 1), 100), 0, 0, 5, 5, 5, 5, 2);
		Assert.Equal(1f, same[2, 2]);
		Assert.Equal(100.0, same.MaxRawDensity);

		Assert.Throws<ArgumentOutOfRangeException>(() => HeatmapRaster.Compute(null, 0, 0, 1, 1, 0, 1, 1));
		Assert.Throws<ArgumentOutOfRangeException>(() => HeatmapRaster.Compute(null, 0, 0, 1, 1, 1, 1, 0));
		Assert.Throws<ArgumentException>(() => HeatmapRaster.Compute(null, 1, 0, 1, 1, 1, 1, 1));
	}

	[Fact]
	public void Heatmap_SameGrid_WhateverTheInputOrder()
	{
		Random random = new Random(5);
		List<HeatPoint> points = Enumerable.Range(0, 3000).Select(i => new HeatPoint(random.NextDouble() * 200, random.NextDouble() * 100, 1 + random.Next(5))).ToList();
		HeatmapGrid a = HeatmapRaster.Compute(points, 0, 0, 200, 100, 80, 40, 6);
		points.Reverse();
		HeatmapGrid b = HeatmapRaster.Compute(points, 0, 0, 200, 100, 80, 40, 6);
		for (int i = 0; i < a.Values.Length; i++)
		{
			Assert.Equal(a.Values[i], b.Values[i], 4);
		}
	}

	[Fact]
	public void Heatmap_ToRgba_ColoursByDensityAndFadesTheAlpha()
	{
		HeatmapGrid grid = new HeatmapGrid(3, 1, new[] { 0f, 0.5f, 1f }, 1);
		ColorStop[] ramp = { new ColorStop(1, 255, 0, 0), new ColorStop(0, 0, 0, 255) };   // out of order on purpose

		byte[] rgba = HeatmapRaster.ToRgba(grid, ramp);
		Assert.Equal(12, rgba.Length);
		Assert.Equal(new byte[] { 0, 0, 0, 0 }, rgba[0..4]);   // no density: clear
		Assert.Equal(new byte[] { 255, 0, 0, 255 }, rgba[8..12]);
		Assert.Equal(128, rgba[7]);   // half density, half alpha
		Assert.InRange(rgba[4], 120, 135);
		Assert.InRange(rgba[6], 120, 135);

		byte[] soft = HeatmapRaster.ToRgba(grid, ramp, 100);
		Assert.Equal(100, soft[11]);

		byte[] pre = HeatmapRaster.ToRgba(grid, ramp, 100, premultiplied: true);
		Assert.Equal(new byte[] { 100, 0, 0, 100 }, pre[8..12]);

		Assert.Throws<ArgumentException>(() => HeatmapRaster.ToRgba(grid, new ColorStop[0]));
	}

	[Fact]
	public void Heatmap_FiftyThousandPoints_AtFullSize_Finish()
	{
		Random random = new Random(9);
		List<HeatPoint> points = Enumerable.Range(0, 50000).Select(i => new HeatPoint(random.NextDouble() * 10000, random.NextDouble() * 7500, 1 + random.Next(4))).ToList();
		Stopwatch watch = Stopwatch.StartNew();
		HeatmapGrid grid = HeatmapRaster.Compute(points, 0, 0, 10000, 7500, 1024, 768, 20);
		byte[] rgba = HeatmapRaster.ToRgba(grid, new[] { new ColorStop(0, 0, 0, 255), new ColorStop(1, 255, 0, 0) });
		watch.Stop();
		Assert.Equal(1024 * 768 * 4, rgba.Length);
		Assert.Equal(1f, grid.Values.Max());
		Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"Took {watch.Elapsed}.");
	}

	// ---- Water-surface grid ----

	private const double Lat = 39.7;
	private const double Lon = -84.2;

	private static HighWaterMark Mark(double northMetres, decimal elevation, int? quality = 2, string datum = "NAVD88", double eastMetres = 0, string id = null) => new HighWaterMark
	{
		Id = id,
		Latitude = Lat + northMetres / 111195.0,
		Longitude = Lon + eastMetres / (111195.0 * Math.Cos(Lat * Math.PI / 180)),
		ElevationFt = elevation,
		DatumName = datum,
		Quality = quality,
	};

	/// <summary>A 1 by 1 grid whose single cell is centred on the test point.</summary>
	private static WaterSurfaceGrid OneCell(IEnumerable<HighWaterMark> marks, HighWaterMarkOptions options = null) =>
		DepthSurface.Compute(marks, Lon - 0.0005, Lat - 0.0005, Lon + 0.0005, Lat + 0.0005, 1, 1, options);

	[Fact]
	public void Surface_TwoMarks_AreWeightedByInverseDistanceSquared()
	{
		// 100 m (740 ft) and 200 m (750 ft), same quality: weights 1/100^2 and 1/200^2 are 4 to 1, so 740 * 0.8 + 750 * 0.2 = 742.
		WaterSurfaceGrid grid = OneCell(new[] { Mark(100, 740m), Mark(-200, 750m) });
		Assert.Equal(1, grid.CellsWithData);
		Assert.Equal(742.0, grid.ElevationFt[0], 1);
		Assert.Equal(grid.ElevationFt[0], grid.MinFt);
		Assert.Equal(grid.ElevationFt[0], grid.MaxFt);
		Assert.Equal("NAVD88", grid.DatumName);
		Assert.Equal(2, grid.MarksUsed);
	}

	[Fact]
	public void Surface_EveryCell_EqualsThePerStructureEstimateAtItsCentre()
	{
		Random random = new Random(21);
		List<HighWaterMark> marks = Enumerable.Range(0, 40)
			.Select(i => Mark(random.Next(-1500, 1500), 700 + random.Next(0, 80) / 10m, 1 + random.Next(6), eastMetres: random.Next(-1500, 1500), id: "m" + i)).ToList();
		const int W = 12;
		const int H = 9;
		WaterSurfaceGrid grid = DepthSurface.Compute(marks, Lon - 0.015, Lat - 0.012, Lon + 0.015, Lat + 0.012, W, H);
		int withData = 0;
		for (int row = 0; row < H; row++)
		{
			for (int col = 0; col < W; col++)
			{
				double lat = Lat + 0.012 - (row + 0.5) * 0.024 / H;
				double lon = Lon - 0.015 + (col + 0.5) * 0.030 / W;
				WaterSurfaceEstimate expected = HighWaterMarks.Estimate(marks, lat, lon);
				if (expected.ElevationFt is decimal ft)
				{
					withData++;
					Assert.Equal((float)ft, grid[col, row]);
					Assert.Equal(expected.Confidence, grid.Confidence[row * W + col]);
				}
				else
				{
					Assert.True(float.IsNaN(grid[col, row]));
					Assert.Equal(EstimateConfidence.None, grid.Confidence[row * W + col]);
				}
			}
		}
		Assert.True(withData > 0);
		Assert.Equal(withData, grid.CellsWithData);
	}

	[Fact]
	public void Surface_HasNoDataBeyondTheSearchDistance()
	{
		// One mark at the centre of a 4 by 4 grid 0.04 degrees across: only the four centre cells (about 700 m away) are within 1,000 m.
		HighWaterMark mark = new HighWaterMark { Latitude = Lat, Longitude = Lon, ElevationFt = 740m, DatumName = "NAVD88", Quality = 2 };
		WaterSurfaceGrid grid = DepthSurface.Compute(new[] { mark }, Lon - 0.02, Lat - 0.02, Lon + 0.02, Lat + 0.02, 4, 4);
		Assert.Equal(4, grid.CellsWithData);
		Assert.True(float.IsNaN(grid[0, 0]));
		Assert.Equal(740f, grid[1, 1]);
		Assert.Equal(740f, grid[2, 2]);
		Assert.Equal(EstimateConfidence.Low, grid.Confidence[1 * 4 + 1]);   // one mark only
		Assert.Equal(EstimateConfidence.None, grid.Confidence[0]);
	}

	[Fact]
	public void Surface_EdgeCases()
	{
		WaterSurfaceGrid empty = OneCell(null);
		Assert.Equal(0, empty.CellsWithData);
		Assert.True(float.IsNaN(empty.MinFt));
		Assert.True(float.IsNaN(empty.MaxFt));

		// Marks far outside the extent are not used at all.
		WaterSurfaceGrid outside = OneCell(new[] { Mark(50000, 740m) });
		Assert.Equal(0, outside.MarksUsed);
		Assert.Equal(0, outside.CellsWithData);

		// All marks identical: a flat surface.
		WaterSurfaceGrid flat = OneCell(Enumerable.Repeat(Mark(50, 741.5m), 5));
		Assert.Equal((741.5f, 741.5f), (flat.MinFt, flat.MaxFt));

		// Unusable marks (no position) are skipped.
		WaterSurfaceGrid skipped = OneCell(new[] { new HighWaterMark { Latitude = 0, Longitude = 0, ElevationFt = 1m, Quality = 1 } });
		Assert.Equal(0, skipped.CellsWithData);

		Assert.Throws<ArgumentOutOfRangeException>(() => DepthSurface.Compute(null, 0, 0, 1, 1, 0, 1));
		Assert.Throws<ArgumentException>(() => DepthSurface.Compute(null, 1, 0, 0, 1, 1, 1));
	}

	[Fact]
	public void Surface_UsesOneDatum_TheOneWithMostMarks_AndCountsTheOthers()
	{
		List<HighWaterMark> marks = new List<HighWaterMark>
		{
			Mark(50, 740m), Mark(-60, 741m, eastMetres: 30), Mark(80, 742m, eastMetres: -40),
			Mark(10, 700m, datum: "NGVD29"),
			Mark(20, 755m, datum: null),
		};
		WaterSurfaceGrid grid = OneCell(marks);
		Assert.Equal("NAVD88", grid.DatumName);
		Assert.Equal(3, grid.MarksUsed);
		Assert.Equal(2, grid.MarksLeftOut);
		Assert.InRange(grid.ElevationFt[0], 740f, 742f);
	}

	[Fact]
	public void Surface_SameResult_WhateverTheOrderOfTheMarks()
	{
		Random random = new Random(4);
		List<HighWaterMark> marks = Enumerable.Range(0, 60)
			.Select(i => Mark(random.Next(-900, 900), 700 + random.Next(0, 50) / 10m, 1 + random.Next(6), eastMetres: random.Next(-900, 900), id: "m" + i)).ToList();
		WaterSurfaceGrid a = DepthSurface.Compute(marks, Lon - 0.01, Lat - 0.01, Lon + 0.01, Lat + 0.01, 10, 10);
		marks.Reverse();
		WaterSurfaceGrid b = DepthSurface.Compute(marks, Lon - 0.01, Lat - 0.01, Lon + 0.01, Lat + 0.01, 10, 10);
		Assert.Equal(a.ElevationFt, b.ElevationFt);
		Assert.Equal(a.Confidence, b.Confidence);
	}

	[Fact]
	public void Surface_ManyMarksAndCells_Finish()
	{
		Random random = new Random(13);
		List<HighWaterMark> marks = Enumerable.Range(0, 1500)
			.Select(i => Mark(random.Next(-4000, 4000), 700 + random.Next(0, 100) / 10m, 1 + random.Next(6), eastMetres: random.Next(-4000, 4000), id: "m" + i)).ToList();
		Stopwatch watch = Stopwatch.StartNew();
		WaterSurfaceGrid grid = DepthSurface.Compute(marks, Lon - 0.05, Lat - 0.04, Lon + 0.05, Lat + 0.04, 150, 120);
		watch.Stop();
		Assert.True(grid.CellsWithData > 0);
		Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"Took {watch.Elapsed}.");
	}

	// ---- Class breaks ----

	[Fact]
	public void ClassBreaks_EqualInterval_AndQuantile_OnKnownValues()
	{
		float[] values = Enumerable.Range(0, 11).Select(i => (float)i).Append(float.NaN).ToArray();   // 0 to 10 and a no-data cell
		Assert.Equal(new[] { 0.0, 2, 4, 6, 8, 10 }, ClassBreaks.Compute(values, 5, ClassMethod.EqualInterval));

		float[] ten = Enumerable.Range(1, 10).Select(i => (float)i).ToArray();
		Assert.Equal(new[] { 1.0, 5.5, 10 }, ClassBreaks.Compute(ten, 2, ClassMethod.Quantile));
		Assert.Equal(new[] { 1.0, 3.25, 5.5, 7.75, 10 }, ClassBreaks.Compute(ten, 4, ClassMethod.Quantile));
	}

	[Fact]
	public void ClassBreaks_EdgeCases()
	{
		Assert.Empty(ClassBreaks.Compute(new[] { float.NaN }, 5, ClassMethod.Quantile));
		Assert.Empty(ClassBreaks.Compute(null, 5, ClassMethod.EqualInterval));
		Assert.Equal(new[] { 3.0, 3.0 }, ClassBreaks.Compute(new[] { 3f, 3f, 3f }, 5, ClassMethod.EqualInterval));

		// A heavy repeat makes quantile edges coincide; they merge instead of making empty classes.
		double[] merged = ClassBreaks.Compute(new float[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 9 }, 5, ClassMethod.Quantile);
		Assert.Equal(merged.Distinct().Count(), merged.Length);
		Assert.Equal((1.0, 9.0), (merged[0], merged[^1]));

		Assert.Throws<ArgumentOutOfRangeException>(() => ClassBreaks.Compute(new[] { 1f }, 0, ClassMethod.Quantile));
	}

	[Fact]
	public void ClassBreaks_ClassOf_PutsEdgesInTheHigherClassAndTheTopInTheLast()
	{
		double[] edges = { 0, 2, 4, 6, 8, 10 };
		Assert.Equal(0, ClassBreaks.ClassOf(0, edges));
		Assert.Equal(1, ClassBreaks.ClassOf(2, edges));
		Assert.Equal(1, ClassBreaks.ClassOf(3.9, edges));
		Assert.Equal(4, ClassBreaks.ClassOf(10, edges));
		Assert.Equal(-1, ClassBreaks.ClassOf(11, edges));
		Assert.Equal(-1, ClassBreaks.ClassOf(double.NaN, edges));
		Assert.Equal(-1, ClassBreaks.ClassOf(1, Array.Empty<double>()));
		Assert.Equal(0, ClassBreaks.ClassOf(3, new[] { 3.0, 3.0 }));
	}
}
