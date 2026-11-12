using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Sde.App.Services;

namespace Sde.App.Tests;

/// <summary>
/// Text tokens keep at least 4.5:1 contrast (WCAG AA for body text) on the backgrounds they are used on, in the light
/// and the dark theme. The colours are read from the app's resources (Styles/Tokens.axaml), so this checks what ships.
/// </summary>
public class ContrastTests
{
	/// <summary>Text (or icon) token, background token. Every pair is checked in both themes.</summary>
	public static readonly (string Text, string Background)[] Pairs =
	{
		("TextPrimary", "BgApp"), ("TextPrimary", "BgSidebar"), ("TextPrimary", "BgSurface"), ("TextPrimary", "BgSubtle"), ("TextPrimary", "BgHover"), ("TextPrimary", "AccentSoft"),
		("TextSecondary", "BgApp"), ("TextSecondary", "BgSidebar"), ("TextSecondary", "BgSurface"), ("TextSecondary", "BgSubtle"), ("TextSecondary", "BgHover"), ("TextSecondary", "AccentSoft"),
		("TextTertiary", "BgApp"), ("TextTertiary", "BgSidebar"), ("TextTertiary", "BgSurface"), ("TextTertiary", "BgSubtle"), ("TextTertiary", "BgHover"),
		("Accent", "BgApp"), ("Accent", "BgSidebar"), ("Accent", "BgSurface"), ("Accent", "AccentSoft"),
		("OnAccent", "AccentFill"), ("OnAccent", "AccentFillHover"), ("OnAccent", "AccentFillPressed"), ("OnAccent", "DangerFill"), ("OnAccent", "DangerFillHover"),
		("DangerFg", "DangerBg"), ("DangerFg", "BgSurface"), ("DangerFg", "BgApp"),
		("WarningFg", "WarningBg"), ("WarningFg", "BgSurface"), ("WarningFg", "BgApp"),
		("SuccessFg", "SuccessBg"), ("SuccessFg", "BgSurface"), ("SuccessFg", "BgApp"),
		("InfoFg", "InfoBg"), ("InfoFg", "BgSurface"), ("VioletFg", "VioletBg"), ("NeutralFg", "NeutralBg"),
		("TextPrimary", "DangerBg"), ("TextPrimary", "WarningBg"), ("TextPrimary", "SuccessBg"), ("TextPrimary", "InfoBg"), ("TextPrimary", "NeutralBg"),
		("TextOnTooltip", "BgTooltip"),
		// The inspector panes (Structures, the editor's result), the selected table row and the sidebar's step outline
		("TextPrimary", "BgInspector"), ("TextSecondary", "BgInspector"), ("TextTertiary", "BgInspector"), ("Accent", "BgInspector"), ("DangerFg", "BgInspector"),
		("DangerFg", "AccentSoft"), ("DangerFg", "BgSidebar"), ("TextTertiary", "AccentSoft"),
		// The navy frame (the sidebar and the title band) and the result panel have their own text colours
		("TextOnFrame", "BgFrame"), ("TextOnFrame", "BgFrameHover"), ("TextOnFrame", "BgFramePressed"), ("TextOnFrame", "BgFrameField"),
		("TextOnFrameSecondary", "BgFrame"), ("TextOnFrameSecondary", "BgFrameHover"), ("TextOnFrameSecondary", "BgFrameField"),
		("TextOnFrameTertiary", "BgFrame"), ("TextOnFrameTertiary", "BgFrameHover"), ("TextOnFrameTertiary", "BgFrameField"),
		("AccentOnFrame", "BgFrame"), ("AccentOnFrame", "BgFrameHover"), ("DangerOnFrame", "BgFrame"), ("DangerOnFrame", "BgFrameHover"),
		("TextOnHero", "BgHero"), ("TextOnHeroSecondary", "BgHero"), ("DangerOnFrame", "BgHero"),
		// 3.5's frame in use: the selected sidebar row (white on the accent), the chosen segment of the view switch, the
		// step showing (a lighter navy row), a required count on its capsule; and the tables' tinted header row
		("OnAccent", "AccentFill"), ("TextOnFrame", "BgFrameSelected"),
		("TextOnFrameSecondary", "BgFramePressed"), ("DangerOnFrame", "BgFrameField"), ("DangerOnFrame", "BgFramePressed"),
		("TextPrimary", "BgTableHeader"), ("TextSecondary", "BgTableHeader"),
		// The older names the result colours are looked up by
		("StatusSubstantialFg", "StatusSubstantialBg"), ("StatusCloseFg", "StatusCloseBg"), ("StatusNotSubstantialFg", "StatusNotSubstantialBg"),
		("StatusIncompleteFg", "StatusIncompleteBg"), ("StatusUndeterminedFg", "StatusUndeterminedBg"),
		("StatusSubstantialFg", "CardBg"), ("StatusCloseFg", "CardBg"), ("StatusNotSubstantialFg", "CardBg"), ("StatusIncompleteFg", "CardBg"),
		("RequiredFg", "CardBg"), ("RecommendedFg", "CardBg"), ("SuggestedFg", "CardBg"), ("TextMuted", "CardBg"), ("TextMuted", "SidebarBg"),
	};

	private static Color ColorOf(string key, ThemeVariant theme)
	{
		Assert.True(Application.Current!.TryGetResource(key, theme, out object value), $"No resource \"{key}\" in the {theme} theme");
		ISolidColorBrush brush = Assert.IsAssignableFrom<ISolidColorBrush>(value);
		return brush.Color;
	}

	private static double Luminance(Color c)
	{
		static double Channel(byte v)
		{
			double s = v / 255.0;
			return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
		}
		return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
	}

	public static double Ratio(Color a, Color b)
	{
		double la = Luminance(a);
		double lb = Luminance(b);
		return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
	}

	[AvaloniaFact]
	public void TextTokens_HaveAtLeast4Point5To1_InBothThemes()
	{
		List<string> failures = new();
		foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
		{
			foreach ((string text, string background) in Pairs)
			{
				Color bg = ColorOf(background, theme);
				Assert.Equal(255, bg.A);                     // backgrounds are opaque, so the ratio is exact
				double ratio = Ratio(ColorOf(text, theme), bg);
				if (ratio < 4.5)
				{
					failures.Add($"{theme}: {text} on {background} is {ratio:0.00}:1");
				}
			}
		}
		Assert.True(failures.Count == 0, string.Join("\n", failures));
	}

	/// <summary>Primary text: held to 7:1 (WCAG AAA) while Increase Contrast is on; the rest keeps 4.5:1.</summary>
	public static readonly string[] PrimaryText = { "TextPrimary", "TextOnFrame", "TextOnHero", "TextOnTooltip" };

	/// <summary>A colour with Increase Contrast's dictionary over the tokens: its own brush if it has one, else the tokens'.</summary>
	private static Color HighContrastColorOf(IResourceProvider highContrast, string key, ThemeVariant theme) =>
		highContrast.TryGetResource(key, theme, out object value) && value is ISolidColorBrush brush ? brush.Color : ColorOf(key, theme);

	/// <summary>Every pair again with Styles/Contrast.High.axaml applied: 7:1 for primary text, 4.5:1 for the rest.</summary>
	[AvaloniaFact]
	public void WithIncreaseContrast_PrimaryTextHas7To1_AndTheRest4Point5To1()
	{
		IResourceProvider hc = HighContrast.Load();
		List<string> failures = new();
		foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
		{
			foreach ((string text, string background) in Pairs)
			{
				double need = PrimaryText.Contains(text) ? 7 : 4.5;
				double ratio = Ratio(HighContrastColorOf(hc, text, theme), HighContrastColorOf(hc, background, theme));
				if (ratio < need)
				{
					failures.Add($"{theme}: {text} on {background} is {ratio:0.00}:1, needs {need}:1");
				}
			}
		}
		Assert.True(failures.Count == 0, string.Join("\n", failures));
	}

	/// <summary>With Increase Contrast, borders, dividers and secondary text stand out more, and cards lose their shadow.</summary>
	[AvaloniaFact]
	public void WithIncreaseContrast_BordersAndSecondaryText_AreStronger_AndCardsLoseTheirShadow()
	{
		IResourceProvider hc = HighContrast.Load();
		foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
		{
			Color surface = ColorOf("BgSurface", theme);
			foreach (string border in new[] { "BorderDefault", "BorderStrong", "BorderSubtle", "BorderCard", "TextControlBorderBrush", "ButtonBorderBrush", "DataGridGridLinesBrush" })
			{
				Assert.True(Ratio(HighContrastColorOf(hc, border, theme), surface) > Ratio(ColorOf(border, theme), surface) + 0.5, $"{theme} {border}");
			}
			// Inputs and cards are outlined clearly enough to see (WCAG's 3:1 for a control's edge)
			Assert.True(Ratio(HighContrastColorOf(hc, "BorderStrong", theme), surface) >= 3, $"{theme} input edges");
			Assert.True(Ratio(HighContrastColorOf(hc, "BorderCard", theme), surface) >= 3, $"{theme} card edges");
			foreach (string text in new[] { "TextSecondary", "TextTertiary", "TextOnFrameSecondary", "TextOnFrameTertiary" })
			{
				string background = text.Contains("Frame") ? "BgFrame" : "BgSurface";
				Assert.True(Ratio(HighContrastColorOf(hc, text, theme), ColorOf(background, theme)) > Ratio(ColorOf(text, theme), ColorOf(background, theme)), $"{theme} {text}");
			}
			Assert.True(hc.TryGetResource("ShadowCard", theme, out object shadow));
			BoxShadows shadows = (BoxShadows)shadow!;
			for (int i = 0; i < shadows.Count; i++)
			{
				Assert.Equal(0, shadows[i].Color.A);
			}
		}
		Assert.True(hc.TryGetResource("FocusRingOpacity", null, out object opacity));
		Assert.Equal(1.0, (double)opacity!);
	}

	/// <summary>
	/// Every brush in the tokens made from a colour Increase Contrast changes is repeated in its dictionary, so no border
	/// or label stays faint (a new brush added to the tokens fails this until it is added there too).
	/// </summary>
	[Fact]
	public void IncreaseContrast_RepeatsEveryBrushMadeFromAColourItChanges()
	{
		string styles = Path.Combine(TestPaths.Root, "src", "Sde.App", "Styles");
		string tokens = File.ReadAllText(Path.Combine(styles, "Tokens.axaml"));
		string high = File.ReadAllText(Path.Combine(styles, "Contrast.High.axaml"));
		List<string> missing = new();
		foreach (string theme in new[] { "Light", "Dark" })
		{
			string ThemeBlock(string xaml)
			{
				int start = xaml.IndexOf($"<ResourceDictionary x:Key=\"{theme}\">", StringComparison.Ordinal);
				Assert.True(start >= 0, theme);
				return xaml[start..xaml.IndexOf("</ResourceDictionary>", start, StringComparison.Ordinal)];
			}
			string highBlock = ThemeBlock(high);
			HashSet<string> changed = System.Text.RegularExpressions.Regex.Matches(highBlock, "<Color x:Key=\"([A-Za-z0-9]+)\">").Select(m => m.Groups[1].Value).ToHashSet();
			Assert.Contains("BorderStrongColor", changed);
			HashSet<string> repeated = System.Text.RegularExpressions.Regex.Matches(highBlock, "<SolidColorBrush x:Key=\"([A-Za-z0-9]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
			foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(ThemeBlock(tokens), "<SolidColorBrush x:Key=\"([A-Za-z0-9]+)\" Color=\"\\{StaticResource ([A-Za-z0-9]+)\\}\""))
			{
				if (changed.Contains(m.Groups[2].Value) && !repeated.Contains(m.Groups[1].Value))
				{
					missing.Add($"{theme}: {m.Groups[1].Value} (made from {m.Groups[2].Value})");
				}
			}
		}
		Assert.True(missing.Count == 0, "Not repeated in Styles/Contrast.High.axaml:\n" + string.Join("\n", missing));
	}

	/// <summary>Turning Increase Contrast on merges the stronger colours over the tokens at once; turning it off takes them out.</summary>
	[AvaloniaFact]
	public void IncreaseContrast_ChangesTheColoursAtOnce_AndBack()
	{
		Application app = Application.Current!;
		Color normal = ColorOf("BorderCard", ThemeVariant.Light);
		bool? was = MacNative.IncreaseContrastOverride;
		try
		{
			MacNative.IncreaseContrastOverride = false;
			HighContrast.Apply(app, setting: true);
			Assert.True(HighContrast.IsOn);
			Assert.NotEqual(normal, ColorOf("BorderCard", ThemeVariant.Light));
			Assert.Equal(1.0, (double)app.FindResource("FocusRingOpacity")!);
			HighContrast.Apply(app, setting: false);
			Assert.False(HighContrast.IsOn);
			Assert.Equal(normal, ColorOf("BorderCard", ThemeVariant.Light));
			// The Mac's own Increase Contrast turns it on without the setting
			MacNative.IncreaseContrastOverride = true;
			HighContrast.Apply(app, setting: false);
			Assert.True(HighContrast.IsOn);
		}
		finally
		{
			MacNative.IncreaseContrastOverride = was;
			HighContrast.Set(app, false);
		}
		Assert.Equal(0.5, (double)app.FindResource("FocusRingOpacity")!);
	}

	[AvaloniaFact]
	public void TheThemesDiffer_AndTheAccentIsSdesWaterBlue()
	{
		Assert.NotEqual(ColorOf("BgSurface", ThemeVariant.Light), ColorOf("BgSurface", ThemeVariant.Dark));
		Assert.Equal(SystemAccent.Fallback, ColorOf("AccentFill", ThemeVariant.Light));
		Assert.True(Application.Current!.TryGetResource("AccentFallbackColor", null, out object fallback));
		Assert.Equal(SystemAccent.Fallback, (Color)fallback);
		// Fluent's own accent (check boxes, sliders, switches) starts from the same colour.
		Assert.True(Application.Current.TryGetResource("SystemAccentColor", ThemeVariant.Light, out object fluent));
		Assert.Equal(SystemAccent.Fallback, (Color)fluent);
	}

	/// <summary>Whatever accent the Mac is set to, the colours derived from it keep text at 4.5:1.</summary>
	[Theory]
	[InlineData("#007AFF")] // blue (and multicolour)
	[InlineData("#A550A7")] // purple
	[InlineData("#F74F9E")] // pink
	[InlineData("#FF5257")] // red
	[InlineData("#F7821B")] // orange
	[InlineData("#FFC600")] // yellow
	[InlineData("#62BA46")] // green
	[InlineData("#8C8C8C")] // graphite
	public void EveryMacAccent_DerivesReadableColours(string accent)
	{
		List<string> failures = new();
		foreach ((ThemeVariant theme, Dictionary<string, Color> c) in SystemAccent.Derive(Color.Parse(accent)))
		{
			Color sidebar = theme == ThemeVariant.Light ? SystemAccent.LightSidebar : SystemAccent.DarkSidebar;
			Color surface = theme == ThemeVariant.Light ? SystemAccent.LightSurface : SystemAccent.DarkSurface;
			void Check(string what, Color a, Color b)
			{
				if (Ratio(a, b) < 4.5)
				{
					failures.Add($"{accent} {theme}: {what} is {Ratio(a, b):0.00}:1");
				}
			}
			Check("white on AccentFill", Colors.White, c["AccentFill"]);
			Check("Accent on the sidebar", c["Accent"], sidebar);
			Check("Accent on the surface", c["Accent"], surface);
			Check("Accent on AccentSoft", c["Accent"], c["AccentSoft"]);
		}
		Assert.True(failures.Count == 0, string.Join("\n", failures));
	}

	/// <summary>The surfaces the Mac's accent is checked against are the tokens' own sidebar and surface colours.</summary>
	[AvaloniaFact]
	public void TheSurfacesAnAccentIsCheckedAgainst_AreTheTokens()
	{
		Assert.Equal(ColorOf("BgSidebar", ThemeVariant.Light), SystemAccent.LightSidebar);
		Assert.Equal(ColorOf("BgSurface", ThemeVariant.Light), SystemAccent.LightSurface);
		Assert.Equal(ColorOf("BgSidebar", ThemeVariant.Dark), SystemAccent.DarkSidebar);
		Assert.Equal(ColorOf("BgSurface", ThemeVariant.Dark), SystemAccent.DarkSurface);
	}

	[Fact]
	public void TheRatioIsComputedAsWcagSays()
	{
		Assert.Equal(21.0, Ratio(Colors.Black, Colors.White), 2);
		Assert.Equal(1.0, Ratio(Colors.Gray, Colors.Gray), 2);
	}
}
