using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;

namespace Sde.App.Controls;

/// <summary>
/// The thin strip on a sidebar's or an inspector's edge that drags it wider or narrower (Styles/Shell.axaml draws it:
/// ResizeHandleWidth wide, invisible, with the left-right resize cursor). <see cref="Value"/> is bound two-way to the
/// width in a view model, which keeps it between its limits; a double-click runs <see cref="ResetCommand"/> (back to
/// the default width). Unlike a GridSplitter it never takes over the column's width, so hiding and showing the pane
/// and the remembered width keep working.
/// </summary>
public sealed class ResizeHandle : Thumb
{
	/// <summary>The width being dragged, in pixels.</summary>
	public static readonly StyledProperty<double> ValueProperty =
		AvaloniaProperty.Register<ResizeHandle, double>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

	/// <summary>True for a pane on the left (the sidebar: dragging right widens it); false for one on the right (an inspector).</summary>
	public static readonly StyledProperty<bool> GrowsRightProperty = AvaloniaProperty.Register<ResizeHandle, bool>(nameof(GrowsRight), true);

	/// <summary>Run on a double-click: puts the width back to its default.</summary>
	public static readonly StyledProperty<ICommand> ResetCommandProperty = AvaloniaProperty.Register<ResizeHandle, ICommand>(nameof(ResetCommand));

	public double Value
	{
		get => GetValue(ValueProperty);
		set => SetValue(ValueProperty, value);
	}

	public bool GrowsRight
	{
		get => GetValue(GrowsRightProperty);
		set => SetValue(GrowsRightProperty, value);
	}

	public ICommand ResetCommand
	{
		get => GetValue(ResetCommandProperty);
		set => SetValue(ResetCommandProperty, value);
	}

	/// <summary>
	/// The drag moves the edge by the pointer's movement. The handle sits on the edge it moves, so each step is measured
	/// from where the edge now is.
	/// </summary>
	protected override void OnDragDelta(VectorEventArgs e)
	{
		base.OnDragDelta(e);
		Drag(e.Vector.X);
	}

	/// <summary>Moves the edge by <paramref name="dx"/> pixels (positive is to the right).</summary>
	public void Drag(double dx)
	{
		if (dx != 0)
		{
			Value += GrowsRight ? dx : -dx;
		}
	}

	protected override void OnPointerPressed(PointerPressedEventArgs e)
	{
		if (e.ClickCount == 2 && ResetCommand?.CanExecute(null) == true)
		{
			ResetCommand.Execute(null);
			e.Handled = true;
			return;
		}
		base.OnPointerPressed(e);
	}
}
