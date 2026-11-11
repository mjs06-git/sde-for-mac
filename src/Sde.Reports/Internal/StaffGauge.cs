using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using Sde.Reports.Fonts;
using SkiaSharp;

namespace Sde.Reports.Internal;

/// <summary>
/// The staff gauge under a result: a horizontal track with a tick every 10 percent (taller at 0, 50 and 100), water
/// from 0 to the percentage, and a red high-water mark at the threshold (50 percent) that stands above and below the
/// track. Water is blue below the threshold, amber within 10 points of it and red at or above it. The tick labels and
/// the mark carry the meaning, so it reads the same printed in greyscale; the percentage itself is printed as text
/// beside it.
/// <para>
/// MigraDoc has no lines or arbitrary shapes, so the gauge is drawn with SkiaSharp (like the event summary's dot
/// plot) into a one-page PDF and placed as an image, which keeps it vector and sharp at any print size. Its labels
/// are drawn as letter outlines, so the gauge adds no text to the page and no picture to the PDF's image list.
/// </para>
/// </summary>
internal static class StaffGauge
{
	private const float Height = 19.5f;      // points
	private const float Side = 8f;           // room for the "0" and "100%" labels to centre on their ticks
	private const float MarkTop = 0.2f;
	private const float TrackTop = 2.5f;
	private const float TrackBottom = 8.5f;
	private const float MarkBottom = 11f;
	private const float TickShort = 10.5f;
	private const float TickTall = 12f;
	private const float LabelBaseline = 18.8f;
	private const float LabelSize = 6f;

	/// <summary>Adds the gauge as a paragraph holding the drawing, <paramref name="widthPoints"/> wide.</summary>
	public static Paragraph Add(Cell cell, decimal percent, decimal threshold, double widthPoints, double spaceBefore)
	{
		Paragraph p = cell.AddParagraph();
		p.Format.SpaceBefore = Unit.FromPoint(spaceBefore);
		MigraDoc.DocumentObjectModel.Shapes.Image img = p.AddImage("base64:" + Convert.ToBase64String(Pdf(percent, threshold, widthPoints)));
		img.LockAspectRatio = true;
		img.Width = Unit.FromPoint(widthPoints);
		return p;
	}

	/// <summary>The water's colour for a percentage: blue below the threshold, amber within 10 points of it, red at or above it.</summary>
	public static Color WaterColor(decimal percent, decimal threshold) =>
		percent >= threshold ? Theme.GaugeRed : percent >= threshold - 10 ? Theme.GaugeClose : Theme.GaugeWater;

	/// <summary>The gauge as a one-page PDF, <paramref name="widthPoints"/> by 19.5 points, with no background.</summary>
	public static byte[] Pdf(decimal percent, decimal threshold, double widthPoints)
	{
		using MemoryStream stream = new MemoryStream();
		using (SKDocument doc = SKDocument.CreatePdf(stream))
		{
			SKCanvas canvas = doc.BeginPage((float)widthPoints, Height);
			Draw(canvas, percent, threshold, (float)widthPoints);
			doc.EndPage();
			doc.Close();
		}
		return stream.ToArray();
	}

	private static void Draw(SKCanvas canvas, decimal percent, decimal threshold, float width)
	{
		float left = Side;
		float right = width - Side;
		float X(double pct) => left + (float)(Math.Clamp(pct, 0, 100) / 100.0) * (right - left);

		SKRect track = new SKRect(left, TrackTop, right, TrackBottom);
		using (SKPaint paper = new SKPaint { Color = SKColors.White, IsAntialias = true })
		{
			canvas.DrawRect(track, paper);
		}
		double clamped = Math.Clamp((double)percent, 0, 100);
		if (clamped > 0)
		{
			using SKPaint water = new SKPaint { Color = Sk(WaterColor(percent, threshold)), IsAntialias = true };
			canvas.DrawRect(new SKRect(left, TrackTop, X(clamped), TrackBottom), water);
		}
		using (SKPaint edge = new SKPaint { Color = Sk(Theme.Muted), Style = SKPaintStyle.Stroke, StrokeWidth = 0.4f, IsAntialias = true })
		{
			canvas.DrawRect(track, edge);
		}
		using (SKPaint tick = new SKPaint { Color = Sk(Theme.Muted), Style = SKPaintStyle.Stroke, StrokeWidth = 0.45f, IsAntialias = true })
		{
			for (int i = 0; i <= 100; i += 10)
			{
				canvas.DrawLine(X(i), TrackBottom, X(i), i % 50 == 0 ? TickTall : TickShort, tick);
			}
		}

		// The high-water mark, with a white edge so it stands out from red water.
		float mx = X((double)threshold);
		using (SKPaint halo = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 2.6f, IsAntialias = true })
		{
			canvas.DrawLine(mx, TrackTop, mx, TrackBottom, halo);
		}
		using (SKPaint mark = new SKPaint { Color = Sk(Theme.GaugeRed), Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, IsAntialias = true })
		{
			canvas.DrawLine(mx, MarkTop, mx, MarkBottom, mark);
		}

		using SKTypeface regular = Typeface("PublicSans-Regular");
		using SKTypeface semiBold = Typeface("PublicSans-SemiBold");
		Label(canvas, "0", X(0), regular, Theme.Muted);
		Label(canvas, threshold.ToString("0.##", Fmt.Us) + "%", mx, semiBold, Theme.Ink);
		Label(canvas, "100%", X(100), regular, Theme.Muted);
	}

	/// <summary>A label centred on <paramref name="x"/>, drawn as letter outlines (not text).</summary>
	private static void Label(SKCanvas canvas, string text, float x, SKTypeface face, Color color)
	{
		using SKFont font = new SKFont(face, LabelSize);
		float w = font.MeasureText(text);
		using SKPath path = font.GetTextPath(text, new SKPoint(x - w / 2, LabelBaseline));
		using SKPaint fill = new SKPaint { Color = Sk(color), IsAntialias = true };
		canvas.DrawPath(path, fill);
	}

	private static SKTypeface Typeface(string face)
	{
		using SKData data = SKData.CreateCopy(ReportFonts.Face(face));
		return SKTypeface.FromData(data) ?? SKTypeface.Default;
	}

	/// <summary>A report colour as a SkiaSharp colour, for drawings.</summary>
	public static SKColor Sk(Color c) => new SKColor((byte)c.R, (byte)c.G, (byte)c.B);
}
