using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Sde.Core.Assessments;
using Sde.Data;

namespace Sde.Field.Tests;

/// <summary>
/// The phone form's protections from the 2026-10-28 security review: pairing however a path is spelled, number limits,
/// a Final assessment's structure, checked-out assessments and photo contents. Plus protections the review found
/// sound, kept so they stay that way.
/// </summary>
public class SecurityTests
{
	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	// ───────────────────────── Pairing ─────────────────────────

	[Theory]
	[InlineData("/API/structures?q=")]
	[InlineData("/Api/structures?q=")]
	[InlineData("/API/teams")]
	[InlineData("/API/lookups")]
	[InlineData("/API/session")]
	[InlineData("/api/structures/")]
	public async Task Unpaired_IsRefused_HoweverThePathIsSpelled(string path)
	{
		await using PhoneSession s = new PhoneSession();
		HttpResponseMessage res = await s.Client.GetAsync(path, Ct);
		Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
		Assert.DoesNotContain("Sample Road", await res.Content.ReadAsStringAsync(Ct));
	}

	[Fact]
	public async Task Unpaired_CantReadOrChangeAnAssessment_InAnyCase()
	{
		await using PhoneSession s = new PhoneSession();
		HttpResponseMessage read = await s.Client.GetAsync($"/Api/assessments/{s.AssessmentId}", Ct);
		Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
		Assert.DoesNotContain("Sample Road", await read.Content.ReadAsStringAsync(Ct));

		HttpRequestMessage change = new HttpRequestMessage(HttpMethod.Post, $"/API/assessments/{s.AssessmentId}/changes")
		{
			Content = JsonContent.Create(new { changes = new[] { new { key = "Property.City", value = "Elsewhere" } } }),
		};
		change.Headers.Add(FieldServer.CsrfHeader, "1");
		Assert.Equal(HttpStatusCode.Unauthorized, (await s.Client.SendAsync(change, Ct)).StatusCode);
		Assert.Equal("Testville", s.Store.GetProperty(s.Assessment.Property.PropertyID).City);
	}

	[Fact]
	public async Task Unpaired_PhotoUpload_NeverReachesTheConverter()
	{
		await using PhoneSession s = new PhoneSession();
		MultipartFormDataContent form = new MultipartFormDataContent();
		ByteArrayContent file = new ByteArrayContent(PhoneSession.Heic(4096));
		file.Headers.ContentType = new MediaTypeHeaderValue("image/heic");
		form.Add(file, "photo", "x.heic");
		HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, $"/API/assessments/{s.AssessmentId}/photos") { Content = form };
		req.Headers.Add(FieldServer.CsrfHeader, "1");
		Assert.Equal(HttpStatusCode.Unauthorized, (await s.Client.SendAsync(req, Ct)).StatusCode);
		Assert.Empty(s.Converter.Converted);
		Assert.Empty(s.Store.LoadAssessment(s.AssessmentId).Attachments);
	}

	[Fact]
	public async Task Unpaired_StillGetsThePageAndCanPair()
	{
		await using PhoneSession s = new PhoneSession();
		Assert.Equal(HttpStatusCode.OK, (await s.Client.GetAsync("/", Ct)).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await s.Client.GetAsync("/app.js", Ct)).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await s.PairAsync()).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await s.Client.GetAsync("/API/structures?q=", Ct)).StatusCode);
	}

	// ───────────────────────── Numbers ─────────────────────────

	[Fact]
	public async Task HugeNumber_IsRefused_AndTheAssessmentStillLoads()
	{
		await using PhoneSession s = new PhoneSession();
		await s.PairAsync();
		HttpResponseMessage res = await s.Change("Assessment.MarketAppraisal", decimal.MaxValue.ToString(CultureInfo.InvariantCulture));
		Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
		Assert.Contains("Professional market appraisal", await PhoneSession.Error(res));
		Assert.Null(Record.Exception(() => s.Store.LoadAssessment(s.AssessmentId)));
		Assert.Null(Record.Exception(() => s.Store.LoadAllAssessments()));
	}

	[Theory]
	[InlineData("Property.Latitude", "500")]
	[InlineData("Property.Longitude", "-181")]
	[InlineData("Property.YearOfConstruction", "-40")]
	[InlineData("Assessment.BaseCost", "-100")]
	[InlineData("Assessment.MarketAppraisal", "-1")]
	[InlineData("Assessment.GeographicAdjustment", "-0.5")]
	[InlineData("Property.BFE", "-99999999999")]
	public async Task NumbersOutsideTheEditorsRange_AreRefused(string key, string value)
	{
		await using PhoneSession s = new PhoneSession();
		await s.PairAsync();
		Assert.Equal(HttpStatusCode.BadRequest, (await s.Change(key, value)).StatusCode);
	}

	[Theory]
	[InlineData("Property.Latitude", "39.103118")]
	[InlineData("Property.YearOfConstruction", "1962")]
	[InlineData("Assessment.MarketAppraisal", "245000")]
	[InlineData("Property.BFE", "-4.5")]
	public async Task NumbersInRange_AreSaved(string key, string value)
	{
		await using PhoneSession s = new PhoneSession();
		await s.PairAsync();
		Assert.Equal(HttpStatusCode.OK, (await s.Change(key, value)).StatusCode);
	}

	// ───────────────────────── A structure's other assessments ─────────────────────────

	[Fact]
	public async Task SecondAssessment_OfAStructure_IsRefused()
	{
		await using PhoneSession s = new PhoneSession();
		await s.PairAsync();
		HttpResponseMessage res = await s.Post($"/api/properties/{s.Assessment.Property.PropertyID}/assessments", new { });
		Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
		Assert.Single(s.Store.GetAssessmentsForProperty(s.Assessment.Property.PropertyID));
	}

	[Fact]
	public async Task FinalAssessmentsStructure_CantChange_ThroughAnotherAssessment()
	{
		await using PhoneSession s = new PhoneSession();
		await s.PairAsync();
		Guid sibling = AddAssessmentOnTheMac(s);
		s.Store.SetStatus(s.AssessmentId, AssessmentStatus.Final);

		HttpResponseMessage res = await s.Change(sibling, "Property.StreetName", "Other Street");
		Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
		Assert.Contains("Another assessment of this structure is Final", await PhoneSession.Error(res));
		Assert.Equal("Sample Road", s.Store.LoadAssessment(s.AssessmentId).Property.StreetName);
		// The other assessment's own fields can still be changed.
		Assert.Equal(HttpStatusCode.OK, (await s.Change(sibling, "Assessment.InspectorName", "Pat Inspector")).StatusCode);
	}

	[Fact]
	public async Task CheckedOutAssessmentsStructure_CantChange_ThroughAnotherAssessment()
	{
		await using PhoneSession s = new PhoneSession();
		await s.PairAsync();
		Guid sibling = AddAssessmentOnTheMac(s);
		WorkPackages.CheckOut(s.Store, new[] { s.AssessmentId }, Path.Combine(s.Db.Folder, "out" + WorkPackages.Extension), "Team 1");

		HttpResponseMessage res = await s.Change(sibling, "Property.City", "Elsewhere");
		Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
		Assert.Contains("checked out", await PhoneSession.Error(res));
		Assert.Equal("Testville", s.Store.GetProperty(s.Assessment.Property.PropertyID).City);
	}

	private static Guid AddAssessmentOnTheMac(PhoneSession s)
	{
		AssessmentFile file = FieldApi.NewAssessment(s.Store, s.Assessment.Property.PropertyID);
		s.Store.SaveAssessment(file);
		return file.Assessment.AssessmentID;
	}

	// ───────────────────────── Checked-out assessments ─────────────────────────

	[Fact]
	public async Task CheckedOutAssessment_ShowsAsLocked_AndChangesGet409()
	{
		await using PhoneSession s = new PhoneSession();
		await s.PairAsync();
		WorkPackages.CheckOut(s.Store, new[] { s.AssessmentId }, Path.Combine(s.Db.Folder, "out" + WorkPackages.Extension), "Team 1");
		List<Exception> failed = new();
		s.Server.Failed += failed.Add;

		JsonElement view = await PhoneSession.Json(await s.Client.GetAsync($"/api/assessments/{s.AssessmentId}", Ct));
		Assert.True(view.GetProperty("locked").GetBoolean());
		Assert.Contains("checked out to Team 1", view.GetProperty("lockedMessage").GetString());

		Assert.Equal(HttpStatusCode.Conflict, (await s.Change("Property.City", "Elsewhere")).StatusCode);
		Assert.Equal(HttpStatusCode.Conflict, (await s.Upload(PhoneSession.Jpeg(4096), "a.jpg", "image/jpeg")).StatusCode);
		Assert.Equal("Testville", s.Store.GetProperty(s.Assessment.Property.PropertyID).City);
		Assert.Empty(s.Store.LoadAssessment(s.AssessmentId).Attachments);
		Assert.Empty(failed);
	}

	// ───────────────────────── Photo contents ─────────────────────────

	[Fact]
	public async Task FileThatOnlyStartsLikeAJpeg_IsRefused()
	{
		await using PhoneSession s = new PhoneSession();
		await s.PairAsync();
		byte[] notAPhoto = new byte[] { 0xFF, 0xD8, 0xFF }.Concat(Encoding.ASCII.GetBytes("<html><body>not a photo</body></html>")).ToArray();
		Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await s.Upload(notAPhoto, "x.jpg", "image/jpeg")).StatusCode);
		Assert.Empty(s.Store.LoadAssessment(s.AssessmentId).Attachments);
	}

	[Fact]
	public async Task FileThatOnlyStartsLikeAPng_IsRefused_AndARealPngHeaderIsKept()
	{
		await using PhoneSession s = new PhoneSession();
		await s.PairAsync();
		byte[] signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
		byte[] notAPhoto = signature.Concat(Encoding.ASCII.GetBytes("<html><body>not a photo</body></html>")).ToArray();
		Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await s.Upload(notAPhoto, "x.png", "image/png")).StatusCode);

		byte[] png = signature.Concat(new byte[] { 0, 0, 0, 13 }).Concat(Encoding.ASCII.GetBytes("IHDR"))
			.Concat(new byte[] { 0, 0, 0, 1, 0, 0, 0, 1, 8, 2, 0, 0, 0 }).Concat(new byte[64]).ToArray();
		Assert.Equal(HttpStatusCode.OK, (await s.Upload(png, "x.png", "image/png")).StatusCode);
	}

	[Theory]
	[InlineData(0xC0)]   // baseline
	[InlineData(0xC2)]   // progressive
	public async Task Jpeg_WithItsFrameHeaderAfterOtherSegments_IsKept(int frameMarker)
	{
		await using PhoneSession s = new PhoneSession();
		await s.PairAsync();
		// Start of image, an EXIF-sized APP1 segment, then the frame header: as phone cameras write them.
		List<byte> jpeg = new() { 0xFF, 0xD8, 0xFF, 0xE1, 0x10, 0x02 };
		jpeg.AddRange(new byte[0x1000]);
		jpeg.AddRange(new byte[] { 0xFF, (byte)frameMarker, 0x00, 0x11, 0x08, 0x0B, 0xD0, 0x0F, 0xC0, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01 });
		jpeg.AddRange(new byte[256]);
		Assert.Equal(HttpStatusCode.OK, (await s.Upload(jpeg.ToArray(), "IMG_0003.JPG", "image/jpeg")).StatusCode);
	}

	// ───────────────────────── Checked by the review and sound ─────────────────────────

	[Fact]
	public async Task Session_DoesNotSurviveARestart()
	{
		await using PhoneSession s = new PhoneSession();
		await s.PairAsync();
		Assert.Equal(HttpStatusCode.OK, (await s.Client.GetAsync("/api/session", Ct)).StatusCode);
		await s.Server.StopAsync();
		await s.Server.StartAsync(new FieldServerOptions { BindAddress = IPAddress.Loopback, TempFolder = Path.Combine(s.Db.Folder, "phone-temp2") });
		Assert.Equal(HttpStatusCode.Unauthorized, (await s.Client.GetAsync(new Uri(s.Server.BaseAddress, "/api/session"), Ct)).StatusCode);
	}

	[Fact]
	public async Task WrongPins_SentAtOnce_StillPausePinPairing()
	{
		await using PhoneSession s = new PhoneSession();
		string pin = s.Server.Pairing.Current.Pin;
		string wrong = pin == "123456" ? "654321" : "123456";
		HttpResponseMessage[] all = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => s.Post("/api/pair", new { pin = wrong, name = "X" })));
		Assert.Equal(4, all.Count(r => r.StatusCode == HttpStatusCode.Forbidden));
		Assert.Equal(36, all.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
		Assert.Equal(HttpStatusCode.TooManyRequests, (await s.Post("/api/pair", new { pin, name = "X" })).StatusCode);
	}

	[Fact]
	public async Task Preflight_IsRefused_WithoutCorsHeaders_AndErrorsCarryTheCsp()
	{
		await using PhoneSession s = new PhoneSession();
		HttpRequestMessage pre = new HttpRequestMessage(HttpMethod.Options, "/api/pair");
		pre.Headers.Add("Origin", "http://other.example");
		pre.Headers.Add("Access-Control-Request-Method", "POST");
		pre.Headers.Add("Access-Control-Request-Headers", "x-sde");
		HttpResponseMessage res = await s.Client.SendAsync(pre, Ct);
		Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
		Assert.False(res.Headers.Contains("Access-Control-Allow-Origin"));
		Assert.True(res.Headers.Contains("Content-Security-Policy"));
		Assert.True((await s.Client.GetAsync("/nope", Ct)).Headers.Contains("Content-Security-Policy"));
	}
}
