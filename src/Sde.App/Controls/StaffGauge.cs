using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Sde.App.Controls;

/// <summary>
/// The app's signature: a flood staff gauge laid on its side. A rounded staff graduated every 10 percent, water filling
/// it from 0 to <see cref="Value"/>, and the red high-water mark at <see cref="Threshold"/> (FEMA's 50 percent line)
/// standing a little above and below the staff. The water is blue below the line, amber within <see cref="NearBand"/>
/// points of it and red at or above it. Every colour comes from the theme (the Gauge* tokens), so it redraws when the
/// theme changes. <see cref="IsMini"/> draws the small label-less version for a table cell; <see cref="IsEmpty"/> draws
/// the dry staff only, for a structure with no result yet. A screen reader hears the value and where it stands
/// against the line, for example "75 percent damaged, at or above the 50 percent line".
/// </summary>
public sealed class StaffGauge : Control
{
	/// <summary>The percent damaged. Drawn from 0 to 100; a value over 100 fills the staff and is still read out as it is.</summary>
	public static readonly StyledProperty<double> ValueProperty =
		AvaloniaProperty.Register<StaffGauge, double>(nameof(Value));

	/// <summary>Where the high-water mark stands, in percent (the substantial damage line).</summary>
	public static readonly StyledProperty<double> ThresholdProperty =
		AvaloniaProperty.Register<StaffGauge, double>(nameof(Threshold), 50);

	/// <summary>How many points below the line the water turns amber (FEMA's "near threshold" band).</summary>
	public static readonly StyledProperty<double> NearBandProperty =
		AvaloniaProperty.Register<StaffGauge, double>(nameof(NearBand), 10);

	/// <summary>Whether the 0%, line and 100% captions are written under the staff.</summary>
	public static readonly StyledProperty<bool> ShowLabelsProperty =
		AvaloniaProperty.Register<StaffGauge, bool>(nameof(ShowLabels));

	/// <summary>The small version for a table cell: a thin staff and the mark, no graduations or captions.</summary>
	public static readonly StyledProperty<bool> IsMiniProperty =
		AvaloniaProperty.Register<StaffGauge, bool>(nameof(IsMini));

	/// <summary>True when there is no result to show: the staff is drawn dry and the value is not read out.</summary>
	public static readonly StyledProperty<bool> IsEmptyProperty =
		AvaloniaProperty.Register<StaffGauge, bool>(nameof(IsEmpty));

	/// <summary>Whether the high-water mark is drawn. Off for a share that is not measured against the line (the Summary's headline).</summary>
	public static readonly StyledProperty<bool> ShowMarkProperty =
		AvaloniaProperty.Register<StaffGauge, bool>(nameof(ShowMark), true);

	/// <summary>A fixed colour for the water (a resource key such as "GaugeDanger"), in place of the colour by result.</summary>
	public static readonly StyledProperty<string> WaterKeyProperty =
		AvaloniaProperty.Register<StaffGauge, string>(nameof(WaterKey));

	/// <summary>
	/// True on the dark result card: every colour comes from the gauge's "OnDark" tokens (a lighter staff, brighter water
	/// and red) and the captions from the hero's text colours, so the gauge reads on navy in both themes.
	/// </summary>
	public static readonly StyledProperty<bool> OnDarkProperty =
		AvaloniaProperty.Register<StaffGauge, bool>(nameof(OnDark));

	/// <summary>How thick the staff is drawn, in pixels (8 by default; the result card and the Summary draw it thicker).</summary>
	public static readonly StyledProperty<double> TrackThicknessProperty =
		AvaloniaProperty.Register<StaffGauge, double>(nameof(TrackThickness), 8);

	// The drawing's own geometry (not design tokens: it is the shape of the control, like an icon's path).
	private const double MiniTrackHeight = 4;
	private const double Overshoot = 4;
	private const double TickGap = 2;
	private const double MinorTick = 3;
	private const double MajorTick = 6;
	private const double LabelGap = 3;
	private const double MarkWidth = 2;
	private const double MiniHeight = 8;

	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	/// <summary>The name this control last gave itself, so a name set in a view is never overwritten.</summary>
	private string _spokenName;

	static StaffGauge()
	{
		AffectsRender<StaffGauge>(ValueProperty, ThresholdProperty, NearBandProperty, ShowLabelsProperty, IsMiniProperty, IsEmptyProperty, ShowMarkProperty, WaterKeyProperty,
			OnDarkProperty, TrackThicknessProperty);
		AffectsMeasure<StaffGauge>(ShowLabelsProperty, IsMiniProperty, TrackThicknessProperty);
	}

	public StaffGauge()
	{
		ActualThemeVariantChanged += (_, _) => InvalidateVisual();
		UpdateSpokenName();
	}

	/// <inheritdoc cref="ValueProperty"/>
	public double Value
	{
		get => GetValue(ValueProperty);
		set => SetValue(ValueProperty, value);
	}

	/// <inheritdoc cref="ThresholdProperty"/>
	public double Threshold
	{
		get => GetValue(ThresholdProperty);
		set => SetValue(ThresholdProperty, value);
	}

	/// <inheritdoc cref="NearBandProperty"/>
	public double NearBand
	{
		get => GetValue(NearBandProperty);
		set => SetValue(NearBandProperty, value);
	}

	/// <inheritdoc cref="ShowLabelsProperty"/>
	public bool ShowLabels
	{
		get => GetValue(ShowLabelsProperty);
		set => SetValue(ShowLabelsProperty, value);
	}

	/// <inheritdoc cref="IsMiniProperty"/>
	public bool IsMini
	{
		get => GetValue(IsMiniProperty);
		set => SetValue(IsMiniProperty, value);
	}

	/// <inheritdoc cref="IsEmptyProperty"/>
	public bool IsEmpty
	{
		get => GetValue(IsEmptyProperty);
		set => SetValue(IsEmptyProperty, value);
	}

	/// <inheritdoc cref="ShowMarkProperty"/>
	public bool ShowMark
	{
		get => GetValue(ShowMarkProperty);
		set => SetValue(ShowMarkProperty, value);
	}

	/// <inheritdoc cref="WaterKeyProperty"/>
	public string WaterKey
	{
		get => GetValue(WaterKeyProperty);
		set => SetValue(WaterKeyProperty, value);
	}

	/// <inheritdoc cref="OnDarkProperty"/>
	public bool OnDark
	{
		get => GetValue(OnDarkProperty);
		set => SetValue(OnDarkProperty, value);
	}

	/// <inheritdoc cref="TrackThicknessProperty"/>
	public double TrackThickness
	{
		get => GetValue(TrackThicknessProperty);
		set => SetValue(TrackThicknessProperty, value);
	}

	/// <summary>The resource key of the water's colour now: blue below the line, amber near it, red at or above it.</summary>
	public string FillKey =>
		!string.IsNullOrEmpty(WaterKey) ? WaterKey
		: Value >= Threshold ? "GaugeDanger"
		: Value >= Threshold - NearBand ? "GaugeNear"
		: "GaugeWater";

	/// <summary>
	/// The resource key the water is painted with: <see cref="FillKey"/>, or its "OnDark" version on the dark result card
	/// (for example "GaugeDangerOnDark"). <see cref="FillKey"/> keeps the meaning; this is the colour.
	/// </summary>
	public string PaintKey => Key(FillKey);

	/// <summary>The water's brush in the theme now showing (what <see cref="Render"/> paints with).</summary>
	public IBrush FillBrush => Brush(PaintKey, Brushes.SteelBlue);

	/// <summary>A gauge token's key, with "OnDark" added when the gauge is drawn on the dark result card.</summary>
	private string Key(string key) => OnDark ? key + "OnDark" : key;

	/// <summary>What a screen reader hears, for example "45 percent damaged, within 10 points of the 50 percent line".</summary>
	public string SpokenText
	{
		get
		{
			if (IsEmpty)
			{
				return "No result yet";
			}
			string value = Value.ToString("0.#", Us);
			string line = Threshold.ToString("0.#", Us);
			if (!ShowMark)
			{
				return $"{value} percent";
			}
			if (Value >= Threshold)
			{
				return $"{value} percent damaged, at or above the {line} percent line";
			}
			if (Value >= Threshold - NearBand)
			{
				return $"{value} percent damaged, within {NearBand.ToString("0.#", Us)} points of the {line} percent line";
			}
			return $"{value} percent damaged, below the {line} percent line";
		}
	}

	protected override AutomationPeer OnCreateAutomationPeer() => new StaffGaugeAutomationPeer(this);

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property == ValueProperty || change.Property == ThresholdProperty || change.Property == NearBandProperty
			|| change.Property == IsEmptyProperty || change.Property == ShowMarkProperty)
		{
			UpdateSpokenName();
		}
	}

	/// <summary>Names the control for screen readers, unless a view gave it a name of its own.</summary>
	private void UpdateSpokenName()
	{
		string current = AutomationProperties.GetName(this);
		if (!string.IsNullOrEmpty(current) && current != _spokenName)
		{
			return;
		}
		_spokenName = SpokenText;
		AutomationProperties.SetName(this, _spokenName);
	}

	protected override Size MeasureOverride(Size availableSize)
	{
		double width = double.IsInfinity(availableSize.Width) ? (IsMini ? 56 : 200) : availableSize.Width;
		if (IsMini)
		{
			return new Size(Math.Min(width, 56), MiniHeight);
		}
		double height = Overshoot + TrackThickness + TickGap + MajorTick;
		if (ShowLabels)
		{
			height += LabelGap + LabelHeight();
		}
		return new Size(width, Math.Ceiling(height));
	}

	public override void Render(DrawingContext context)
	{
		double width = Bounds.Width;
		if (width < 4)
		{
			return;
		}
		if (IsMini)
		{
			RenderMini(context, width);
			return;
		}

		// The staff, inset by half a mark so the end graduations and the mark at 0 or 100 aren't cut off
		double inset = MarkWidth / 2;
		double left = inset;
		double right = width - inset;
		double span = right - left;
		double thickness = TrackThickness;
		Rect track = new Rect(left, Overshoot, span, thickness);
		RoundedRect staff = new RoundedRect(track, thickness / 2);
		context.DrawRectangle(Brush(Key("GaugeTrack"), Brushes.LightGray), null, staff);
		double lineX = left + span * Math.Clamp(Threshold, 0, 100) / 100;

		if (!IsEmpty)
		{
			double level = left + span * Math.Clamp(Value, 0, 100) / 100;
			using (context.PushClip(staff))
			{
				context.DrawRectangle(FillBrush, null, new Rect(left, track.Y, level - left, thickness));
			}
		}

		// Graduations every 10 percent under the staff; taller at 0, 50 and 100
		IBrush tick = Brush(Key("GaugeTick"), Brushes.Gray);
		double tickTop = track.Bottom + TickGap;
		for (int i = 0; i <= 10; i++)
		{
			double x = Math.Round(left + span * i / 10.0 - 0.5) + 0.5;
			bool major = i is 0 or 5 or 10;
			context.DrawRectangle(tick, null, new Rect(x - 0.5, tickTop, 1, major ? MajorTick : MinorTick));
		}

		if (ShowMark)
		{
			// The high-water mark: from above the staff down through the graduations, with a thin gap where it crosses the water
			double markX = Math.Round(lineX - MarkWidth / 2);
			context.DrawRectangle(Brush(Key("GaugeMarkHalo"), Brushes.White), null, new Rect(markX - 1, track.Y, MarkWidth + 2, thickness));
			context.DrawRectangle(Brush(Key("GaugeMark"), Brushes.Red), null, new RoundedRect(new Rect(markX, 0, MarkWidth, tickTop + MajorTick), MarkWidth / 2));
		}

		if (ShowLabels)
		{
			double top = tickTop + MajorTick + LabelGap;
			IBrush quiet = Brush(OnDark ? "TextOnHeroSecondary" : "TextTertiary", Brushes.Gray);
			FormattedText zero = Label("0%", quiet, FontWeight.Normal);
			FormattedText hundred = Label("100%", quiet, FontWeight.Normal);
			context.DrawText(zero, new Point(0, top));
			context.DrawText(hundred, new Point(width - hundred.Width, top));
			if (ShowMark)
			{
				FormattedText line = Label(Threshold.ToString("0.#", Us) + "%", Brush(OnDark ? "DangerOnFrame" : "DangerFg", Brushes.Red), FontWeight.SemiBold);
				double x = Math.Clamp(lineX - line.Width / 2, zero.Width + 4, width - hundred.Width - 4 - line.Width);
				context.DrawText(line, new Point(x, top));
			}
			else
			{
				FormattedText half = Label("50%", quiet, FontWeight.Normal);
				context.DrawText(half, new Point(left + span / 2 - half.Width / 2, top));
			}
		}
	}

	/// <summary>The table-cell version: a thin staff, the water and the mark standing through it.</summary>
	private void RenderMini(DrawingContext context, double width)
	{
		double inset = MarkWidth / 2;
		double span = width - 2 * inset;
		double y = (Bounds.Height - MiniTrackHeight) / 2;
		Rect track = new Rect(inset, y, span, MiniTrackHeight);
		RoundedRect staff = new RoundedRect(track, MiniTrackHeight / 2);
		context.DrawRectangle(Brush(Key("GaugeTrack"), Brushes.LightGray), null, staff);
		if (!IsEmpty)
		{
			double level = inset + span * Math.Clamp(Value, 0, 100) / 100;
			using (context.PushClip(staff))
			{
				context.DrawRectangle(FillBrush, null, new Rect(inset, y, level - inset, MiniTrackHeight));
			}
		}
		// With no result the cell shows the dry staff alone, so the rows with a reading stand out
		if (ShowMark && !IsEmpty)
		{
			double markX = Math.Round(inset + span * Math.Clamp(Threshold, 0, 100) / 100 - MarkWidth / 2);
			context.DrawRectangle(Brush(Key("GaugeMark"), Brushes.Red), null, new RoundedRect(new Rect(markX, 0, MarkWidth, Bounds.Height), MarkWidth / 2));
		}
	}

	private FormattedText Label(string text, IBrush brush, FontWeight weight) =>
		new FormattedText(text, Us, FlowDirection.LeftToRight, new Typeface(TextElement.GetFontFamily(this), FontStyle.Normal, weight),
			CaptionSize(), brush);

	private double LabelHeight() => Label("0%", Brushes.Black, FontWeight.Normal).Height;

	private double CaptionSize() =>
		this.TryFindResource("FontSizeCaption", out object size) && size is double d ? d : 11;

	private IBrush Brush(string key, IBrush fallback) =>
		this.TryFindResource(key, ActualThemeVariant, out object value) && value is IBrush b ? b : fallback;

	/// <summary>Reads the gauge to assistive technology as one piece of text: its value and where it stands against the line.</summary>
	private sealed class StaffGaugeAutomationPeer(StaffGauge owner) : ControlAutomationPeer(owner)
	{
		protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

		protected override string GetClassNameCore() => nameof(StaffGauge);

		protected override bool IsControlElementCore() => true;
	}
}
