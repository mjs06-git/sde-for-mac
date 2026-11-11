using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Rendering;
using Mapsui.Rendering.Skia;
using Mapsui.Rendering.Skia.SkiaStyles;
using Mapsui.Styles;
using SkiaSharp;
using Color = Mapsui.Styles.Color;

namespace Sde.App.Maps;

/// <summary>A cluster of structures on the map: a circle sized by how many it holds, with the number in the middle.</summary>
public sealed class MapClusterStyle : BaseStyle
{
	public int Count { get; init; }

	public Color Fill { get; init; } = new Color(128, 128, 128);

	/// <summary>The number's colour.</summary>
	public Color Ink { get; init; } = Color.White;

	/// <summary>The ring round the circle (the card colour, so clusters stand out on any basemap).</summary>
	public Color Ring { get; init; } = Color.White;

	/// <summary>The circle's diameter in points for a cluster of <paramref name="count"/>: 26 for 2, growing slowly to 54.</summary>
	public static double DiameterFor(int count) => Math.Min(54, 22 + 8 * Math.Log10(Math.Max(2, count)) * 2.2);
}

/// <summary>Draws <see cref="MapClusterStyle"/>: a ringed circle with the count centred in the app's Inter font.</summary>
public sealed class MapClusterRenderer : ISkiaStyleRenderer
{
	/// <summary>Registers the renderer with the map engine (once; safe to call often).</summary>
	public static void Register()
	{
		if (Interlocked.Exchange(ref _registered, 1) == 0)
		{
			MapRenderer.RegisterStyleRenderer(typeof(MapClusterStyle), new MapClusterRenderer());
		}
	}

	private static int _registered;

	public bool Draw(SKCanvas canvas, Viewport viewport, ILayer layer, IFeature feature, IStyle style, RenderService renderService, long iteration)
	{
		if (style is not MapClusterStyle cluster || feature.Extent?.Centroid is not MPoint at)
		{
			return false;
		}
		(double sx, double sy) = viewport.WorldToScreenXY(at.X, at.Y);
		float radius = (float)(MapClusterStyle.DiameterFor(cluster.Count) / 2);
		float opacity = (float)layer.Opacity * cluster.Opacity;
		using SKPaint ring = new SKPaint { IsAntialias = true, Color = ToSkia(cluster.Ring, opacity) };
		canvas.DrawCircle((float)sx, (float)sy, radius + 2.5f, ring);
		using SKPaint fill = new SKPaint { IsAntialias = true, Color = ToSkia(cluster.Fill, opacity) };
		canvas.DrawCircle((float)sx, (float)sy, radius, fill);
		string text = cluster.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
		using SKFont font = new SKFont(MapLabelRenderer.Typeface, radius > 20 ? 13 : 12) { Embolden = true };
		float width = font.MeasureText(text);
		font.GetFontMetrics(out SKFontMetrics m);
		using SKPaint ink = new SKPaint { IsAntialias = true, Color = ToSkia(cluster.Ink, opacity) };
		canvas.DrawText(text, (float)sx - width / 2, (float)sy - (m.Ascent + m.Descent) / 2, font, ink);
		return true;
	}

	private static SKColor ToSkia(Color c, float opacity) =>
		new SKColor((byte)c.R, (byte)c.G, (byte)c.B, (byte)Math.Clamp((int)Math.Round(c.A * opacity), 0, 255));
}

/// <summary>The map engine's layers for the analysis results: clusters among the structure dots, and pictures over the basemap.</summary>
internal static class AnalysisLayers
{
	/// <summary>
	/// The structures as clusters: a cluster of one is that structure's own dot (in its own fill); larger ones are
	/// <see cref="MapClusterStyle"/> circles, drawn last so they sit over any single dot nearby.
	/// </summary>
	public static List<IFeature> Clusters(IReadOnlyList<MapClusterMark> clusters, IReadOnlyList<MapMarker> markers, MapTheme theme)
	{
		Dictionary<Guid, MapMarker> byId = new Dictionary<Guid, MapMarker>();
		foreach (MapMarker m in markers)
		{
			byId[m.Id] = m;
		}
		List<MapMarker> singles = new List<MapMarker>();
		List<IFeature> circles = new List<IFeature>();
		foreach (MapClusterMark c in clusters)
		{
			if (c.Count == 1 && c.Single is Guid id && byId.TryGetValue(id, out MapMarker single))
			{
				singles.Add(single);
				continue;
			}
			PointFeature f = new PointFeature(c.X, c.Y);
			f.Styles.Add(new MapClusterStyle
			{
				Count = c.Count,
				Fill = MapCanvas.ToEngine(c.Fill, new Color(128, 128, 128)),
				Ink = MapCanvas.ToEngine(c.Ink, Color.White),
				Ring = theme.DotOutline,
			});
			circles.Add(f);
		}
		List<IFeature> features = StructureDots.Dots(singles, theme);
		features.AddRange(circles);
		return features;
	}

	/// <summary>A picture over the basemap, as a raster layer at its opacity.</summary>
	public static MemoryLayer Raster(MapRasterOverlay overlay)
	{
		RasterFeature feature = new RasterFeature(new MRaster(overlay.Png, new MRect(overlay.MinX, overlay.MinY, overlay.MaxX, overlay.MaxY)));
		MemoryLayer layer = new MemoryLayer(overlay.Key)
		{
			Features = new List<IFeature> { feature },
			Style = new RasterStyle(),
			Opacity = overlay.Opacity,
		};
		layer.Attribution.Enabled = false;
		return layer;
	}
}
