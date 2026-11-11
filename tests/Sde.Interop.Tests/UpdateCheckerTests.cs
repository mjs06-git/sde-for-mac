using System.Net;
using System.Text;
using Sde.Interop.Online;

namespace Sde.Interop.Tests;

/// <summary>Help, then Check for Updates: the GitHub client, answered by a fake handler (never the live service).</summary>
public class UpdateCheckerTests
{
	private const string Page = "https://github.com/mjs06-git/sde-for-mac/releases/tag/v3.5.1";

	private static string Release(string tag = "v3.5.1", string url = Page, bool prerelease = false, bool draft = false, string published = "2026-11-02T15:04:05Z") =>
		"{\"tag_name\":\"" + tag + "\",\"html_url\":\"" + url + "\",\"published_at\":\"" + published + "\",\"prerelease\":" + (prerelease ? "true" : "false")
		+ ",\"draft\":" + (draft ? "true" : "false") + ",\"name\":\"SDE for Mac\",\"body\":\"Notes\",\"assets\":[{\"name\":\"x.zip\"}],\"author\":{\"login\":\"someone\"}}";

	private static (UpdateChecker Checker, FakeHandler Handler, List<HttpRequestMessage> Requests) Make(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
	{
		List<HttpRequestMessage> requests = new List<HttpRequestMessage>();
		FakeHandler handler = new FakeHandler((request, ct) =>
		{
			requests.Add(request);
			return respond(request, ct);
		});
		return (new UpdateChecker(new HttpClient(handler), "SDE-for-Mac", "3.5.0") { Timeout = TimeSpan.FromSeconds(5) }, handler, requests);
	}

	private static (UpdateChecker Checker, FakeHandler Handler, List<HttpRequestMessage> Requests) Answering(string body, HttpStatusCode status = HttpStatusCode.OK) =>
		Make((_, _) => Task.FromResult(FakeHandler.Json(body, status)));

	[Fact]
	public async Task NewerRelease_IsOffered_WithVersionDateAndPage()
	{
		(UpdateChecker checker, FakeHandler handler, List<HttpRequestMessage> requests) = Answering(Release());
		UpdateCheckResult result = await checker.CheckAsync("3.5.0", CancellationToken.None);

		Assert.Equal(UpdateCheckOutcome.NewerAvailable, result.Outcome);
		Assert.Equal("3.5.1", result.Latest.Version);
		Assert.Equal(Page, result.Latest.PageUrl);
		Assert.Equal(new DateTime(2026, 11, 2, 15, 4, 5, DateTimeKind.Utc), result.Latest.PublishedOn);
		Assert.Null(result.Message);

		// One HTTPS GET to the repository's latest release, naming only the app and its version.
		Assert.Equal("https://api.github.com/repos/mjs06-git/sde-for-mac/releases/latest", Assert.Single(handler.Urls));
		HttpRequestMessage request = Assert.Single(requests);
		Assert.Equal(HttpMethod.Get, request.Method);
		Assert.Equal("SDE-for-Mac/3.5.0", string.Join(" ", request.Headers.GetValues("User-Agent")));
		Assert.Null(request.Content);
		Assert.Empty(request.RequestUri.Query);
	}

	[Theory]
	[InlineData("v3.5.0", "3.5.0")]   // the same
	[InlineData("v3.4.9", "3.5.0")]   // older
	[InlineData("v3.9.9", "3.10.0")]  // older, though "3.9.9" sorts after "3.10.0" as text
	public async Task SameOrOlderRelease_IsUpToDate(string tag, string running)
	{
		(UpdateChecker checker, _, _) = Answering(Release(tag, "https://github.com/mjs06-git/sde-for-mac/releases/tag/" + tag));
		UpdateCheckResult result = await checker.CheckAsync(running, CancellationToken.None);
		Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
		Assert.Null(result.Latest);
	}

	[Fact]
	public async Task TenIsNewerThanNine()
	{
		(UpdateChecker checker, _, _) = Answering(Release("v3.10.0", "https://github.com/mjs06-git/sde-for-mac/releases/tag/v3.10.0"));
		UpdateCheckResult result = await checker.CheckAsync("3.9.9", CancellationToken.None);
		Assert.Equal(UpdateCheckOutcome.NewerAvailable, result.Outcome);
		Assert.Equal("3.10.0", result.Latest.Version);
	}

	[Theory]
	[InlineData("v3.6.0-beta1", true, false)]
	[InlineData("v3.6.0-beta1", false, false)]   // a hyphen alone makes it a pre-release
	[InlineData("v3.6.0", true, false)]
	[InlineData("v3.6.0", false, true)]
	public async Task PreReleasesAndDrafts_AreIgnored(string tag, bool prerelease, bool draft)
	{
		(UpdateChecker checker, _, _) = Answering(Release(tag, Page, prerelease, draft));
		UpdateCheckResult result = await checker.CheckAsync("3.5.0", CancellationToken.None);
		Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
		Assert.Null(result.Latest);
	}

	[Theory]
	[InlineData("not json")]
	[InlineData("[1,2,3]")]
	[InlineData("{\"html_url\":\"" + Page + "\",\"prerelease\":false,\"draft\":false}")]                       // no tag
	[InlineData("{\"tag_name\":351,\"html_url\":\"" + Page + "\",\"prerelease\":false,\"draft\":false}")]      // a number for the tag
	[InlineData("{\"tag_name\":\"v3.5.1\",\"html_url\":\"" + Page + "\",\"prerelease\":\"no\",\"draft\":false}")] // text for true or false
	[InlineData("{\"tag_name\":\"v3.5.1\",\"html_url\":\"" + Page + "\",\"draft\":false}")]                    // no prerelease
	[InlineData("{\"tag_name\":\"3.5.1\",\"html_url\":\"" + Page + "\",\"prerelease\":false,\"draft\":false}")] // no "v"
	[InlineData("{\"tag_name\":\"v3.5\",\"html_url\":\"" + Page + "\",\"prerelease\":false,\"draft\":false}")]  // two numbers
	[InlineData("{\"tag_name\":\"latest\",\"html_url\":\"" + Page + "\",\"prerelease\":false,\"draft\":false}")]
	public async Task MalformedAnswers_CouldNotCheck(string body)
	{
		(UpdateChecker checker, _, _) = Answering(body);
		UpdateCheckResult result = await checker.CheckAsync("3.5.0", CancellationToken.None);
		Assert.Equal(UpdateCheckOutcome.CouldNotCheck, result.Outcome);
		Assert.Equal(UpdateChecker.UnexpectedAnswer, result.Message);
		Assert.Null(result.Latest);
		Assert.False(string.IsNullOrEmpty(result.Detail));
	}

	[Theory]
	[InlineData("https://evil.example/mjs06-git/sde-for-mac/releases/tag/v3.5.1")]
	[InlineData("http://github.com/mjs06-git/sde-for-mac/releases/tag/v3.5.1")]
	[InlineData("https://github.com/someone-else/sde-for-mac/releases/tag/v3.5.1")]
	[InlineData("https://github.com.evil.example/mjs06-git/sde-for-mac/releases/tag/v3.5.1")]
	[InlineData("https://user@github.com/mjs06-git/sde-for-mac/releases/tag/v3.5.1")]
	[InlineData("https://github.com:8443/mjs06-git/sde-for-mac/releases/tag/v3.5.1")]
	[InlineData("https://github.com/mjs06-git/sde-for-mac/archive/v3.5.1.zip")]
	[InlineData("file:///Applications/SDE.app")]
	[InlineData("javascript:alert(1)")]
	public async Task ReleasePagesOutsideTheRepository_AreRefused(string url)
	{
		Assert.False(UpdateChecker.IsReleasePage(url));
		(UpdateChecker checker, _, _) = Answering(Release(url: url));
		UpdateCheckResult result = await checker.CheckAsync("3.5.0", CancellationToken.None);
		Assert.Equal(UpdateCheckOutcome.CouldNotCheck, result.Outcome);
		Assert.Null(result.Latest);
	}

	[Theory]
	[InlineData(HttpStatusCode.Forbidden, "limiting")]
	[InlineData(HttpStatusCode.TooManyRequests, "limiting")]
	[InlineData(HttpStatusCode.NotFound, "no published release")]
	[InlineData(HttpStatusCode.InternalServerError, "error 500")]
	public async Task HttpErrors_CouldNotCheck_InPlainEnglish(HttpStatusCode status, string said)
	{
		(UpdateChecker checker, _, _) = Answering("{\"message\":\"API rate limit exceeded\"}", status);
		UpdateCheckResult result = await checker.CheckAsync("3.5.0", CancellationToken.None);
		Assert.Equal(UpdateCheckOutcome.CouldNotCheck, result.Outcome);
		Assert.Contains(said, result.Message);
		Assert.Contains(((int)status).ToString(), result.Detail);
	}

	[Fact]
	public async Task NoAnswer_TimesOut_AndCouldNotCheck()
	{
		(UpdateChecker checker, _, _) = Make(async (_, ct) =>
		{
			await Task.Delay(TimeSpan.FromSeconds(30), ct);
			return FakeHandler.Json(Release());
		});
		checker.Timeout = TimeSpan.FromMilliseconds(100);
		UpdateCheckResult result = await checker.CheckAsync("3.5.0", CancellationToken.None);
		Assert.Equal(UpdateCheckOutcome.CouldNotCheck, result.Outcome);
		Assert.Contains("didn't answer", result.Message);
	}

	[Fact]
	public async Task Offline_CouldNotCheck()
	{
		(UpdateChecker checker, _, _) = Make((_, _) => throw new HttpRequestException("No route to host"));
		UpdateCheckResult result = await checker.CheckAsync("3.5.0", CancellationToken.None);
		Assert.Equal(UpdateCheckOutcome.CouldNotCheck, result.Outcome);
		Assert.Contains("Couldn't reach GitHub", result.Message);
	}

	[Fact]
	public async Task AnOversizedAnswer_IsNotRead()
	{
		string huge = "{\"tag_name\":\"v3.5.1\",\"html_url\":\"" + Page + "\",\"prerelease\":false,\"draft\":false,\"body\":\"" + new string('x', UpdateChecker.MaxResponseBytes) + "\"}";
		(UpdateChecker checker, _, _) = Make((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
		{
			// No length given, so the cap is applied while reading.
			Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(huge))),
		}));
		UpdateCheckResult result = await checker.CheckAsync("3.5.0", CancellationToken.None);
		Assert.Equal(UpdateCheckOutcome.CouldNotCheck, result.Outcome);
		Assert.Equal(UpdateChecker.UnexpectedAnswer, result.Message);
	}

	[Fact]
	public async Task AnUnreadableRunningVersion_CouldNotCheck_WithoutAsking()
	{
		(UpdateChecker checker, FakeHandler handler, _) = Answering(Release());
		UpdateCheckResult result = await checker.CheckAsync("dev build", CancellationToken.None);
		Assert.Equal(UpdateCheckOutcome.CouldNotCheck, result.Outcome);
		Assert.Empty(handler.Urls);
	}

	[Theory]
	[InlineData("3.5.0", "3.5.1", -1)]
	[InlineData("3.5.1", "3.5.0", 1)]
	[InlineData("3.10.0", "3.9.9", 1)]
	[InlineData("4.0.0", "3.99.99", 1)]
	[InlineData("3.5.0", "3.5.0", 0)]
	[InlineData("3.6.0-beta1", "3.6.0", -1)]
	[InlineData("3.6.0-beta2", "3.6.0-beta1", 1)]
	public void Versions_CompareNumberByNumber(string a, string b, int sign)
	{
		Assert.True(SemanticVersion.TryParse(a, out SemanticVersion x));
		Assert.True(SemanticVersion.TryParse(b, out SemanticVersion y));
		Assert.Equal(sign, Math.Sign(x.CompareTo(y)));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("3.5")]
	[InlineData("v3.5.0")]
	[InlineData("3.5.0.1")]
	[InlineData("three.five.oh")]
	[InlineData("3.5.0 ")]
	[InlineData("99999999.0.0")]
	public void MalformedVersions_AreNotRead(string text)
	{
		// A trailing space is trimmed, so that one is the only one read.
		Assert.Equal(text == "3.5.0 ", SemanticVersion.TryParse(text, out _));
	}

	[Theory]
	[InlineData("v3.5.1", true)]
	[InlineData("v3.6.0-beta1", false)]
	[InlineData("3.5.1", false)]
	[InlineData("V3.5.1", false)]
	[InlineData("v", false)]
	public void Tags_AreVAndThreeNumbers(string tag, bool read) => Assert.Equal(read, SemanticVersion.TryParseTag(tag, out _));
}
