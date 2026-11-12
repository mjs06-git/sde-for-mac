using Avalonia;
using Avalonia.Controls;

namespace Sde.App.Controls;

/// <summary>
/// Lays tags out in one line, left to right. When they all fit each has its natural width; when they don't, they share
/// the width so every tag keeps its dot and its name trims with an ellipsis (and when even a dot each won't fit, the
/// ones past the edge are hidden). A narrow Tags column so never cuts a chip in half; the full list is the cell's
/// tooltip.
/// </summary>
public sealed class TagStrip : Panel
{
	public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<TagStrip, double>(nameof(Spacing), 4);

	/// <summary>The gap between tags.</summary>
	public double Spacing
	{
		get => GetValue(SpacingProperty);
		set => SetValue(SpacingProperty, value);
	}

	/// <summary>The narrowest a tag is drawn: its dot, with room either side.</summary>
	private const double SmallestTag = 28;

	/// <summary>A shortened tag narrower than this shows only its dot (a name cut to an ellipsis says nothing).</summary>
	private const double SmallestNamed = 48;

	/// <summary>The width each tag gets, in order (0 = hidden), worked out by the last measure.</summary>
	private readonly List<double> _widths = new();

	protected override Size MeasureOverride(Size availableSize)
	{
		_widths.Clear();
		double height = 0;
		List<double> natural = new();
		foreach (Control child in Children)
		{
			child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
			natural.Add(child.DesiredSize.Width);
			height = Math.Max(height, child.DesiredSize.Height);
		}
		int count = natural.Count;
		double total = natural.Sum() + Spacing * Math.Max(0, count - 1);
		double room = availableSize.Width;
		if (count == 0)
		{
			return new Size(0, 0);
		}
		if (double.IsInfinity(room) || total <= room)
		{
			_widths.AddRange(natural);
		}
		else
		{
			// How many fit at the smallest size, then share the room between those, the narrow ones keeping their width.
			int shown = Math.Max(1, Math.Min(count, (int)((room + Spacing) / (SmallestTag + Spacing))));
			double left = room - Spacing * (shown - 1);
			List<int> order = Enumerable.Range(0, shown).OrderBy(i => natural[i]).ToList();
			double[] widths = new double[count];
			int remaining = shown;
			foreach (int i in order)
			{
				double share = left / remaining;
				double width = Math.Max(Math.Min(natural[i], share), Math.Min(SmallestTag, left));
				widths[i] = width < natural[i] && width < SmallestNamed ? Math.Min(SmallestTag, left) : width;
				left -= widths[i];
				remaining--;
			}
			_widths.AddRange(widths);
		}
		for (int i = 0; i < count; i++)
		{
			Children[i].Measure(new Size(Math.Max(0, _widths[i]), availableSize.Height));
		}
		double used = _widths.Where(w => w > 0).Sum() + Spacing * Math.Max(0, _widths.Count(w => w > 0) - 1);
		return new Size(double.IsInfinity(room) ? used : Math.Min(used, room), height);
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		double x = 0;
		for (int i = 0; i < Children.Count; i++)
		{
			Control child = Children[i];
			double width = i < _widths.Count ? _widths[i] : 0;
			if (width <= 0)
			{
				// Out of sight past the edge (the cell clips it).
				child.Arrange(new Rect(finalSize.Width + 1000, 0, 0, 0));
				continue;
			}
			child.Arrange(new Rect(x, (finalSize.Height - child.DesiredSize.Height) / 2, width, child.DesiredSize.Height));
			x += width + Spacing;
		}
		return finalSize;
	}
}
