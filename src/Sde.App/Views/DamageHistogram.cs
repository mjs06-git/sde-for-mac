using Avalonia;
using Avalonia.Media;
using Sde.App.ViewModels;

namespace Sde.App.Views;

/// <summary>
/// The Summary's damage distribution: how many assessments fall in each 10-point band of percent damaged, as bars on
/// light gridlines, with the substantial damage line drawn through the chart in the gauge's red. Bars at or above the
/// line are red, the band just under it amber, the rest water blue (each bar's colour comes with its
/// <see cref="HistogramBin"/>). A screen reader hears the counts in words.
/// </summary>
public sealed class DamageHistogram : ChartBase
{
	public static readonly StyledProperty<IReadOnlyList<HistogramBin>> BinsProperty =
		AvaloniaProperty.Register<DamageHistogram, IReadOnlyList<HistogramBin>>(nameof(Bins));

	/// <summary>Where the substantial damage line is drawn, in percent.</summary>
	public static readonly StyledProperty<double> ThresholdProperty =
		AvaloniaProperty.Register<DamageHistogram, double>(nameof(Threshold), 50);

	// The drawing's own geometry
	private const double BarShare = 0.68;
	private const double BarRadius = 4;
	private const double LineWidth = 2;

	static DamageHistogram()
	{
		AffectsRender<DamageHistogram>(BinsProperty, ThresholdProperty);
	}

	public DamageHistogram() => UpdateSpokenName();

	/// <inheritdoc cref="BinsProperty"/>
	public IReadOnlyList<HistogramBin> Bins
	{
		get => GetValue(BinsProperty);
		set => SetValue(BinsProperty, value);
	}

	/// <inheritdoc cref="ThresholdProperty"/>
	public double Threshold
	{
		get => GetValue(ThresholdProperty);
		set => SetValue(ThresholdProperty, value);
	}

	/// <summary>For example "Damage distribution: 0 to 10 percent, 2; 50 to 60 percent, 5. 19 at or above the 50 percent line."</summary>
	public override string SpokenText
	{
		get
		{
			if (Bins == null || !Bins.Any(b => b.Count > 0))
			{
				return "Damage distribution: no results yet";
			}
			string parts = string.Join("; ", Bins.Where(b => b.Count > 0)
				.Select(b => $"{b.From.ToString("0", Us)} to {b.To.ToString("0", Us)} percent, {b.Count.ToString("N0", Us)}"));
			int over = Bins.Where(b => b.From >= Threshold).Sum(b => b.Count);
			return $"Damage distribution: {parts}. {over.ToString("N0", Us)} at or above the {Threshold.ToString("0.#", Us)} percent line.";
		}
	}

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property == BinsProperty || change.Property == ThresholdProperty)
		{
			UpdateSpokenName();
		}
	}

	public override void Render(DrawingContext context)
	{
		IReadOnlyList<HistogramBin> bins = Bins;
		if (bins == null || bins.Count == 0 || Bounds.Width < 40 || Bounds.Height < 40)
		{
			return;
		}
		IBrush quiet = Brush("TextTertiary", Brushes.Gray);
		IBrush ink = Brush("TextSecondary", Brushes.Gray);
		double labelHeight = Caption("0", quiet).Height;
		(int step, int top) = NiceScale(bins.Max(b => b.Count));
		double left = AxisWidth(top);
		// Room at the top for the line's label and, under it, the tallest bar's count; at the bottom for the percent labels
		double row = labelHeight + LabelGap;
		Rect plot = new Rect(left, 2 * row, Bounds.Width - left - 4, Bounds.Height - 3 * row);
		if (plot.Width < 20 || plot.Height < 20)
		{
			return;
		}
		DrawGrid(context, plot, step, top);

		double slot = plot.Width / bins.Count;
		double barWidth = Math.Max(2, Math.Round(slot * BarShare));
		for (int i = 0; i < bins.Count; i++)
		{
			HistogramBin bin = bins[i];
			double x = Math.Round(plot.Left + slot * i + (slot - barWidth) / 2);
			if (bin.Count > 0)
			{
				double h = Math.Max(2, plot.Height * bin.Count / top);
				Rect bar = new Rect(x, plot.Bottom - h, barWidth, h);
				IBrush fill = Brush(bin.ColourKey, Brushes.SteelBlue);
				// Rounded at the top only: a rounded bar with its lower corners squared off
				double r = Math.Min(BarRadius, Math.Min(barWidth / 2, h / 2));
				context.DrawRectangle(fill, null, new RoundedRect(bar, r));
				context.DrawRectangle(fill, null, new Rect(bar.X, bar.Bottom - r, bar.Width, r));
				FormattedText count = Caption(bin.Count.ToString("N0", Us), ink, FontWeight.SemiBold);
				context.DrawText(count, new Point(x + barWidth / 2 - count.Width / 2, bar.Y - LabelGap / 2 - count.Height));
			}
		}

		// The baseline, and the percent at every band edge under it
		context.DrawRectangle(Brush("BorderStrong", Brushes.Gray), null, new Rect(plot.Left, Math.Round(plot.Bottom), plot.Width, 1));
		for (int i = 0; i <= bins.Count; i++)
		{
			double edge = i == bins.Count ? bins[^1].To : bins[i].From;
			FormattedText label = Caption(edge.ToString("0", Us) + (i == bins.Count ? "%" : ""), quiet);
			double x = plot.Left + slot * i - label.Width / 2;
			x = Math.Clamp(x, plot.Left - label.Width / 2, Bounds.Width - label.Width);
			context.DrawText(label, new Point(x, plot.Bottom + LabelGap / 2 + 1));
		}

		// The substantial damage line, through the bars, with its value at the top
		double span = bins[^1].To - bins[0].From;
		double lineX = Math.Round(plot.Left + plot.Width * (Threshold - bins[0].From) / span - LineWidth / 2);
		IBrush red = Brush("GaugeMark", Brushes.Red);
		context.DrawRectangle(red, null, new RoundedRect(new Rect(lineX, plot.Top - LabelGap / 2, LineWidth, plot.Bottom - plot.Top + LabelGap / 2), LineWidth / 2));
		FormattedText mark = Caption(Threshold.ToString("0.#", Us) + "% line", Brush("DangerFg", Brushes.Red), FontWeight.SemiBold);
		context.DrawText(mark, new Point(Math.Min(lineX + LabelGap, Bounds.Width - mark.Width), 0));
	}
}
