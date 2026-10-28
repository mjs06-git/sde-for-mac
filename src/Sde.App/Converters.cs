using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Sde.App.ViewModels.Editor;

namespace Sde.App;

/// <summary>Value converters used by the views.</summary>
public static class Converters
{
	/// <summary>The name of the result's text colour, e.g. "StatusSubstantialFg", for <see cref="ThemeBrush"/>.</summary>
	public static readonly IValueConverter StatusForegroundKey = new FuncValueConverter<ResultState, string>(s => $"Status{s}Fg");

	/// <summary>The damaged percentage's colour: red when substantially damaged, otherwise ordinary text (colour is kept for the one result that matters).</summary>
	public static readonly IValueConverter PercentForegroundKey = new FuncValueConverter<ResultState, string>(s => s == ResultState.Substantial ? "StatusSubstantialFg" : "TextPrimary");

	/// <summary>The result dot's colour: the status hues, brighter than text so a small dot reads.</summary>
	public static readonly IValueConverter StatusDotKey = new FuncValueConverter<ResultState, string>(s => $"Status{s}Dot");

	/// <summary>The name of the result's background colour, e.g. "StatusSubstantialBg", for <see cref="ThemeBrush"/>.</summary>
	public static readonly IValueConverter StatusBackgroundKey = new FuncValueConverter<ResultState, string>(s => $"Status{s}Bg");

	/// <summary>"$1,234.56" or "—" for null.</summary>
	public static readonly IValueConverter Money = new FuncValueConverter<decimal?, string>(v => v is decimal d ? d.ToString("C2", CultureInfo.GetCultureInfo("en-US")) : "–");

	/// <summary>"$1,235" (whole dollars) or "—".</summary>
	public static readonly IValueConverter MoneyWhole = new FuncValueConverter<decimal?, string>(v => v is decimal d ? d.ToString("C0", CultureInfo.GetCultureInfo("en-US")) : "–");

	/// <summary>"12.5%" or "—".</summary>
	public static readonly IValueConverter Percent = new FuncValueConverter<decimal?, string>(v => v is decimal d ? d.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "–");

	/// <summary>"1,450 sq ft" or "—".</summary>
	public static readonly IValueConverter SquareFeet = new FuncValueConverter<decimal?, string>(v => v is decimal d ? d.ToString("N0", CultureInfo.GetCultureInfo("en-US")) + " sq ft" : "–");

	/// <summary>True when the structure-list filter equals the parameter (e.g. "Substantial").</summary>
	public static readonly IValueConverter FilterIs = new FuncValueConverter<Sde.App.ViewModels.StructureFilter, string, bool>((f, p) => f.ToString() == p);

	public static readonly IValueConverter BoldIf = new FuncValueConverter<bool, FontWeight>(b => b ? FontWeight.SemiBold : FontWeight.Normal);

	public static readonly IValueConverter IsPositive = new FuncValueConverter<int, bool>(v => v > 0);

	public static readonly IValueConverter IsZero = new FuncValueConverter<int, bool>(v => v == 0);

	/// <summary>The name of the issue's colour, required (red) / recommended (amber) / suggested (green), for <see cref="ThemeBrush"/>.</summary>
	public static readonly IValueConverter IssueBrushKey = new FuncValueConverter<Sde.Core.Assessments.IssueSeverity, string>(s => s switch
	{
		Sde.Core.Assessments.IssueSeverity.Required => "RequiredFg",
		Sde.Core.Assessments.IssueSeverity.Recommended => "RecommendedFg",
		_ => "SuggestedFg",
	});

	/// <summary>A shortcut as a Mac menu shows it: ⌃⌥⇧⌘ then the key, with no plus signs ("⇧⌘N").</summary>
	public static readonly IValueConverter MacKeyGesture = new FuncValueConverter<Avalonia.Input.KeyGesture, string>(MacGesture);

	public static string MacGesture(Avalonia.Input.KeyGesture gesture)
	{
		if (gesture is null)
		{
			return null;
		}
		Avalonia.Input.KeyModifiers m = gesture.KeyModifiers;
		string mods = (m.HasFlag(Avalonia.Input.KeyModifiers.Control) ? "⌃" : "") + (m.HasFlag(Avalonia.Input.KeyModifiers.Alt) ? "⌥" : "")
			+ (m.HasFlag(Avalonia.Input.KeyModifiers.Shift) ? "⇧" : "") + (m.HasFlag(Avalonia.Input.KeyModifiers.Meta) ? "⌘" : "");
		return mods + MacKey(gesture.Key);
	}

	private static string MacKey(Avalonia.Input.Key key) => key switch
	{
		>= Avalonia.Input.Key.A and <= Avalonia.Input.Key.Z => key.ToString(),
		>= Avalonia.Input.Key.D0 and <= Avalonia.Input.Key.D9 => ((char)('0' + (key - Avalonia.Input.Key.D0))).ToString(),
		>= Avalonia.Input.Key.F1 and <= Avalonia.Input.Key.F24 => key.ToString(),
		Avalonia.Input.Key.OemComma => ",",
		Avalonia.Input.Key.OemPeriod => ".",
		Avalonia.Input.Key.OemOpenBrackets => "[",
		Avalonia.Input.Key.OemCloseBrackets => "]",
		Avalonia.Input.Key.OemPlus or Avalonia.Input.Key.Add => "+",
		Avalonia.Input.Key.OemMinus or Avalonia.Input.Key.Subtract => "-",
		Avalonia.Input.Key.OemQuestion => "/",
		Avalonia.Input.Key.OemSemicolon => ";",
		Avalonia.Input.Key.OemQuotes => "'",
		Avalonia.Input.Key.Enter => "↩",
		Avalonia.Input.Key.Escape => "⎋",
		Avalonia.Input.Key.Back => "⌫",
		Avalonia.Input.Key.Delete => "⌦",
		Avalonia.Input.Key.Tab => "⇥",
		Avalonia.Input.Key.Space => "Space",
		Avalonia.Input.Key.Left => "←",
		Avalonia.Input.Key.Right => "→",
		Avalonia.Input.Key.Up => "↑",
		Avalonia.Input.Key.Down => "↓",
		_ => key.ToString(),
	};
}
