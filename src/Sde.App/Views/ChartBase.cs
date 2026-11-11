using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Sde.App.Views;

/// <summary>
/// What the Summary's charts share (they are drawn with plain shapes, as <see cref="StackedBar"/> is: the app has no
/// chart library). Every colour comes from the theme's resources, so a chart redraws when the theme changes. A screen
/// reader hears the chart as one piece of text that states its data in words (<see cref="SpokenText"/>); the drawing
/// itself has no parts of its own in the accessibility tree.
/// </summary>
public abstract class ChartBase : Control
{
	/// <summary>The space between an axis and its labels, and above a bar's count (the drawing's geometry, not a token).</summary>
	protected const double LabelGap = 6;

	protected static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	/// <summary>The name this chart last gave itself, so a name set in a view is never overwritten.</summary>
	private string _spokenName;

	protected ChartBase()
	{
		ActualThemeVariantChanged += (_, _) => InvalidateVisual();
	}

	/// <summary>The chart's data in words, for a screen reader.</summary>
	public abstract string SpokenText { get; }

	protected override AutomationPeer OnCreateAutomationPeer() => new ChartAutomationPeer(this);

	/// <summary>Names the chart for screen readers from its data, unless a view gave it a name of its own.</summary>
	protected void UpdateSpokenName()
	{
		string current = AutomationProperties.GetName(this);
		if (!string.IsNullOrEmpty(current) && current != _spokenName)
		{
			return;
		}
		_spokenName = SpokenText;
		AutomationProperties.SetName(this, _spokenName);
	}

	/// <summary>The theme's brush for <paramref name="key"/>, in the theme now showing.</summary>
	protected IBrush Brush(string key, IBrush fallback) =>
		this.TryFindResource(key, ActualThemeVariant, out object value) && value is IBrush b ? b : fallback;

	/// <summary>A caption (FontSizeCaption) in the app's typeface.</summary>
	protected FormattedText Caption(string text, IBrush brush, FontWeight weight = FontWeight.Normal) =>
		new FormattedText(text, Us, FlowDirection.LeftToRight, new Typeface(TextElement.GetFontFamily(this), FontStyle.Normal, weight),
			this.TryFindResource("FontSizeCaption", out object size) && size is double d ? d : 11, brush);

	/// <summary>
	/// A round top for the scale: the smallest of 1, 2, 5, 10, 20, 50 ... times a power of ten that leaves at most
	/// <paramref name="lines"/> gridlines above zero and reaches <paramref name="max"/>. Returns the step and the top.
	/// </summary>
	protected static (int Step, int Top) NiceScale(int max, int lines = 4)
	{
		max = Math.Max(1, max);
		int[] multiples = { 1, 2, 5 };
		for (int power = 1; ; power *= 10)
		{
			foreach (int m in multiples)
			{
				int step = m * power;
				int top = (int)Math.Ceiling(max / (double)step) * step;
				if (top / step <= lines)
				{
					return (step, top);
				}
			}
		}
	}

	/// <summary>Light horizontal gridlines with their values at the left, from 0 to <paramref name="top"/>.</summary>
	protected void DrawGrid(DrawingContext context, Rect plot, int step, int top)
	{
		IBrush grid = Brush("ChartGrid", Brushes.LightGray);
		IBrush label = Brush("TextTertiary", Brushes.Gray);
		for (int v = 0; v <= top; v += step)
		{
			double y = Math.Round(plot.Bottom - plot.Height * v / top) + 0.5;
			if (v > 0)
			{
				context.DrawRectangle(grid, null, new Rect(plot.Left, y - 0.5, plot.Width, 1));
			}
			FormattedText text = Caption(v.ToString("N0", Us), label);
			context.DrawText(text, new Point(plot.Left - LabelGap - text.Width, y - text.Height / 2));
		}
	}

	/// <summary>How wide the value labels at the left are, so the plot starts after them.</summary>
	protected double AxisWidth(int top) => Caption(top.ToString("N0", Us), Brushes.Black).Width + LabelGap;

	/// <summary>Reads the chart to assistive technology as one piece of text.</summary>
	private sealed class ChartAutomationPeer(ChartBase owner) : ControlAutomationPeer(owner)
	{
		protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

		protected override string GetClassNameCore() => Owner.GetType().Name;

		protected override bool IsControlElementCore() => true;
	}
}
