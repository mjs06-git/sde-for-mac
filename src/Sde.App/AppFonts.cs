using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace Sde.App;

/// <summary>
/// The app's typeface, Public Sans: the same face the PDF reports and the phone form use, bundled with the app (the
/// files live once, in Sde.Reports/Fonts, SIL OFL 1.1). The Mac's system font and Inter follow it as fallbacks for
/// the few glyphs Public Sans lacks, such as the keyboard symbols in shortcuts. Inter also stays for the map labels.
/// </summary>
public static class AppFonts
{
	public const string PublicSans = "fonts:PublicSans#Public Sans";
	public const string System = ".AppleSystemUIFont";
	public const string Inter = "fonts:Inter#Inter";

	private static readonly Uri PublicSansKey = new Uri("fonts:PublicSans", UriKind.Absolute);
	private static readonly Uri PublicSansSource = new Uri("avares://SDE/Assets/Fonts", UriKind.Absolute);

	public static AppBuilder WithAppFonts(this AppBuilder builder) => builder
		.WithInterFont()
		.ConfigureFonts(fonts => fonts.AddFontCollection(new EmbeddedFontCollection(PublicSansKey, PublicSansSource)))
		.With(new FontManagerOptions
		{
			DefaultFamilyName = PublicSans,
			FontFallbacks = Fallbacks(),
		});

	private static FontFallback[] Fallbacks()
	{
		List<FontFallback> fallbacks = new List<FontFallback>();
		if (OperatingSystem.IsMacOS())
		{
			fallbacks.Add(new FontFallback { FontFamily = new FontFamily(System) });
		}
		fallbacks.Add(new FontFallback { FontFamily = new FontFamily(Inter) });
		return fallbacks.ToArray();
	}
}
