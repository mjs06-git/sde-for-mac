using System.Net;
using System.Text.Json;
using NetTopologySuite.Geometries;
using Sde.App.Maps;
using Sde.Gis.Features;
using Sde.Gis.Styles;

namespace Sde.App.Tests;

/// <summary>
/// Loopback server behind the Option B native WKWebView map: strict token/host checks, bundled offline MapLibre
/// assets, TileCache tile proxying, and styled GeoJSON feature-layer streaming.
/// </summary>
public class MapWebServerTests
{
	[Fact]
	public async Task RefusesMissingOrWrongToken_AndServesBundledAssetsWithToken()
	{
		CancellationToken ct = TestContext.Current.CancellationToken;
		await using MapWebServer server = await MapWebServer.StartAsync();
		using HttpClient client = new();

		// No token -> 403
		using HttpResponseMessage noToken = await client.GetAsync(server.BaseUrl + "/", ct);
		Assert.Equal(HttpStatusCode.Forbidden, noToken.StatusCode);

		// Wrong token -> 403
		using HttpResponseMessage badToken = await client.GetAsync(server.BaseUrl + "/wrong/", ct);
		Assert.Equal(HttpStatusCode.Forbidden, badToken.StatusCode);

		// Valid token prefix serves bundled Public Sans fonts
		foreach (string path in new[] { "fonts/PublicSans-Regular.ttf", "fonts/PublicSans-SemiBold.ttf" })
		{
			using HttpResponseMessage res = await client.GetAsync($"{server.EntryUrl}{path}", ct);
			Assert.Equal(HttpStatusCode.OK, res.StatusCode);
			byte[] bytes = await res.Content.ReadAsByteArrayAsync(ct);
			Assert.True(bytes.Length > 100, $"{path} should be non-empty");
		}
	}

	[Fact]
	public async Task ProxiesTilesThroughTileCache_AndStreamsStyledFeatureGeoJson()
	{
		CancellationToken ct = TestContext.Current.CancellationToken;
		byte[] fakePng = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 };
		string tempDir = Path.Combine(Path.GetTempPath(), "sde-mapweb-test-" + Guid.NewGuid().ToString("N"));
		try
		{
			TileCache cache = new(tempDir, (_, _) => Task.FromResult(fakePng));
			await using MapWebServer server = await MapWebServer.StartAsync();
			server.Tiles = cache;

			using HttpClient client = new();

			// Tile proxy hits our fake TileCache downloader and returns image/png
			using HttpResponseMessage tile = await client.GetAsync($"{server.EntryUrl}tiles/usgs-topo/12/1086/1566", ct);
			Assert.Equal(HttpStatusCode.OK, tile.StatusCode);
			Assert.Equal("image/png", tile.Content.Headers.ContentType?.MediaType);
			Assert.Equal(fakePng, await tile.Content.ReadAsByteArrayAsync(ct));
			Assert.Equal(1, cache.Downloads);

			// Feature layer GeoJSON includes resolved style and selection flags
			FeatureLayer fl = new("flood", GeometryKind.Point);
			fl.AddField("Zone", FieldType.Text);
			Feature feat = fl.AddFeature(new Point(-84.512, 39.103), id: 42);
			feat[0] = "AE";
			server.SetFeatureLayers(new[]
			{
				new MapFeatureLayer
				{
					Key = "flood-layer",
					Name = "Flood",
					Data = fl,
					Style = LayerStyle.SingleSymbol(new Symbol { Fill = "#CC3311", Stroke = "#112233", StrokeWidth = 2 }),
					Labels = new LabelStyle { Field = "Zone" },
					Selected = new long[] { 42 },
				},
			});

			using HttpResponseMessage geo = await client.GetAsync($"{server.EntryUrl}layers/flood-layer.geojson", ct);
			Assert.Equal(HttpStatusCode.OK, geo.StatusCode);
			using JsonDocument doc = JsonDocument.Parse(await geo.Content.ReadAsStringAsync(ct));
			JsonElement f0 = doc.RootElement.GetProperty("features")[0];
			JsonElement props = f0.GetProperty("properties");
			Assert.Equal(42, props.GetProperty("_id").GetInt64());
			Assert.Equal("#CC3311", props.GetProperty("_fill").GetString());
			Assert.Equal("#112233", props.GetProperty("_stroke").GetString());
			Assert.True(props.GetProperty("_selected").GetBoolean());
			Assert.Equal("AE", props.GetProperty("_label").GetString());
		}
		finally
		{
			if (Directory.Exists(tempDir))
			{
				Directory.Delete(tempDir, recursive: true);
			}
		}
	}
}
