using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Platform;

namespace Sde.App.Services;

/// <summary>
/// Increase Contrast: stronger borders, dividers and focus rings, darker secondary text (lighter in dark mode) and clear
/// borders in place of card shadows (Styles/Contrast.High.axaml, merged over the tokens). On while the Mac's Increase
/// Contrast is on (System Settings, Accessibility, Display) or while "Increase contrast" is on in Settings, Appearance.
/// The Mac's setting is read at launch and again whenever the Mac reports a change of appearance, which it does when
/// Increase Contrast is turned on or off, so the app follows it without a restart.
/// </summary>
public static class HighContrast
{
	private static readonly Uri Source = new Uri("avares://SDE/Styles/Contrast.High.axaml");

	private static ResourceInclude _merged;
	private static bool _setting;
	private static EventHandler<PlatformColorValues> _following;

	/// <summary>True while the stronger colours are merged in.</summary>
	public static bool IsOn => _merged != null;

	/// <summary>Raised after the stronger colours go in or come out.</summary>
	public static event Action Changed;

	/// <summary>Whether the Mac asks for more contrast: its Increase Contrast setting, or a high-contrast appearance.</summary>
	public static bool SystemWantsIt(Application app)
	{
		if (MacNative.IncreaseContrastOverride is bool forced)
		{
			return forced;
		}
		return MacNative.IncreaseContrast || app.PlatformSettings?.GetColorValues().ContrastPreference == ColorContrastPreference.High;
	}

	/// <summary>
	/// Turns the stronger colours on or off for the app's own setting and the Mac's, and keeps following the Mac's
	/// changes from then on.
	/// </summary>
	public static void Apply(Application app, bool setting)
	{
		_setting = setting;
		if (_following == null && app.PlatformSettings is IPlatformSettings platform)
		{
			_following = (_, _) => Set(app, _setting || SystemWantsIt(app));
			platform.ColorValuesChanged += _following;
		}
		Set(app, setting || SystemWantsIt(app));
	}

	/// <summary>Merges the stronger colours in (last, so they win over the tokens and the Compact sizes) or takes them out.</summary>
	public static void Set(Application app, bool on)
	{
		if (on == IsOn)
		{
			return;
		}
		if (on)
		{
			_merged = new ResourceInclude(new Uri("avares://SDE/")) { Source = Source };
			app.Resources.MergedDictionaries.Add(_merged);
		}
		else
		{
			app.Resources.MergedDictionaries.Remove(_merged);
			_merged = null;
		}
		Changed?.Invoke();
	}

	/// <summary>The stronger colours on their own, without merging them in (for checking them).</summary>
	public static IResourceProvider Load() => new ResourceInclude(new Uri("avares://SDE/")) { Source = Source };
}
