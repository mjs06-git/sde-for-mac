namespace Sde.Gis.Analysis;

/// <summary>A point with a weight for a heat map, in the same coordinates as the extent given to <see cref="HeatmapRaster"/>.</summary>
/// <param name="X">Easting in map units.</param>
/// <param name="Y">Northing in map units.</param>
/// <param name="Weight">How much the point counts (1 for a plain count). Zero, negative and NaN weights are ignored.</param>
public readonly record struct HeatPoint(double X, double Y, double Weight);

/// <summary>The shape a point's influence takes as it fades with distance.</summary>
public enum HeatKernel
{
	/// <summary>Smooth with a finite reach: (1 - (d/r)^2)^2 inside the radius, 0 beyond it.</summary>
	Quartic,

	/// <summary>A bell curve with a standard deviation of a third of the radius, cut off at the radius.</summary>
	Gaussian,
}

/// <summary>One stop of a colour ramp: a position from 0 (lowest density) to 1 (highest) and the colour there.</summary>
public readonly record struct ColorStop(double Position, byte R, byte G, byte B);

/// <summary>
/// A density grid: <see cref="Values"/> holds <see cref="Width"/> times <see cref="Height"/> numbers from 0 to 1, row by
/// row, the first row being the top (north) edge of the extent.
/// </summary>
public sealed class HeatmapGrid
{
	public HeatmapGrid(int width, int height, float[] values, double maxRawDensity)
	{
		Width = width;
		Height = height;
		Values = values;
		MaxRawDensity = maxRawDensity;
	}

	public int Width { get; }

	public int Height { get; }

	/// <summary>Density per pixel, 0 to 1 (the busiest pixel is 1 unless there is nothing to show, when all are 0).</summary>
	public float[] Values { get; }

	/// <summary>The busiest pixel's density before it was scaled to 1, in summed weight units; 0 when the grid is empty.</summary>
	public double MaxRawDensity { get; }

	/// <summary>The density at a pixel (column, row from the top).</summary>
	public float this[int column, int row] => Values[row * Width + column];
}

/// <summary>
/// Turns weighted points into a heat map: a density grid and the pixels to draw it. No UI types are used, so the app
/// can wrap the pixels in whatever bitmap its map layer needs.
/// </summary>
public static class HeatmapRaster
{
	/// <summary>
	/// Builds the density grid for the extent. Each point is placed in its pixel, and every busy pixel then adds the
	/// kernel, scaled by its weight, to the pixels around it (so the cost is the number of busy pixels times the kernel
	/// area, not the number of points times the grid). The result is divided by its largest value, so 1 is the hottest
	/// pixel. Points outside the extent but within the radius still glow into the edge; farther ones are ignored.
	/// Different input orders give the same grid up to floating-point rounding (about one part in a million).
	/// </summary>
	/// <param name="points">The weighted points.</param>
	/// <param name="minX">West edge of the extent, map units.</param>
	/// <param name="minY">South edge of the extent.</param>
	/// <param name="maxX">East edge of the extent.</param>
	/// <param name="maxY">North edge of the extent.</param>
	/// <param name="width">Grid columns in pixels (1 or more).</param>
	/// <param name="height">Grid rows in pixels (1 or more).</param>
	/// <param name="radiusPixels">How far a point's glow reaches, in pixels (1 or more).</param>
	/// <param name="kernel">The glow's shape.</param>
	public static HeatmapGrid Compute(IEnumerable<HeatPoint> points, double minX, double minY, double maxX, double maxY, int width, int height, int radiusPixels, HeatKernel kernel = HeatKernel.Quartic)
	{
		if (width < 1 || height < 1)
		{
			throw new ArgumentOutOfRangeException(nameof(width), "The grid must be at least one pixel wide and high.");
		}
		if (radiusPixels < 1)
		{
			throw new ArgumentOutOfRangeException(nameof(radiusPixels), "The radius must be at least one pixel.");
		}
		if (!(maxX > minX) || !(maxY > minY) || !double.IsFinite(minX) || !double.IsFinite(maxX) || !double.IsFinite(minY) || !double.IsFinite(maxY))
		{
			throw new ArgumentException("The extent must have a positive width and height.");
		}

		// Place each point in its pixel on a grid padded by the radius, so points just outside still count.
		int r = radiusPixels;
		int pw = width + 2 * r;
		int ph = height + 2 * r;
		double[] bins = new double[(long)pw * ph];
		double sx = width / (maxX - minX);
		double sy = height / (maxY - minY);
		foreach (HeatPoint p in points ?? Enumerable.Empty<HeatPoint>())
		{
			if (!(p.Weight > 0) || !double.IsFinite(p.Weight) || !double.IsFinite(p.X) || !double.IsFinite(p.Y))
			{
				continue;
			}
			double fx = Math.Floor((p.X - minX) * sx);
			double fy = Math.Floor((maxY - p.Y) * sy);
			if (fx < -r || fx >= width + r || fy < -r || fy >= height + r)
			{
				continue;
			}
			bins[(long)((int)fy + r) * pw + (int)fx + r] += p.Weight;
		}

		// The kernel as a table of (2r+1) square.
		int k = 2 * r + 1;
		float[] table = new float[k * k];
		for (int dy = -r; dy <= r; dy++)
		{
			for (int dx = -r; dx <= r; dx++)
			{
				double d2 = (double)(dx * dx + dy * dy) / ((double)r * r);
				double v = d2 >= 1 ? 0
					: kernel == HeatKernel.Gaussian ? Math.Exp(-0.5 * d2 * 9.0) // d / sigma = 3 d / r
					: (1 - d2) * (1 - d2);
				table[(dy + r) * k + dx + r] = (float)v;
			}
		}

		float[] density = new float[width * height];
		for (int by = 0; by < ph; by++)
		{
			for (int bx = 0; bx < pw; bx++)
			{
				double w = bins[(long)by * pw + bx];
				if (w == 0)
				{
					continue;
				}
				float fw = (float)w;
				int cy = by - r;
				int cx = bx - r;
				int y0 = Math.Max(0, cy - r);
				int y1 = Math.Min(height - 1, cy + r);
				int x0 = Math.Max(0, cx - r);
				int x1 = Math.Min(width - 1, cx + r);
				for (int y = y0; y <= y1; y++)
				{
					int rowOut = y * width;
					int rowTable = (y - cy + r) * k + r - cx;
					for (int x = x0; x <= x1; x++)
					{
						density[rowOut + x] += fw * table[rowTable + x];
					}
				}
			}
		}

		float max = 0;
		for (int i = 0; i < density.Length; i++)
		{
			if (density[i] > max)
			{
				max = density[i];
			}
		}
		if (max > 0)
		{
			float inv = 1f / max;
			for (int i = 0; i < density.Length; i++)
			{
				density[i] = Math.Min(1f, density[i] * inv);
			}
		}
		return new HeatmapGrid(width, height, density, max);
	}

	/// <summary>
	/// Colours a density grid. Returns 4 bytes per pixel (red, green, blue, alpha), row by row from the top, 8 bits each.
	/// The colour comes from the ramp at the pixel's density (blended between neighbouring stops; the ramp need not
	/// start at 0 or end at 1, the nearest stop is held), and the alpha is the density times <paramref name="maxAlpha"/>,
	/// so empty areas are fully clear. With <paramref name="premultiplied"/> the colour is multiplied by the alpha, which
	/// is what most bitmap types expect.
	/// </summary>
	/// <param name="grid">The density grid.</param>
	/// <param name="ramp">The colour stops, in any order; at least one.</param>
	/// <param name="maxAlpha">The alpha of the hottest pixel (0 to 255).</param>
	/// <param name="premultiplied">True for premultiplied alpha, false for straight alpha.</param>
	public static byte[] ToRgba(HeatmapGrid grid, ColorStop[] ramp, byte maxAlpha = 255, bool premultiplied = false)
	{
		ArgumentNullException.ThrowIfNull(grid);
		if (ramp == null || ramp.Length == 0)
		{
			throw new ArgumentException("The colour ramp needs at least one stop.", nameof(ramp));
		}
		ColorStop[] stops = ramp.OrderBy(s => s.Position).ToArray();

		// A 256-step lookup, so the per-pixel work is a table read.
		(byte R, byte G, byte B)[] lut = new (byte, byte, byte)[256];
		for (int i = 0; i < 256; i++)
		{
			lut[i] = Colour(stops, i / 255.0);
		}

		byte[] rgba = new byte[grid.Values.Length * 4];
		for (int i = 0; i < grid.Values.Length; i++)
		{
			float d = grid.Values[i];
			if (!(d > 0))
			{
				continue;   // all four bytes stay 0: clear
			}
			d = Math.Min(1f, d);
			int alpha = (int)Math.Round(d * maxAlpha);
			(byte R, byte G, byte B) c = lut[(int)Math.Round(d * 255)];
			int o = i * 4;
			if (premultiplied)
			{
				rgba[o] = (byte)((c.R * alpha + 127) / 255);
				rgba[o + 1] = (byte)((c.G * alpha + 127) / 255);
				rgba[o + 2] = (byte)((c.B * alpha + 127) / 255);
			}
			else
			{
				rgba[o] = c.R;
				rgba[o + 1] = c.G;
				rgba[o + 2] = c.B;
			}
			rgba[o + 3] = (byte)alpha;
		}
		return rgba;
	}

	private static (byte, byte, byte) Colour(ColorStop[] stops, double t)
	{
		if (t <= stops[0].Position)
		{
			return (stops[0].R, stops[0].G, stops[0].B);
		}
		for (int i = 1; i < stops.Length; i++)
		{
			if (t <= stops[i].Position)
			{
				ColorStop a = stops[i - 1];
				ColorStop b = stops[i];
				double span = b.Position - a.Position;
				double f = span <= 0 ? 1 : (t - a.Position) / span;
				return (Mix(a.R, b.R, f), Mix(a.G, b.G, f), Mix(a.B, b.B, f));
			}
		}
		ColorStop last = stops[^1];
		return (last.R, last.G, last.B);
	}

	private static byte Mix(byte a, byte b, double f) => (byte)Math.Round(a + (b - a) * f);
}
