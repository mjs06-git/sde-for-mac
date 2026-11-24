using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using NetTopologySuite.IO.Converters;
using Sde.Gis.Features;
using Sde.Gis.Styles;

namespace Sde.App.Maps;

/// <summary>
/// Loopback-only HTTP server backing the embedded MapLibre GL JS <c>WKWebView</c>.
/// Serves the bundled offline MapLibre assets, proxies map tiles through SDE's <see cref="TileCache"/> (so disk
/// caching, offline areas, OSM User-Agent rules, and FEMA NFHL <c>{bbox}</c> overlays work identically), and streams
/// <see cref="MapFeatureLayer"/> GeoJSON and <see cref="MapRasterOverlay"/> PNGs directly from memory.
/// </summary>
public sealed class MapWebServer : IAsyncDisposable
{
	private static readonly JsonSerializerOptions GeoJsonOptions = CreateGeoJsonOptions();

	private static readonly Dictionary<string, (string Resource, string ContentType)> StaticFiles = new(StringComparer.Ordinal)
	{
		["/"] = ("Sde.App.Maps.Web.index.html", "text/html; charset=utf-8"),
		["/index.html"] = ("Sde.App.Maps.Web.index.html", "text/html; charset=utf-8"),
		["/map.css"] = ("Sde.App.Maps.Web.map.css", "text/css; charset=utf-8"),
		["/hud.js"] = ("Sde.App.Maps.Web.hud.js", "application/javascript; charset=utf-8"),
		["/map.js"] = ("Sde.App.Maps.Web.map.js", "application/javascript; charset=utf-8"),
		["/maplibre-gl.css"] = ("Sde.App.Maps.Web.maplibre-gl.css", "text/css; charset=utf-8"),
		["/maplibre-gl.js"] = ("Sde.App.Maps.Web.maplibre-gl.js", "application/javascript; charset=utf-8"),
		["/fonts/PublicSans-Regular.ttf"] = ("Sde.App.Maps.Web.fonts.PublicSans-Regular.ttf", "font/ttf"),
		["/fonts/PublicSans-SemiBold.ttf"] = ("Sde.App.Maps.Web.fonts.PublicSans-SemiBold.ttf", "font/ttf"),
	};

	private readonly WebApplication _app;
	private readonly string _token;
	private readonly string _expectedHost;
	private readonly ConcurrentDictionary<string, Basemap> _tileSources = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, MapFeatureLayer> _featureLayers = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, MapRasterOverlay> _rasters = new(StringComparer.Ordinal);
	private volatile TileCache _tiles;

	private MapWebServer(WebApplication app, int port, string token)
	{
		_app = app;
		Port = port;
		_token = token;
		_expectedHost = $"127.0.0.1:{port}";
		BaseUrl = $"http://127.0.0.1:{port}";
		EntryUrl = $"{BaseUrl}/{token}/";
	}

	public int Port { get; }

	public string BaseUrl { get; }

	/// <summary>Initial URL loaded into <c>WKWebView</c>, carrying the per-launch secret token.</summary>
	public string EntryUrl { get; }

	public TileCache Tiles
	{
		get => _tiles;
		set => _tiles = value;
	}

	/// <summary>Registers a basemap or tile overlay so <c>/tiles/{id}/{z}/{x}/{y}</c> can serve it via <see cref="TileCache"/>.</summary>
	public void RegisterTileSource(Basemap source)
	{
		if (source != null && !string.IsNullOrEmpty(source.Id))
		{
			_tileSources[source.Id] = source;
		}
	}

	/// <summary>Updates the set of vector feature layers available at <c>/layers/{key}.geojson</c>.</summary>
	public void SetFeatureLayers(IEnumerable<MapFeatureLayer> layers)
	{
		HashSet<string> keep = new(StringComparer.Ordinal);
		foreach (MapFeatureLayer layer in layers)
		{
			if (!string.IsNullOrEmpty(layer.Key))
			{
				_featureLayers[layer.Key] = layer;
				keep.Add(layer.Key);
			}
		}
		foreach (string key in _featureLayers.Keys)
		{
			if (!keep.Contains(key))
			{
				_featureLayers.TryRemove(key, out _);
			}
		}
	}

	/// <summary>Updates the raster overlays (water surface, etc.) available at <c>/rasters/{key}.png</c>.</summary>
	public void SetRasters(IEnumerable<MapRasterOverlay> rasters)
	{
		HashSet<string> keep = new(StringComparer.Ordinal);
		foreach (MapRasterOverlay r in rasters)
		{
			if (!string.IsNullOrEmpty(r.Key))
			{
				_rasters[r.Key] = r;
				keep.Add(r.Key);
			}
		}
		foreach (string key in _rasters.Keys)
		{
			if (!keep.Contains(key))
			{
				_rasters.TryRemove(key, out _);
			}
		}
	}

	public static async Task<MapWebServer> StartAsync()
	{
		string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
		WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
		builder.Logging.ClearProviders();
		builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
		WebApplication app = builder.Build();

		MapWebServer server = null;

		// Security gate: loopback only, exact Host header, and per-launch secret token in the first path segment
		// (/{token}/...). Avoids cookies over plain loopback HTTP while protecting every subresource request.
		app.Use(async (ctx, next) =>
		{
			if (server == null)
			{
				ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
				return;
			}
			IPAddress remote = ctx.Connection.RemoteIpAddress;
			if (remote == null || !IPAddress.IsLoopback(remote))
			{
				ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
				return;
			}
			if (!string.Equals(ctx.Request.Host.Value, server._expectedHost, StringComparison.Ordinal))
			{
				ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
				return;
			}
			string rawPath = ctx.Request.Path.Value ?? "";
			if (!HasValidTokenPrefix(rawPath, server._token))
			{
				ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
				return;
			}
			await next();
		});

		RouteGroupBuilder routes = app.MapGroup($"/{token}");
		Assembly asm = typeof(MapWebServer).Assembly;

		foreach ((string route, (string resource, string contentType)) in StaticFiles)
		{
			string resName = resource;
			string mime = contentType;
			routes.MapGet(route, async ctx =>
			{
				await using Stream stream = asm.GetManifestResourceStream(resName);
				if (stream == null)
				{
					ctx.Response.StatusCode = StatusCodes.Status404NotFound;
					return;
				}
				ctx.Response.ContentType = mime;
				ctx.Response.Headers.CacheControl = "no-store";
				await stream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
			});
		}

		// Map tile proxy through SDE's TileCache (supports Basemaps + FEMA NFHL overlays + offline disk cache).
		routes.MapGet("/tiles/{sourceId}/{z:int}/{x:int}/{y:int}", async (HttpContext ctx, string sourceId, int z, int x, int y) =>
		{
			TileCache cache = server._tiles;
			if (cache == null || !server._tileSources.TryGetValue(sourceId, out Basemap source) || !source.HasTiles)
			{
				ctx.Response.StatusCode = StatusCodes.Status204NoContent;
				return;
			}
			int scale = int.TryParse(ctx.Request.Query["scale"].FirstOrDefault(), out int s) && s >= 2 ? 2 : 1;
			TileFetch fetched;
			try
			{
				fetched = await cache.FetchAsync(source, z, x, y, scale, ctx.RequestAborted);
			}
			catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
			{
				return;
			}
			if (fetched.Bytes == null || fetched.Bytes.Length == 0)
			{
				ctx.Response.StatusCode = StatusCodes.Status204NoContent;
				return;
			}
			ctx.Response.ContentType = GuessImageType(fetched.Bytes);
			ctx.Response.Headers.CacheControl = "private, max-age=3600";
			await ctx.Response.Body.WriteAsync(fetched.Bytes, ctx.RequestAborted);
		});

		// GIS feature layers serialized as styled GeoJSON.
		routes.MapGet("/layers/{key}.geojson", async (HttpContext ctx, string key) =>
		{
			if (!server._featureLayers.TryGetValue(key, out MapFeatureLayer layer) || layer.Data == null)
			{
				ctx.Response.StatusCode = StatusCodes.Status404NotFound;
				return;
			}
			ctx.Response.ContentType = "application/geo+json; charset=utf-8";
			ctx.Response.Headers.CacheControl = "no-store";
			byte[] json = SerializeFeatureLayer(layer);
			await ctx.Response.Body.WriteAsync(json, ctx.RequestAborted);
		});

		// Raster overlays (water surface, etc.).
		routes.MapGet("/rasters/{key}.png", async (HttpContext ctx, string key) =>
		{
			if (!server._rasters.TryGetValue(key, out MapRasterOverlay raster) || raster.Png == null)
			{
				ctx.Response.StatusCode = StatusCodes.Status404NotFound;
				return;
			}
			ctx.Response.ContentType = "image/png";
			ctx.Response.Headers.CacheControl = "no-store";
			await ctx.Response.Body.WriteAsync(raster.Png, ctx.RequestAborted);
		});

		await app.StartAsync();
		string addr = app.Urls.First();
		int port = new Uri(addr).Port;
		server = new MapWebServer(app, port, token);
		foreach (Basemap b in Basemaps.All)
		{
			server.RegisterTileSource(b);
		}
		server.RegisterTileSource(TileOverlays.FloodZones);
		return server;
	}

	private static byte[] SerializeFeatureLayer(MapFeatureLayer layer)
	{
		HashSet<long> hidden = layer.Edits?.Hidden is { Count: > 0 } h ? h as HashSet<long> ?? h.ToHashSet() : null;
		HashSet<long> selected = layer.Selected is { Count: > 0 } s ? s as HashSet<long> ?? s.ToHashSet() : null;
		LayerStyle style = layer.Style ?? new LayerStyle();
		LabelStyle labels = layer.Labels;
		int labelIndex = !string.IsNullOrEmpty(labels?.Field) ? layer.Data.Fields.IndexOf(labels.Field) : -1;

		using MemoryStream ms = new();
		using (Utf8JsonWriter w = new(ms))
		{
			w.WriteStartObject();
			w.WriteString("type", "FeatureCollection");
			w.WritePropertyName("features");
			w.WriteStartArray();

			IEnumerable<Feature> source = layer.Data.Features;
			if (layer.Edits?.Drawn is { Count: > 0 } drawn)
			{
				source = source.Where(f => hidden == null || !hidden.Contains(f.Id)).Concat(drawn);
			}
			else if (hidden != null)
			{
				source = source.Where(f => !hidden.Contains(f.Id));
			}

			foreach (Feature f in source)
			{
				if (f.Geometry == null || f.Geometry.IsEmpty)
				{
					continue;
				}
				Symbol sym = style.SymbolFor(f);
				if (sym == null)
				{
					continue;
				}
				w.WriteStartObject();
				w.WriteString("type", "Feature");
				w.WriteNumber("id", f.Id);
				w.WritePropertyName("geometry");
				JsonSerializer.Serialize(w, f.Geometry, GeoJsonOptions);
				w.WritePropertyName("properties");
				w.WriteStartObject();
				w.WriteNumber("_id", f.Id);
				w.WriteString("_fill", NormalizeCssColor(sym.Fill, "#2F6FB3"));
				w.WriteNumber("_fillOpacity", ExtractAlpha(sym.Fill, 0.35));
				w.WriteString("_stroke", NormalizeCssColor(sym.Stroke, "#1B3F66"));
				w.WriteNumber("_strokeOpacity", ExtractAlpha(sym.Stroke, 0.9));
				w.WriteNumber("_strokeWidth", Math.Max(0.5, sym.StrokeWidth));
				w.WriteNumber("_pointRadius", Math.Max(3, sym.PointSize / 2.0));
				w.WriteBoolean("_selected", selected != null && selected.Contains(f.Id));
				if (labelIndex >= 0 && f[labelIndex] is object lv)
				{
					w.WriteString("_label", FieldValues.Format(lv));
				}
				w.WriteEndObject();
				w.WriteEndObject();
			}

			w.WriteEndArray();
			w.WriteEndObject();
		}
		return ms.ToArray();
	}

	private static string NormalizeCssColor(string hex, string fallback)
	{
		if (string.IsNullOrWhiteSpace(hex))
		{
			return fallback;
		}
		string h = hex.Trim();
		if (h.StartsWith('#') && h.Length == 9)
		{
			// Strip alpha for MapLibre paint color (alpha goes in *-opacity).
			return h[..7];
		}
		return h;
	}

	private static double ExtractAlpha(string hex, double defaultAlpha)
	{
		if (!string.IsNullOrWhiteSpace(hex))
		{
			string h = hex.Trim();
			if (h.StartsWith('#') && h.Length == 9 && byte.TryParse(h.AsSpan(7, 2), System.Globalization.NumberStyles.HexNumber, null, out byte a))
			{
				return a / 255.0;
			}
			if (h.StartsWith('#') && h.Length == 7)
			{
				return 1.0;
			}
		}
		return defaultAlpha;
	}

	private static string GuessImageType(byte[] bytes)
	{
		if (bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
		{
			return "image/png";
		}
		if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
		{
			return "image/jpeg";
		}
		if (bytes.Length >= 4 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46)
		{
			return "image/webp";
		}
		return "image/png";
	}

	private static bool HasValidTokenPrefix(string path, string expectedToken)
	{
		if (string.IsNullOrEmpty(path) || path[0] != '/')
		{
			return false;
		}
		int slash = path.IndexOf('/', 1);
		string segment = slash < 0 ? path[1..] : path[1..slash];
		return FixedTimeEquals(segment, expectedToken);
	}

	private static bool FixedTimeEquals(string a, string b)
	{
		if (a == null || b == null || a.Length != b.Length)
		{
			return false;
		}
		return CryptographicOperations.FixedTimeEquals(
			System.Text.Encoding.UTF8.GetBytes(a),
			System.Text.Encoding.UTF8.GetBytes(b));
	}

	private static JsonSerializerOptions CreateGeoJsonOptions()
	{
		JsonSerializerOptions opts = new();
		opts.Converters.Add(new GeoJsonConverterFactory());
		return opts;
	}

	public async ValueTask DisposeAsync()
	{
		try
		{
			await _app.StopAsync();
		}
		catch
		{
			// Best effort on shutdown.
		}
		await _app.DisposeAsync();
	}
}
