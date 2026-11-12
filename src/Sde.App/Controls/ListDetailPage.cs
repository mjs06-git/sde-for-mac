using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.LogicalTree;

namespace Sde.App.Controls;

/// <summary>
/// The layout the Workflow pages share (Review, Compliance, Assignments): an optional filter bar along the top, the
/// page's list (usually a table card) with a status line under it, and an inspector column at the right. The inspector
/// is as wide as the window's shared inspector width (<see cref="InspectorWidth"/>, bound two-way to the main view
/// model's), its leading edge drags that width with a <see cref="ResizeHandle"/>, and it shows while
/// <see cref="IsInspectorVisible"/> is true (bound to the one inspector switch, View then Hide or Show Inspector), so
/// every page with an inspector agrees with Structures. The look is its template in Styles/Workflow.axaml.
/// </summary>
public sealed class ListDetailPage : ContentControl
{
	/// <summary>The strip above the list: filters on the left. Null leaves room above the list instead.</summary>
	public static readonly StyledProperty<object> FilterBarProperty =
		AvaloniaProperty.Register<ListDetailPage, object>(nameof(FilterBar));

	/// <summary>The line under the list (how many are shown, what was done last).</summary>
	public static readonly StyledProperty<object> StatusProperty =
		AvaloniaProperty.Register<ListDetailPage, object>(nameof(Status));

	/// <summary>The inspector column's content (the selected item's details and actions).</summary>
	public static readonly StyledProperty<object> InspectorProperty =
		AvaloniaProperty.Register<ListDetailPage, object>(nameof(Inspector));

	/// <summary>The inspector's width in pixels; bind it to the main view model's InspectorWidth so it is shared and kept.</summary>
	public static readonly StyledProperty<double> InspectorWidthProperty =
		AvaloniaProperty.Register<ListDetailPage, double>(nameof(InspectorWidth), 288, defaultBindingMode: BindingMode.TwoWay);

	/// <summary>Whether the inspector column shows.</summary>
	public static readonly StyledProperty<bool> IsInspectorVisibleProperty =
		AvaloniaProperty.Register<ListDetailPage, bool>(nameof(IsInspectorVisible), true);

	/// <summary>Run on a double-click on the inspector's edge: back to the default width.</summary>
	public static readonly StyledProperty<ICommand> ResetInspectorWidthCommandProperty =
		AvaloniaProperty.Register<ListDetailPage, ICommand>(nameof(ResetInspectorWidthCommand));

	/// <inheritdoc cref="FilterBarProperty"/>
	public object FilterBar
	{
		get => GetValue(FilterBarProperty);
		set => SetValue(FilterBarProperty, value);
	}

	/// <inheritdoc cref="StatusProperty"/>
	public object Status
	{
		get => GetValue(StatusProperty);
		set => SetValue(StatusProperty, value);
	}

	/// <inheritdoc cref="InspectorProperty"/>
	public object Inspector
	{
		get => GetValue(InspectorProperty);
		set => SetValue(InspectorProperty, value);
	}

	/// <inheritdoc cref="InspectorWidthProperty"/>
	public double InspectorWidth
	{
		get => GetValue(InspectorWidthProperty);
		set => SetValue(InspectorWidthProperty, value);
	}

	/// <inheritdoc cref="IsInspectorVisibleProperty"/>
	public bool IsInspectorVisible
	{
		get => GetValue(IsInspectorVisibleProperty);
		set => SetValue(IsInspectorVisibleProperty, value);
	}

	/// <inheritdoc cref="ResetInspectorWidthCommandProperty"/>
	public ICommand ResetInspectorWidthCommand
	{
		get => GetValue(ResetInspectorWidthCommandProperty);
		set => SetValue(ResetInspectorWidthCommandProperty, value);
	}

	/// <summary>
	/// The filter bar, status line and inspector are this control's logical children (as its content is), so their
	/// bindings see the page's view model and their names belong to the page.
	/// </summary>
	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property == FilterBarProperty || change.Property == StatusProperty || change.Property == InspectorProperty)
		{
			if (change.OldValue is ILogical old)
			{
				LogicalChildren.Remove(old);
			}
			if (change.NewValue is ILogical added && added.LogicalParent == null)
			{
				LogicalChildren.Add(added);
			}
		}
	}
}
