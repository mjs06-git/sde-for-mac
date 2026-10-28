using Avalonia;
using Avalonia.Media;

namespace Sde.App;

/// <summary>
/// The Mac's own system font (SF Pro, reached by its CoreText name), the way native apps look. Inter stays bundled
/// for the map labels and as the font on other platforms (the Linux CI runner), so layouts stay close there.
/// </summary>
public static class AppFonts
{
	public const string System = ".AppleSystemUIFont";
	public const string Inter = "fonts:Inter#Inter";

	public static AppBuilder WithMacFonts(this AppBuilder builder) => builder
		.WithInterFont()
		.With(new FontManagerOptions
		{
			DefaultFamilyName = OperatingSystem.IsMacOS() ? System : Inter,
			FontFallbacks = new[] { new FontFallback { FontFamily = new FontFamily(Inter) } },
		});
}
