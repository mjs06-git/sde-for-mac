using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Sde.App.Services;

/// <summary>
/// Follows the Mac's accent colour (System Settings, Appearance), the way native apps do. The tokens in
/// Styles/Tokens.axaml ship a blue that matches the Mac's default; on a Mac set to another accent the accent brushes
/// are recoloured from it, keeping text on and in the accent at 4.5:1 (the same rule ContrastTests checks).
/// </summary>
public static class SystemAccent
{
	/// <summary>The accent tokens' colours when the Mac doesn't report one (tests, other platforms).</summary>
	public static readonly Color Fallback = Color.Parse("#0A64D6");

	private static readonly Color White = Colors.White;
	private static readonly Color LightSidebar = Color.Parse("#EBEBEA");
	private static readonly Color DarkSidebar = Color.Parse("#2B2B2D");
	private static readonly Color DarkSurface = Color.Parse("#252527");

	/// <summary>Recolours the accent from the Mac's setting now and whenever it changes.</summary>
	public static void Follow(Application app)
	{
		IPlatformSettings settings = app.PlatformSettings;
		if (settings is null)
		{
			return;
		}
		Apply(app, settings.GetColorValues().AccentColor1);
		settings.ColorValuesChanged += (_, values) => Apply(app, values.AccentColor1);
	}

	/// <summary>Sets every accent brush, the focus halo and Fluent's own accent from <paramref name="accent"/>.</summary>
	public static void Apply(Application app, Color accent)
	{
		accent = Color.FromRgb(accent.R, accent.G, accent.B);
		foreach ((ThemeVariant theme, Dictionary<string, Color> colours) in Derive(accent))
		{
			foreach ((string key, Color colour) in colours)
			{
				if (app.TryGetResource(key, theme, out object value) && value is SolidColorBrush brush)
				{
					brush.Color = colour;
				}
			}
			if (ThemeDictionary(app, theme) is IResourceDictionary dictionary)
			{
				Color halo = colours["Focus"];
				dictionary["FocusHalo"] = new BoxShadows(new BoxShadow { Spread = 3, Color = Color.FromArgb(0x73, halo.R, halo.G, halo.B) });
			}
		}
		if (app.Styles.OfType<FluentTheme>().FirstOrDefault() is FluentTheme fluent)
		{
			foreach (ColorPaletteResources palette in fluent.Palettes.Values)
			{
				palette.Accent = accent;
			}
		}
	}

	/// <summary>The accent brushes' colours in each theme, derived from one accent colour.</summary>
	public static IEnumerable<(ThemeVariant, Dictionary<string, Color>)> Derive(Color accent)
	{
		Color lightFill = Darken(accent, c => Ratio(White, c) >= 4.5);
		Color lightText = Darken(accent, c => Ratio(c, LightSidebar) >= 4.5 && Ratio(c, Mix(accent, White, 0.12)) >= 4.5);
		yield return (ThemeVariant.Light, Brushes(lightFill, lightText, Mix(accent, White, 0.12), Mix(accent, White, 0.17), darkenSteps: true));

		Color darkFill = Darken(accent, c => Ratio(White, c) >= 4.5);
		Color darkSoft = Mix(accent, DarkSurface, 0.25);
		Color darkText = Lighten(accent, c => Ratio(c, DarkSidebar) >= 4.5 && Ratio(c, darkSoft) >= 4.5);
		yield return (ThemeVariant.Dark, Brushes(darkFill, darkText, darkSoft, Mix(accent, DarkSurface, 0.32), darkenSteps: false));
	}

	private static Dictionary<string, Color> Brushes(Color fill, Color text, Color soft, Color softHover, bool darkenSteps)
	{
		Color textHover = darkenSteps ? Scale(text, 0.9) : Mix(White, text, 0.18);
		Color textPressed = darkenSteps ? Scale(text, 0.8) : Scale(text, 0.92);
		Color fillHover = Scale(fill, 0.92);
		Color fillPressed = Scale(fill, 0.84);
		Dictionary<string, Color> c = new()
		{
			["Accent"] = text, ["AccentHover"] = textHover, ["AccentPressed"] = textPressed,
			["AccentFill"] = fill, ["AccentFillHover"] = fillHover, ["AccentFillPressed"] = fillPressed,
			["AccentSoft"] = soft, ["AccentSoftHover"] = softHover, ["Focus"] = darkenSteps ? fill : text,
			["InfoFg"] = text,
			// Fluent's accent buttons, pointed at the same colours
			["AccentButtonBackground"] = fill, ["AccentButtonBackgroundPointerOver"] = fillHover, ["AccentButtonBackgroundPressed"] = fillPressed,
			["AccentButtonBorderBrush"] = fill, ["AccentButtonBorderBrushPointerOver"] = fillHover, ["AccentButtonBorderBrushPressed"] = fillPressed,
			["SelectedRow"] = soft,
		};
		return c;
	}

	private static IResourceDictionary ThemeDictionary(Application app, ThemeVariant theme)
	{
		foreach (IResourceProvider provider in app.Resources.MergedDictionaries)
		{
			IResourceDictionary dictionary = provider switch
			{
				ResourceInclude include => include.Loaded as IResourceDictionary,
				IResourceDictionary d => d,
				_ => null,
			};
			if (dictionary is not null && dictionary.ThemeDictionaries.TryGetValue(theme, out IThemeVariantProvider found)
				&& found is IResourceDictionary themed && themed.ContainsKey("FocusHalo"))
			{
				return themed;
			}
		}
		return null;
	}

	private static Color Darken(Color c, Func<Color, bool> enough)
	{
		for (double f = 1; f > 0.05; f -= 0.02)
		{
			Color d = Scale(c, f);
			if (enough(d))
			{
				return d;
			}
		}
		return Colors.Black;
	}

	private static Color Lighten(Color c, Func<Color, bool> enough)
	{
		for (double t = 0; t <= 1; t += 0.02)
		{
			Color l = Mix(White, c, t);
			if (enough(l))
			{
				return l;
			}
		}
		return White;
	}

	private static Color Scale(Color c, double f) => Color.FromRgb((byte)Math.Round(c.R * f), (byte)Math.Round(c.G * f), (byte)Math.Round(c.B * f));

	/// <summary><paramref name="amount"/> of <paramref name="a"/> over <paramref name="b"/>.</summary>
	private static Color Mix(Color a, Color b, double amount) => Color.FromRgb(
		(byte)Math.Round(b.R + (a.R - b.R) * amount), (byte)Math.Round(b.G + (a.G - b.G) * amount), (byte)Math.Round(b.B + (a.B - b.B) * amount));

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
}
