using Avalonia;
using Avalonia.Media;
using Sde.App.ViewModels;

namespace Sde.App.Views;

/// <summary>
/// The Summary's progress over time: the running total of completed assessments by inspection date, as a line over a
/// soft area on light gridlines, with dates along the bottom and the latest total at the line's end. One day alone is
/// drawn as a single point. A screen reader hears the totals in words.
/// </summary>
public sealed class ProgressChart : ChartBase
{
	public static readonly StyledProperty<IReadOnlyList<ProgressPoint>> PointsProperty =
		AvaloniaProperty.Register<ProgressChart, IReadOnlyList<ProgressPoint>>(nameof(Points));

	// The drawing's own geometry
	private const double LineThickness = 2.5;
	private const double DotRadius = 4.5;
	private const int MaxDateLabels = 5;

	static ProgressChart()
	{
		AffectsRender<ProgressChart>(PointsProperty);
	}

	public ProgressChart() => UpdateSpokenName();

	/// <inheritdoc cref="PointsProperty"/>
	public IReadOnlyList<ProgressPoint> Points
	{
		get => GetValue(PointsProperty);
		set => SetValue(PointsProperty, value);
	}

	/// <summary>For example "Progress over time: 40 assessments completed from Sep 15 to Sep 28, 2026. Sep 15, 2; Sep 16, 6; ..."</summary>
	public override string SpokenText
	{
		get
		{
			IReadOnlyList<ProgressPoint> points = Points;
			if (points == null || points.Count == 0)
			{
				return "Progress over time: no completed assessments with an inspection date yet";
			}
			ProgressPoint last = points[^1];
			string total = $"{last.Total.ToString("N0", Us)} {(last.Total == 1 ? "assessment" : "assessments")} completed";
			if (points.Count == 1)
			{
				return $"Progress over time: {total} on {last.Date.ToString("MMM d, yyyy", Us)}.";
			}
			// Each day the total changed, in order
			IEnumerable<string> days = points.Where((p, i) => i == 0 || p.Total != points[i - 1].Total)
				.Select(p => $"{p.Date.ToString("MMM d", Us)}, {p.Total.ToString("N0", Us)}");
			return $"Progress over time: {total} from {points[0].Date.ToString("MMM d", Us)} to {last.Date.ToString("MMM d, yyyy", Us)}. {string.Join("; ", days)}.";
		}
	}

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property == PointsProperty)
		{
			UpdateSpokenName();
		}
	}

	public override void Render(DrawingContext context)
	{
		IReadOnlyList<ProgressPoint> points = Points;
		if (points == null || points.Count == 0 || Bounds.Width < 40 || Bounds.Height < 40)
		{
			return;
		}
		IBrush quiet = Brush("TextTertiary", Brushes.Gray);
		IBrush ink = Brush("TextSecondary", Brushes.Gray);
		double row = Caption("0", quiet).Height + LabelGap;
		(int step, int top) = NiceScale(points[^1].Total);
		double left = AxisWidth(top);
		Rect plot = new Rect(left, row, Bounds.Width - left - 8, Bounds.Height - 2 * row);
		if (plot.Width < 20 || plot.Height < 20)
		{
			return;
		}
		DrawGrid(context, plot, step, top);
		context.DrawRectangle(Brush("BorderStrong", Brushes.Gray), null, new Rect(plot.Left, Math.Round(plot.Bottom), plot.Width, 1));

		Point At(int i) => new Point(
			points.Count == 1 ? plot.Center.X : plot.Left + plot.Width * i / (points.Count - 1),
			plot.Bottom - plot.Height * points[i].Total / top);

		IBrush line = Brush("ChartLine", Brushes.SteelBlue);
		if (points.Count > 1)
		{
			// The area under the line, then the line
			StreamGeometry area = new StreamGeometry();
			using (StreamGeometryContext g = area.Open())
			{
				g.BeginFigure(new Point(plot.Left, plot.Bottom), true);
				for (int i = 0; i < points.Count; i++)
				{
					g.LineTo(At(i));
				}
				g.LineTo(new Point(plot.Right, plot.Bottom));
				g.EndFigure(true);
			}
			context.DrawGeometry(Brush("ChartArea", Brushes.LightBlue), null, area);

			StreamGeometry path = new StreamGeometry();
			using (StreamGeometryContext g = path.Open())
			{
				g.BeginFigure(At(0), false);
				for (int i = 1; i < points.Count; i++)
				{
					g.LineTo(At(i));
				}
				g.EndFigure(false);
			}
			context.DrawGeometry(null, new Pen(line, LineThickness, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round), path);
		}

		// The latest total: a dot on the line's end, ringed in the card's white, with the number above it
		Point end = At(points.Count - 1);
		context.DrawEllipse(line, new Pen(Brush("BgSurface", Brushes.White), 2), end, DotRadius, DotRadius);
		FormattedText total = Caption(points[^1].Total.ToString("N0", Us), Brush("TextPrimary", Brushes.Black), FontWeight.SemiBold);
		double tx = Math.Clamp(end.X - total.Width / 2, plot.Left, Bounds.Width - total.Width);
		context.DrawText(total, new Point(tx, Math.Max(0, end.Y - DotRadius - LabelGap / 2 - total.Height)));

		// Dates along the bottom: the first and last, and evenly spaced ones between
		List<int> ticks = DateTicks(points.Count);
		double y = plot.Bottom + LabelGap / 2 + 1;
		foreach (int i in ticks)
		{
			FormattedText label = Caption(points[i].Date.ToString("MMM d", Us), i == points.Count - 1 ? ink : quiet);
			double x = At(i).X - label.Width / 2;
			if (points.Count > 1 && i == 0)
			{
				x = plot.Left;
			}
			else if (points.Count > 1 && i == points.Count - 1)
			{
				x = plot.Right - label.Width;
			}
			context.DrawText(label, new Point(x, y));
		}
	}

	/// <summary>Which points get a date under them: up to <see cref="MaxDateLabels"/>, the first and the last always.</summary>
	private static List<int> DateTicks(int count)
	{
		if (count <= MaxDateLabels)
		{
			return Enumerable.Range(0, count).ToList();
		}
		List<int> ticks = new();
		for (int k = 0; k < MaxDateLabels; k++)
		{
			ticks.Add((int)Math.Round((count - 1) * k / (double)(MaxDateLabels - 1)));
		}
		return ticks.Distinct().ToList();
	}
}
