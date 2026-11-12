using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;

namespace Sde.App.Tests;

/// <summary>
/// The "High-Water" brand core is the same on every surface: the app's tokens (Styles/Tokens.axaml), the phone form's
/// stylesheet (src/Sde.Field/wwwroot/app.css, light and dark) and the PDF reports' theme (Sde.Reports.Internal.Theme,
/// always light). Only a few colours are checked: the accent fill, the ink, the gauge red, the page background and the
/// substantial and not substantial tones. Sizes and secondary tones may differ between the surfaces.
/// </summary>
public class BrandAgreementTests
{
	private static Color Token(string key, ThemeVariant theme)
	{
		Assert.True(Application.Current!.TryGetResource(key, theme, out object value), $"No resource \"{key}\" in the {theme} theme");
		return value switch
		{
			Color c => c,
			ISolidColorBrush b => b.Color,
			_ => throw new InvalidOperationException($"\"{key}\" is not a colour"),
		};
	}

	/// <summary>The custom properties of the phone form's light block (the first :root) or dark block (the :root inside prefers-color-scheme: dark).</summary>
	private static Dictionary<string, Color> PhoneColours(ThemeVariant theme)
	{
		string css = File.ReadAllText(Path.Combine(TestPaths.Root, "src", "Sde.Field", "wwwroot", "app.css"));
		int dark = css.IndexOf("@media (prefers-color-scheme: dark)", StringComparison.Ordinal);
		Assert.True(dark > 0, "app.css has no dark block");
		string block = theme == ThemeVariant.Dark ? css[dark..] : css[..dark];
		Match root = Regex.Match(block, @":root\s*\{([^}]*)\}");
		Assert.True(root.Success, $"app.css has no :root block for {theme}");
		Dictionary<string, Color> colours = new();
		foreach (Match m in Regex.Matches(root.Groups[1].Value, @"--([\w-]+)\s*:\s*(#[0-9a-fA-F]{6})\s*;"))
		{
			colours[m.Groups[1].Value] = Color.Parse(m.Groups[2].Value);
		}
		return colours;
	}

	/// <summary>A colour field of the reports' internal Theme class, read by reflection (MigraDoc colours have R, G and B).</summary>
	private static Color ReportColour(string field)
	{
		Type theme = Type.GetType("Sde.Reports.Internal.Theme, Sde.Reports", throwOnError: true);
		object value = theme.GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
		Assert.True(value != null, $"Theme has no colour \"{field}\"");
		byte Channel(string name) => Convert.ToByte(value.GetType().GetProperty(name).GetValue(value));
		return Color.FromRgb(Channel("R"), Channel("G"), Channel("B"));
	}

	[AvaloniaTheory]
	[InlineData("Light")]
	[InlineData("Dark")]
	public void PhoneForm_AgreesWithTheAppTokens(string variant)
	{
		ThemeVariant theme = variant == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
		Dictionary<string, Color> phone = PhoneColours(theme);
		(string Css, string Token)[] pairs =
		{
			("accent-fill", "AccentFillColor"),
			("text", "TextPrimaryColor"),
			("bg", "BgAppColor"),
			("danger", "StatusSubstantialFg"),
			("danger-bg", "StatusSubstantialBg"),
			("success", "StatusNotSubstantialFg"),
			("success-bg", "StatusNotSubstantialBg"),
			("gauge-red", "StatusSubstantialDot"),
			("frame", "BgFrameColor"),
			("on-frame", "TextOnFrameColor"),
			("on-frame-2", "TextOnFrameSecondaryColor"),
		};
		List<string> failures = new();
		foreach ((string css, string token) in pairs)
		{
			if (!phone.TryGetValue(css, out Color value))
			{
				failures.Add($"{theme}: app.css has no --{css}");
			}
			else if (value != Token(token, theme))
			{
				failures.Add($"{theme}: --{css} is {value}, {token} is {Token(token, theme)}");
			}
		}
		Assert.True(failures.Count == 0, string.Join("\n", failures));
	}

	[AvaloniaFact]
	public void Reports_AgreeWithTheAppsLightTokens()
	{
		(string Field, string Token)[] pairs =
		{
			("GaugeWater", "AccentFillColor"),
			("Ink", "TextPrimaryColor"),
			("GaugeRed", "DangerFillColor"),
			("SubstantialInk", "StatusSubstantialFg"),
			("SubstantialFill", "StatusSubstantialBg"),
			("NotSubstantialInk", "StatusNotSubstantialFg"),
			("NotSubstantialFill", "StatusNotSubstantialBg"),
		};
		List<string> failures = new();
		foreach ((string field, string token) in pairs)
		{
			Color report = ReportColour(field);
			Color app = Token(token, ThemeVariant.Light);
			if (report != app)
			{
				failures.Add($"Theme.{field} is {report}, {token} is {app}");
			}
		}
		Assert.True(failures.Count == 0, string.Join("\n", failures));
	}

	[AvaloniaFact]
	public void TheGaugeRed_IsTheSameOnPaperAndOnTheLightPhone()
	{
		Assert.Equal(ReportColour("GaugeRed"), PhoneColours(ThemeVariant.Light)["gauge-red"]);
		Assert.Equal(ReportColour("GaugeWater"), PhoneColours(ThemeVariant.Light)["gauge-water"]);
	}
}
