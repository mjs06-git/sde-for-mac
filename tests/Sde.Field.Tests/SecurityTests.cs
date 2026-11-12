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

	// ───────────────────────── The page's files ─────────────────────────

	[Fact]
	public void PageFiles_AreExactlyThese()
	{
		string[] expected = { "app.css", "app.js", "fonts/PublicSans-Regular.ttf", "fonts/PublicSans-SemiBold.ttf", "index.html" };
		Assert.Equal(expected, FieldServer.AssetFiles.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
	}

	[Theory]
	[InlineData("/fonts/PublicSans-Regular.ttf")]
	[InlineData("/fonts/PublicSans-SemiBold.ttf")]
	public async Task Fonts_AreServedFromTheMac_LikeThePagesOtherFiles(string path)
	{
		await using PhoneSession s = new PhoneSession();
		// Before pairing too: the pairing screen uses them.
		HttpResponseMessage res = await s.Client.GetAsync(path, Ct);
		Assert.Equal(HttpStatusCode.OK, res.StatusCode);
		Assert.Equal("font/ttf", res.Content.Headers.ContentType?.MediaType);
		byte[] body = await res.Content.ReadAsByteArrayAsync(Ct);
		Assert.True(body.Length > 10_000);
		Assert.Equal(new byte[] { 0x00, 0x01, 0x00, 0x00 }, body.Take(4).ToArray());   // a TrueType font
		// The same headers as every other response: never cached, never sniffed, the same policy.
		Assert.Equal("no-store", res.Headers.CacheControl?.ToString());
		Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
		Assert.Contains("default-src 'self'", res.Headers.GetValues("Content-Security-Policy").Single());
	}

	[Fact]
	public async Task Stylesheet_LoadsFontsOnlyFromTheMac()
	{
		await using PhoneSession s = new PhoneSession();
		string css = await s.Client.GetStringAsync("/app.css", Ct);
		Assert.Contains("url(\"/fonts/PublicSans-Regular.ttf\")", css);
		Assert.Contains("url(\"/fonts/PublicSans-SemiBold.ttf\")", css);
		Assert.Contains("font-display: swap", css);
		Assert.DoesNotContain("://", css);
		Assert.DoesNotContain("@import", css);
	}

	[Theory]
	[InlineData("/fonts/PublicSans-Bold.ttf")]
	[InlineData("/fonts/PublicSans-Medium.ttf")]
	[InlineData("/fonts/OFL.txt")]
	[InlineData("/fonts/")]
	[InlineData("/fonts")]
	[InlineData("/fonts/PublicSans-Regular.ttf.map")]
	[InlineData("/fonts/PublicSans-Regular.ttf/x")]
	[InlineData("/fonts/..%2fapp.css")]
	[InlineData("/fonts/..%2F..%2FFieldServer.cs")]
	[InlineData("/fonts/%2e%2e%2f%2e%2e%2fSde.Field.csproj")]
	[InlineData("/fonts/..%5c..%5cSde.Field.csproj")]
	[InlineData("/wwwroot/fonts/PublicSans-Regular.ttf")]
	[InlineData("/Sde.Field.wwwroot.fonts.PublicSans-Regular.ttf")]
	[InlineData("/wwwroot/app.css")]
	public async Task AnyOtherFile_IsNotServed(string path)
	{
		await using PhoneSession s = new PhoneSession();
		Assert.Equal(HttpStatusCode.Unauthorized, (await s.Client.GetAsync(path, Ct)).StatusCode);
		await s.PairAsync();
		HttpResponseMessage res = await s.Client.GetAsync(path, Ct);
		Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
		Assert.Equal(0, (await res.Content.ReadAsByteArrayAsync(Ct)).Length);
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
	public async Task SecondAssessment_OfAStructure_IsRefused_UnlessThePhoneAsksForAnother()
	{
		await using PhoneSession s = new PhoneSession();
		await s.PairAsync();
		Guid propertyId = s.Assessment.Property.PropertyID;
		HttpResponseMessage res = await s.Post($"/api/properties/{propertyId}/assessments", new { });
		Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
		Assert.Equal(HttpStatusCode.Conflict, (await s.Post($"/api/properties/{propertyId}/assessments", new { another = false })).StatusCode);
		HttpRequestMessage empty = new HttpRequestMessage(HttpMethod.Post, $"/api/properties/{propertyId}/assessments");
		empty.Headers.Add(FieldServer.CsrfHeader, "1");
		Assert.Equal(HttpStatusCode.Conflict, (await s.Client.SendAsync(empty, Ct)).StatusCode);
		Assert.Single(s.Store.GetAssessmentsForProperty(propertyId));
	}

	[Fact]
	public async Task AnotherAssessment_NeedsPairingAndTheHeader_AndAStructureThatExists()
	{
		await using PhoneSession s = new PhoneSession();
		Guid propertyId = s.Assessment.Property.PropertyID;
		Assert.Equal(HttpStatusCode.Unauthorized, (await s.Post($"/API/properties/{propertyId}/assessments", new { another = true })).StatusCode);
		await s.PairAsync();
		Assert.Equal(HttpStatusCode.Forbidden, (await s.Post($"/api/properties/{propertyId}/assessments", new { another = true }, csrf: false)).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await s.Post($"/api/properties/{Guid.NewGuid()}/assessments", new { another = true })).StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, (await s.Post($"/api/properties/{propertyId}/assessments", new { another = "lots" })).StatusCode);
		Assert.Single(s.Store.GetAssessmentsForProperty(propertyId));
	}

	// ───────────────────────── Element damage, cost adjustments and status ─────────────────────────

	/// <summary>A request to each new endpoint that would be fine from a paired phone on an open assessment.</summary>
	public static TheoryData<string, string> GoodRequests() => new()
	{
		{ "elements", """{"elements":[{"id":4,"damaged":50}]}""" },
		{ "costs", """{"additional":[{"line":1,"name":"Deck","quantity":1,"unitCost":1000}]}""" },
		{ "status", """{"status":"Reviewed"}""" },
	};

	private static Task<HttpResponseMessage> PostRaw(PhoneSession s, string path, string json, bool csrf = true)
	{
		HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
		if (csrf)
		{
			req.Headers.Add(FieldServer.CsrfHeader, "1");
		}
		return s.Client.SendAsync(req, Ct);
	}

	/// <summary>The parts of an assessment the new endpoints change, as text, to show nothing changed.</summary>
	private static string Snapshot(PhoneSession s)
	{
		AssessmentFile f = s.Store.LoadAssessment(s.AssessmentId);
		return string.Join("|", f.ElementPercentages.OrderBy(e => e.CalcID).Select(e => e.CalcID + "=" + e.UserDamage))
			+ "|" + string.Join("|", f.CostAdjustments.OrderBy(c => c.CostAdjustmentLookupID).Select(c => c.CostAdjustmentLookupID + "=" + c.Quantity + "x" + c.UnitCost))
			+ "|" + string.Join("|", f.AdditionalAdjustments.OrderBy(x => x.zOrder).Select(x => x.zOrder + "=" + x.AdjustmentName + ":" + x.Quantity + "x" + x.UnitCost))
			+ "|" + f.Assessment.PercentDamage + "|" + s.Store.GetStatus(s.AssessmentId).Status;
	}

	[Theory]
	[MemberData(nameof(GoodRequests))]
	public async Task NewEndpoints_RefuseUnpairedPhones_HoweverThePathIsSpelled(string what, string json)
	{
		await using PhoneSession s = new PhoneSession();
		string before = Snapshot(s);
		Assert.Equal(HttpStatusCode.Unauthorized, (await PostRaw(s, $"/api/assessments/{s.AssessmentId}/{what}", json)).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await PostRaw(s, $"/API/assessments/{s.AssessmentId}/{what.ToUpperInvariant()}", json)).StatusCode);
		s.Cookies.Add(s.Server.BaseAddress, new Cookie(FieldServer.CookieName, "made-up"));
		Assert.Equal(HttpStatusCode.Unauthorized, (await PostRaw(s, $"/api/assessments/{s.AssessmentId}/{what}", json)).StatusCode);
		Assert.Equal(before, Snapshot(s));
		Assert.Empty(s.Store.GetHistory(s.AssessmentId).Where(h => !h.IsEvent));
	}

	[Theory]
	[MemberData(nameof(GoodRequests))]
	public async Task NewEndpoints_NeedTheCsrfHeader(string what, string json)
	{
		await using PhoneSession s = new PhoneSession();
		await s.PairAsync();
		string before = Snapshot(s);
		Assert.Equal(HttpStatusCode.Forbidden, (await PostRaw(s, $"/api/assessments/{s.AssessmentId}/{what}", json, csrf: false)).StatusCode);
		Assert.Equal(before, Snapshot(s));
		// And the same request with it works, so the refusal was the header's.
		Assert.Equal(HttpStatusCode.OK, (await PostRaw(s, $"/api/assessments/{s.AssessmentId}/{what}", json)).StatusCode);
	}

	[Theory]
	[MemberData(nameof(GoodRequests))]
	public async Task NewEndpoints_RefuseALockedAssessment_AndLeaveItUnchanged(string what, string json)
	{
		await using PhoneSession s = new PhoneSession();
		await s.PairAsync();
		s.Store.SetStatus(s.AssessmentId, AssessmentStatus.Final);
		string before = Snapshot(s);
		HttpResponseMessage res = await PostRaw(s, $"/api/assessments/{s.AssessmentId}/{what}", json);
		Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
		Assert.Contains("Final", await PhoneSession.Error(res));
		Assert.Equal(before, Snapshot(s));
		Assert.Equal(AssessmentStatus.Final, s.Store.GetStatus(s.AssessmentId).Value);
	}

	[Theory]
	[MemberData(nameof(GoodRequests))]
	public async Task NewEndpoints_RefuseACheckedOutAssessment_AndLeaveItUnchanged(string what, string json)
	{
		await using PhoneSession s = new PhoneSession();
		await s.PairAsync();
		WorkPackages.CheckOut(s.Store, new[] { s.AssessmentId }, Path.Combine(s.Db.Folder, "out" + WorkPackages.Extension), "Team 1");
		string before = Snapshot(s);
		HttpResponseMessage res = await PostRaw(s, $"/api/assessments/{s.AssessmentId}/{what}", json);
		Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
		Assert.Contains("checked out", await PhoneSession.Error(res));
		Assert.Equal(before, Snapshot(s));
	}

	[Theory]
	[MemberData(nameof(GoodRequests))]
	public async Task NewEndpoints_RefuseAnAssessmentThatDoesntExist(string what, string json)
	{
		await using PhoneSession s = new PhoneSession();
		await s.PairAsync();
		Assert.Equal(HttpStatusCode.NotFound, (await PostRaw(s, $"/api/assessments/{Guid.NewGuid()}/{what}", json)).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await PostRaw(s, $"/api/assessments/not-a-guid/{what}", json)).StatusCode);
	}

	[Theory]
	[InlineData("""{"elements":[{"id":999,"damaged":50}]}""")]          // not an element
	[InlineData("""{"elements":[{"id":16,"damaged":50}]}""")]           // skirting: manufactured houses only
	[InlineData("""{"elements":[{"id":4,"damaged":-1}]}""")]
	[InlineData("""{"elements":[{"id":4,"damaged":100.01}]}""")]
	[InlineData("""{"elements":[{"id":4,"damaged":1e30}]}""")]
	[InlineData("""{"elements":[{"id":4,"damaged":null}]}""")]
	[InlineData("""{"elements":[{"id":4}]}""")]
	[InlineData("""{"elements":[{"id":4,"damaged":"half"}]}""")]
	[InlineData("""{"elements":[{"id":4.5,"damaged":50}]}""")]
	[InlineData("""{"elements":[{"id":4,"damaged":50},{"id":5,"damaged":101}]}""")]   // one bad value: none saved
	[InlineData("""{"elements":[null]}""")]
	[InlineData("""not json""")]
	public async Task Elements_BadIdsAndValues_AreRefused_AndNothingIsSaved(string json)
	{
		await using PhoneSession s = new PhoneSession();
		await s.PairAsync();
		string before = Snapshot(s);
		Assert.Equal(HttpStatusCode.BadRequest, (await PostRaw(s, $"/api/assessments/{s.AssessmentId}/elements", json)).StatusCode);
		Assert.Equal(before, Snapshot(s));
	}

	[Theory]
	[InlineData("""{"adjustments":[{"id":999999,"quantity":1,"unitCost":1}]}""")]       // not offered for this structure
	[InlineData("""{"adjustments":[{"id":4,"quantity":1,"unitCost":1}]}""")]            // an element's id, not an adjustment
	[InlineData("""{"adjustments":[{"id":{0},"quantity":-1,"unitCost":1}]}""")]          // quantity below 0, as on the Mac
	[InlineData("""{"adjustments":[{"id":{0},"quantity":1,"unitCost":-99999999999}]}""")]
	[InlineData("""{"adjustments":[{"id":{0},"quantity":99999999999,"unitCost":1}]}""")]
	[InlineData("""{"adjustments":[{"id":{0},"quantity":"lots","unitCost":1}]}""")]
	[InlineData("""{"additional":[{"line":0,"name":"Deck","quantity":1,"unitCost":1}]}""")]
	[InlineData("""{"additional":[{"line":8,"name":"Deck","quantity":1,"unitCost":1}]}""")]
	[InlineData("""{"additional":[{"line":1,"name":"Deck","quantity":1e30,"unitCost":1}]}""")]
	[InlineData("""{"additional":[{"line":1,"name":"Deck","quantity":1,"unitCost":10000000001}]}""")]
	[InlineData("""{"additional":[{"line":1,"name":"Two\nlines","quantity":1,"unitCost":1}]}""")]
	[InlineData("""{"additional":[{"line":1,"name":"{1}","quantity":1,"unitCost":1}]}""")]   // too long
	[InlineData("""{"additional":[{"line":1,"name":"Deck","quantity":1,"unitCost":1}],"adjustments":[{"id":999999}]}""")]
	public async Task Costs_BadIdsAndValues_AreRefused_AndNothingIsSaved(string template)
	{
		await using PhoneSession s = new PhoneSession();
		await s.PairAsync();
		// A single-family house, so the standard adjustments (garage, porch and so on) are offered.
		Assert.Equal(HttpStatusCode.OK, (await s.Change("Property.ResidenceTypeID", "92")).StatusCode);
		int offered = s.Store.LoadAssessment(s.AssessmentId).CostAdjustments.First().CostAdjustmentLookupID;
		string json = template.Replace("{0}", offered.ToString(CultureInfo.InvariantCulture)).Replace("{1}", new string('x', FieldApi.MaxAdjustmentNameLength + 1));
		string before = Snapshot(s);
		Assert.Equal(HttpStatusCode.BadRequest, (await PostRaw(s, $"/api/assessments/{s.AssessmentId}/costs", json)).StatusCode);
		Assert.Equal(before, Snapshot(s));
	}

	[Fact]
	public async Task NewEndpoints_RefuseTooManyChangesAtOnce()
	{
		await using PhoneSession s = new PhoneSession();
		await s.PairAsync();
		string before = Snapshot(s);
		string elements = "{\"elements\":[" + string.Join(",", Enumerable.Repeat("{\"id\":4,\"damaged\":1}", FieldServer.MaxChanges + 1)) + "]}";
		Assert.Equal(HttpStatusCode.BadRequest, (await PostRaw(s, $"/api/assessments/{s.AssessmentId}/elements", elements)).StatusCode);
		string costs = "{\"additional\":[" + string.Join(",", Enumerable.Repeat("{\"line\":1,\"quantity\":1}", FieldServer.MaxChanges + 1)) + "]}";
		Assert.Equal(HttpStatusCode.BadRequest, (await PostRaw(s, $"/api/assessments/{s.AssessmentId}/costs", costs)).StatusCode);
		// Bodies over the 1 MB limit are refused before they are read.
		string huge = "{\"elements\":[{\"id\":4,\"damaged\":1}],\"pad\":\"" + new string('x', (int)FieldServer.MaxJsonBytes) + "\"}";
		Assert.NotEqual(HttpStatusCode.OK, (await PostRaw(s, $"/api/assessments/{s.AssessmentId}/elements", huge)).StatusCode);
		Assert.Equal(before, Snapshot(s));
	}

	[Theory]
	[InlineData("Final")]
	[InlineData("LetterSent")]
	[InlineData("Letter sent")]
	[InlineData("Appeal")]
	[InlineData("final")]
	[InlineData("reviewed")]
	[InlineData("2")]
	[InlineData("")]
	[InlineData(null)]
	public async Task Status_OnlyDraftOrReviewed_IsAccepted(string status)
	{
		await using PhoneSession s = new PhoneSession();
		await s.PairAsync();
		HttpResponseMessage res = await s.Post($"/api/assessments/{s.AssessmentId}/status", new { status });
		Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
		Assert.Equal(AssessmentStatus.Draft, s.Store.GetStatus(s.AssessmentId).Value);
		Assert.DoesNotContain(s.Store.GetHistory(s.AssessmentId), h => h.Field == "Status");
	}

	[Theory]
	[InlineData(AssessmentStatus.Final)]
	[InlineData(AssessmentStatus.LetterSent)]
	public async Task Status_CantReopenALockedAssessment(AssessmentStatus locked)
	{
		await using PhoneSession s = new PhoneSession();
		await s.PairAsync();
		s.Store.SetStatus(s.AssessmentId, locked);
		foreach (string to in new[] { "Draft", "Reviewed" })
		{
			Assert.Equal(HttpStatusCode.Conflict, (await s.Post($"/api/assessments/{s.AssessmentId}/status", new { status = to })).StatusCode);
		}
		Assert.Equal(locked, s.Store.GetStatus(s.AssessmentId).Value);
	}

	[Fact]
	public async Task Status_CantLeaveAppeal()
	{
		await using PhoneSession s = new PhoneSession();
		await s.PairAsync();
		s.Store.SetStatus(s.AssessmentId, AssessmentStatus.Appeal);
		HttpResponseMessage res = await s.Post($"/api/assessments/{s.AssessmentId}/status", new { status = "Draft" });
		Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
		Assert.Contains("changed on the Mac", await PhoneSession.Error(res));
		Assert.Equal(AssessmentStatus.Appeal, s.Store.GetStatus(s.AssessmentId).Value);
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
