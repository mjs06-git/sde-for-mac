using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sde.Interop.Online;

/// <summary>A published release of SDE for Mac, as GitHub lists it.</summary>
/// <param name="Version">The release's version, such as "3.5.1" (its tag without the "v").</param>
/// <param name="PageUrl">The release's page on github.com, checked to be in SDE's repository.</param>
/// <param name="PublishedOn">When it was published (UTC), when GitHub says.</param>
public sealed record ReleaseInfo(string Version, string PageUrl, DateTime? PublishedOn);

/// <summary>What Help, then Check for Updates found.</summary>
public enum UpdateCheckOutcome
{
	/// <summary>This copy is the latest published version (or newer).</summary>
	UpToDate,

	/// <summary>A newer version is published; <see cref="UpdateCheckResult.Latest"/> says which.</summary>
	NewerAvailable,

	/// <summary>GitHub couldn't be asked, or its answer couldn't be used; <see cref="UpdateCheckResult.Message"/> says why.</summary>
	CouldNotCheck,
}

/// <summary>The answer to an update check.</summary>
/// <param name="Outcome">Up to date, newer available, or could not check.</param>
/// <param name="Latest">The newer release, only when <paramref name="Outcome"/> is <see cref="UpdateCheckOutcome.NewerAvailable"/>.</param>
/// <param name="Message">Why the check failed, in plain English, only when it could not check.</param>
/// <param name="Detail">Technical detail for the log when it could not check (the status code or what was wrong).</param>
public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, ReleaseInfo Latest, string Message, string Detail = null);

/// <summary>
/// Asks GitHub for the latest published release of SDE for Mac and compares it with the running version. Used only by
/// Help, then Check for Updates: it runs when the person chooses that, never in the background or at launch. It makes
/// one HTTPS GET to GitHub's "latest release" address with a User-Agent naming only the app and its version, and sends
/// nothing about the Mac, the person or the database. It reads only the fields it needs (tag_name, html_url,
/// published_at, prerelease, draft), ignores pre-releases and drafts, and offers a release page only when it is on
/// github.com in SDE's repository. It downloads and installs nothing. See docs/ONLINE-SERVICES.md.
/// </summary>
public sealed class UpdateChecker
{
	/// <summary>SDE for Mac's repository on GitHub, as "owner/name": the one place it is written.</summary>
	public const string Repository = "mjs06-git/sde-for-mac";

	/// <summary>GitHub's address for the repository's latest published release.</summary>
	public const string LatestReleaseUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";

	/// <summary>The most of GitHub's answer that is read; a release's JSON is a few kilobytes, with its notes.</summary>
	public const int MaxResponseBytes = 512 * 1024;

	private const string ServiceName = "GitHub";

	private readonly HttpClient _http;
	private readonly string _userAgent;

	/// <param name="http">The shared HTTP client (tests pass one whose handler answers without the network).</param>
	/// <param name="appName">The app's name for the User-Agent, such as "SDE-for-Mac".</param>
	/// <param name="appVersion">The app's version for the User-Agent, such as "3.5.0".</param>
	public UpdateChecker(HttpClient http, string appName, string appVersion)
	{
		_http = http;
		_userAgent = appName + "/" + appVersion;
	}

	/// <summary>How long to wait for GitHub's answer.</summary>
	public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

	/// <summary>The User-Agent sent: the app's name and version and nothing else.</summary>
	public string UserAgent => _userAgent;

	/// <summary>
	/// Asks GitHub for the latest release and compares it with <paramref name="runningVersion"/> (such as "3.5.0").
	/// Never throws for a network or answer problem: those come back as <see cref="UpdateCheckOutcome.CouldNotCheck"/>.
	/// Throws <see cref="OperationCanceledException"/> only when <paramref name="ct"/> is cancelled.
	/// </summary>
	public async Task<UpdateCheckResult> CheckAsync(string runningVersion, CancellationToken ct)
	{
		if (!SemanticVersion.TryParse(runningVersion, out SemanticVersion running))
		{
			return Failed("SDE couldn't tell which version this is, so it can't compare it with the latest one.", "Running version: " + runningVersion);
		}
		string body;
		using (CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
		{
			limit.CancelAfter(Timeout);
			try
			{
				using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
				request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
				request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
				using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
				if (!response.IsSuccessStatusCode)
				{
					return Failed(StatusMessage(response.StatusCode), $"GET {LatestReleaseUrl} returned {(int)response.StatusCode} {response.ReasonPhrase}");
				}
				body = await ReadLimitedAsync(response.Content, limit.Token).ConfigureAwait(false);
				if (body == null)
				{
					return Failed(UnexpectedAnswer, $"GET {LatestReleaseUrl} answered more than {MaxResponseBytes} bytes.");
				}
			}
			catch (OperationCanceledException) when (!ct.IsCancellationRequested)
			{
				return Failed($"{ServiceName} didn't answer within {(int)Math.Ceiling(Timeout.TotalSeconds)} seconds. Check the internet connection and try again.", $"GET {LatestReleaseUrl} timed out.");
			}
			catch (HttpRequestException ex)
			{
				return Failed($"Couldn't reach {ServiceName}. Check the internet connection and try again.", $"GET {LatestReleaseUrl} failed: {ex.Message}");
			}
		}

		LatestRelease latest = Parse(body, out string problem);
		if (latest == null)
		{
			return Failed(UnexpectedAnswer, problem);
		}
		// Pre-releases and drafts are never offered (GitHub's "latest" leaves them out, but this checks anyway).
		if (latest.IsPrerelease || latest.IsDraft || latest.Tag.Contains('-'))
		{
			return new UpdateCheckResult(UpdateCheckOutcome.UpToDate, null, null);
		}
		if (!SemanticVersion.TryParseTag(latest.Tag, out SemanticVersion published))
		{
			return Failed(UnexpectedAnswer, "Release tag isn't vX.Y.Z: " + latest.Tag);
		}
		if (!IsReleasePage(latest.PageUrl))
		{
			return Failed(UnexpectedAnswer, "Release page isn't in the repository on github.com: " + latest.PageUrl);
		}
		if (published.CompareTo(running) <= 0)
		{
			return new UpdateCheckResult(UpdateCheckOutcome.UpToDate, null, null);
		}
		return new UpdateCheckResult(UpdateCheckOutcome.NewerAvailable, new ReleaseInfo(published.ToString(), latest.PageUrl, latest.PublishedOn), null);
	}

	/// <summary>Said when GitHub answered with something SDE can't use.</summary>
	public const string UnexpectedAnswer = "GitHub sent back an answer SDE couldn't use. Try again later.";

	/// <summary>
	/// True when <paramref name="url"/> is an https address on github.com under SDE's repository's releases (no other
	/// host, port, user name or scheme), so it is safe to open in the browser.
	/// </summary>
	public static bool IsReleasePage(string url)
	{
		if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
		{
			return false;
		}
		return uri.Scheme == Uri.UriSchemeHttps
			&& string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
			&& uri.IsDefaultPort
			&& string.IsNullOrEmpty(uri.UserInfo)
			&& uri.AbsolutePath.StartsWith("/" + Repository + "/releases/", StringComparison.OrdinalIgnoreCase)
			&& !uri.AbsolutePath.Contains("..", StringComparison.Ordinal);
	}

	private static UpdateCheckResult Failed(string message, string detail) => new UpdateCheckResult(UpdateCheckOutcome.CouldNotCheck, null, message, detail);

	private static string StatusMessage(HttpStatusCode status) => status switch
	{
		HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests =>
			"GitHub is limiting how often it can be asked from this network. Try again in an hour.",
		HttpStatusCode.NotFound => "GitHub has no published release of SDE for Mac to compare with.",
		_ => $"GitHub had a problem and couldn't answer (error {(int)status}). Try again later.",
	};

	/// <summary>The body as text, or null when it is longer than <see cref="MaxResponseBytes"/>.</summary>
	private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken ct)
	{
		if (content.Headers.ContentLength is long length && length > MaxResponseBytes)
		{
			return null;
		}
		await using Stream stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
		using MemoryStream buffer = new MemoryStream();
		byte[] chunk = new byte[16 * 1024];
		int read;
		while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
		{
			if (buffer.Length + read > MaxResponseBytes)
			{
				return null;
			}
			buffer.Write(chunk, 0, read);
		}
		return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
	}

	/// <summary>The fields read from GitHub's answer.</summary>
	private sealed record LatestRelease(string Tag, string PageUrl, DateTime? PublishedOn, bool IsPrerelease, bool IsDraft);

	/// <summary>
	/// Reads only tag_name, html_url, published_at, prerelease and draft. Anything else in the answer is ignored. Null
	/// (with <paramref name="problem"/> set) when the answer isn't a JSON object with those fields of the right kinds.
	/// </summary>
	private static LatestRelease Parse(string body, out string problem)
	{
		problem = null;
		try
		{
			using JsonDocument doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
			JsonElement root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
			{
				problem = "The answer isn't a JSON object.";
				return null;
			}
			if (!TryString(root, "tag_name", out string tag) || !TryString(root, "html_url", out string page))
			{
				problem = "tag_name or html_url is missing or not text.";
				return null;
			}
			if (!TryBool(root, "prerelease", out bool prerelease) || !TryBool(root, "draft", out bool draft))
			{
				problem = "prerelease or draft is missing or not true or false.";
				return null;
			}
			DateTime? published = null;
			if (root.TryGetProperty("published_at", out JsonElement when) && when.ValueKind == JsonValueKind.String
				&& DateTime.TryParse(when.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime at))
			{
				published = at;
			}
			return new LatestRelease(tag.Trim(), page.Trim(), published, prerelease, draft);
		}
		catch (JsonException ex)
		{
			problem = "The answer isn't valid JSON: " + ex.Message;
			return null;
		}
	}

	private static bool TryString(JsonElement root, string name, out string value)
	{
		value = root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
		return !string.IsNullOrWhiteSpace(value);
	}

	private static bool TryBool(JsonElement root, string name, out bool value)
	{
		value = false;
		if (!root.TryGetProperty(name, out JsonElement e) || e.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
		{
			return false;
		}
		value = e.GetBoolean();
		return true;
	}
}

/// <summary>
/// A version as three numbers (major.minor.patch), with an optional pre-release label after a hyphen ("3.6.0-beta1"),
/// compared number by number (so 3.10.0 is newer than 3.9.9), a pre-release coming before its release.
/// </summary>
public readonly record struct SemanticVersion(int Major, int Minor, int Patch, string Label) : IComparable<SemanticVersion>
{
	private static readonly Regex Pattern = new Regex(@"^(\d{1,6})\.(\d{1,6})\.(\d{1,6})(?:-([0-9A-Za-z.-]{1,40}))?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	/// <summary>Reads "3.5.0" or "3.6.0-beta1". False for anything else (blank, two numbers, letters, a leading "v").</summary>
	public static bool TryParse(string text, out SemanticVersion version)
	{
		version = default;
		Match m = text == null ? Match.Empty : Pattern.Match(text.Trim());
		if (!m.Success)
		{
			return false;
		}
		version = new SemanticVersion(Number(m.Groups[1].Value), Number(m.Groups[2].Value), Number(m.Groups[3].Value),
			m.Groups[4].Success ? m.Groups[4].Value : null);
		return true;
	}

	/// <summary>Reads a release tag, "v" and three numbers ("v3.5.1"). False for a pre-release tag or anything else.</summary>
	public static bool TryParseTag(string tag, out SemanticVersion version)
	{
		version = default;
		if (tag == null || tag.Length < 2 || tag[0] != 'v' || tag.Contains('-'))
		{
			return false;
		}
		return TryParse(tag.Substring(1), out version);
	}

	public int CompareTo(SemanticVersion other)
	{
		int c = Major.CompareTo(other.Major);
		if (c == 0)
		{
			c = Minor.CompareTo(other.Minor);
		}
		if (c == 0)
		{
			c = Patch.CompareTo(other.Patch);
		}
		if (c != 0)
		{
			return c;
		}
		// The same numbers: a release is newer than its pre-release; two pre-releases compare by label.
		if (Label == null || other.Label == null)
		{
			return Label == null ? (other.Label == null ? 0 : 1) : -1;
		}
		return string.CompareOrdinal(Label, other.Label);
	}

	public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Label == null ? "" : "-" + Label);

	private static int Number(string digits) => int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
}
