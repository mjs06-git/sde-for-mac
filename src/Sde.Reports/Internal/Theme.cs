using MigraDoc.DocumentObjectModel;
using Sde.Reports.Fonts;

namespace Sde.Reports.Internal;

/// <summary>
/// Colors, type sizes and paragraph styles shared by all reports. The colors are the "High-Water" brand core from the
/// app's light theme (src/Sde.App/Styles/Tokens.axaml; print is always light): navy ink, water blue, warm rules and
/// the staff gauge's red. BrandAgreementTests in tests/Sde.App.Tests checks that they still match the app.
/// </summary>
internal static class Theme
{
	/// <summary>Body text (TextPrimary).</summary>
	public static readonly Color Ink = new Color(0x14, 0x23, 0x3A);
	/// <summary>Labels, notes, header and footer (TextSecondary).</summary>
	public static readonly Color Muted = new Color(0x4A, 0x55, 0x67);
	/// <summary>Hairlines under fields (BorderDefault).</summary>
	public static readonly Color Rule = new Color(0xE2, 0xDB, 0xCF);
	/// <summary>Shaded table headers and placeholder boxes (BgSubtle).</summary>
	public static readonly Color Faint = new Color(0xF5, 0xF1, 0xEA);
	/// <summary>Section titles and their rules (the accent as text, Accent).</summary>
	public static readonly Color Accent = new Color(0x1A, 0x57, 0x91);

	// Determination tones: text/edge color and a light fill (the app's StatusSubstantial, StatusNotSubstantial and
	// StatusClose pairs, which are DangerFg/DangerBg, SuccessFg/SuccessBg and WarningFg/WarningBg).
	public static readonly Color SubstantialInk = new Color(0xB5, 0x0E, 0x28);
	public static readonly Color SubstantialFill = new Color(0xFB, 0xE7, 0xEA);
	public static readonly Color NotSubstantialInk = new Color(0x1A, 0x6E, 0x37);
	public static readonly Color NotSubstantialFill = new Color(0xE2, 0xF2, 0xE6);
	public static readonly Color OtherInk = new Color(0x8A, 0x47, 0x00);
	public static readonly Color OtherFill = new Color(0xFB, 0xEB, 0xD3);

	// The staff gauge: water blue (AccentFill) below the threshold, amber within 10 points of it, gauge red
	// (DangerFill) at or above it and for the high-water mark.
	public static readonly Color GaugeWater = new Color(0x1E, 0x62, 0xA0);
	public static readonly Color GaugeClose = new Color(0xB8, 0x6E, 0x0A);
	public static readonly Color GaugeRed = new Color(0xC8, 0x10, 0x2E);

	public const double BodySize = 8;
	public const double LabelSize = 7;
	public const double TitleSize = 8.5;
	public const double NoteSize = 6.8;

	public const string LabelStyle = "SdeLabel";
	public const string ValueStyle = "SdeValue";
	public const string TitleStyle = "SdeTitle";
	public const string NoteStyle = "SdeNote";

	public static Color ToneInk(Determination d) => d switch
	{
		Determination.Substantial => SubstantialInk,
		Determination.NotSubstantial => NotSubstantialInk,
		_ => OtherInk,
	};

	public static Color ToneFill(Determination d) => d switch
	{
		Determination.Substantial => SubstantialFill,
		Determination.NotSubstantial => NotSubstantialFill,
		_ => OtherFill,
	};

	/// <summary>A new document with the report styles. Installs the embedded fonts first.</summary>
	public static Document NewDocument(string title, string subject)
	{
		ReportFonts.EnsureRegistered();
		Document doc = new Document();
		doc.Info.Title = title;
		doc.Info.Subject = subject;
		doc.Info.Author = "Substantial Damage Estimator";

		Style normal = doc.Styles[StyleNames.Normal];
		normal.Font.Name = ReportFonts.Family;
		normal.Font.Size = BodySize;
		normal.Font.Color = Ink;
		normal.ParagraphFormat.SpaceBefore = 0;
		normal.ParagraphFormat.SpaceAfter = 0;

		Style label = doc.Styles.AddStyle(LabelStyle, StyleNames.Normal);
		label.Font.Size = LabelSize;
		label.Font.Color = Muted;

		Style value = doc.Styles.AddStyle(ValueStyle, StyleNames.Normal);
		value.Font.Size = BodySize;
		value.Font.Color = Ink;

		Style heading = doc.Styles.AddStyle(TitleStyle, StyleNames.Normal);
		heading.Font.Size = TitleSize;
		heading.Font.Bold = true;
		heading.Font.Color = Accent;

		Style note = doc.Styles.AddStyle(NoteStyle, StyleNames.Normal);
		note.Font.Size = NoteSize;
		note.Font.Color = Muted;

		foreach (string s in new[] { StyleNames.Header, StyleNames.Footer })
		{
			doc.Styles[s].Font.Name = ReportFonts.Family;
			doc.Styles[s].Font.Size = 7.5;
			doc.Styles[s].Font.Color = Muted;
		}
		return doc;
	}
}
