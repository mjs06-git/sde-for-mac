using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;

namespace Sde.App.Services;

/// <summary>
/// Settings, Appearance: light or dark, SDE's accent or the Mac's, and the Comfortable or Compact sizes. The theme and
/// the accent change at once. The sizes are read when the app starts, because the styles take them as they load.
/// </summary>
public static class Appearance
{
	public const string Light = "Light";
	public const string Dark = "Dark";

	/// <summary>Start with the Compact sizes. Set before the app's styles load (Program, and the tests that look at Compact).</summary>
	public static bool StartCompact { get; set; }

	/// <summary>True once the Compact sizes were merged in for this run.</summary>
	public static bool IsCompact { get; private set; }

	/// <summary>Merges the Compact sizes over the tokens. Called once, straight after the app's XAML loads.</summary>
	public static void ApplyDensity(Application app)
	{
		if (!StartCompact || IsCompact)
		{
			return;
		}
		app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://SDE/")) { Source = new Uri("avares://SDE/Styles/Density.Compact.axaml") });
		IsCompact = true;
	}

	/// <summary>The theme a setting stands for: "Light", "Dark", anything else matches the Mac.</summary>
	public static ThemeVariant ThemeOf(string setting) => setting switch
	{
		Light => ThemeVariant.Light,
		Dark => ThemeVariant.Dark,
		_ => ThemeVariant.Default,
	};

	/// <summary>Applies the theme and the accent choice now.</summary>
	public static void Apply(Application app, AppSettings settings)
	{
		app.RequestedThemeVariant = ThemeOf(settings.Theme);
		if (settings.UseMacAccent && OperatingSystem.IsMacOS())
		{
			SystemAccent.Follow(app);
		}
		else
		{
			SystemAccent.UseBrand(app);
		}
	}
}
