using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Sde.App.Services;

/// <summary>
/// Keyboard focus in the main window, for people who work without a pointer (Full Keyboard Access, VoiceOver):
/// <list type="bullet">
/// <item>Space in a table checks the selected row.</item>
/// <item>Tab always moves on. Avalonia's own Tab handling gives up when the next stop it picks can't take focus (a
/// disabled button, the remembered item of a hidden segmented switch); then the next stop that can is focused instead.</item>
/// <item>A sheet takes focus while it shows: the cursor goes to its first text field (or to the sheet itself), Tab
/// stays inside it, and when it closes focus goes back where it was. The ⌘K palette does the same.</item>
/// </list>
/// </summary>
public static class KeyboardFocus
{
	/// <summary>The most stops Tab skips over looking for one that can take focus.</summary>
	private const int MaxSkipped = 400;

	/// <summary>
	/// Sets up both for the main window: <paramref name="sheetLayer"/> is the scrim that shows while a sheet is open,
	/// <paramref name="sheetCard"/> the sheet inside it, and <paramref name="paletteLayer"/> the ⌘K palette's layer.
	/// </summary>
	public static void Attach(Window window, string sheetLayer, string sheetCard, string paletteLayer)
	{
		List<(Control Shown, Control Layer)> traps = new();
		window.AddHandler(InputElement.KeyDownEvent, (_, e) => OnTabInsideALayer(window, traps, e), RoutingStrategies.Tunnel);
		window.AddHandler(InputElement.KeyDownEvent, OnTabKey, RoutingStrategies.Bubble);
		window.AddHandler(InputElement.KeyDownEvent, OnSpaceKey, RoutingStrategies.Bubble);
		if (window.FindControl<Control>(sheetLayer) is Control scrim && window.FindControl<Control>(sheetCard) is Control card)
		{
			TrapFocus(window, scrim, card, takeFocus: true);
			traps.Add((scrim, card));
		}
		if (window.FindControl<Control>(paletteLayer) is Control palette)
		{
			// The palette puts the cursor in its own search box (CommandPaletteView); here it only gives focus back.
			TrapFocus(window, palette, palette, takeFocus: false);
			traps.Add((palette, palette));
		}
	}

	/// <summary>
	/// Tab while a sheet or the palette shows: the next stop inside it, round from its last stop to its first (and the
	/// other way for Shift-Tab), so the keyboard never wanders into the dimmed window behind. The topmost layer wins.
	/// </summary>
	private static void OnTabInsideALayer(Window window, List<(Control Shown, Control Layer)> traps, KeyEventArgs e)
	{
		if (e.Handled || e.Key != Key.Tab || (e.KeyModifiers & ~KeyModifiers.Shift) != KeyModifiers.None || window.FocusManager is not FocusManager focus)
		{
			return;
		}
		(_, Control layer) = traps.LastOrDefault(t => t.Shown.IsVisible && t.Layer.IsEffectivelyVisible);
		if (layer == null)
		{
			return;
		}
		NavigationDirection direction = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? NavigationDirection.Previous : NavigationDirection.Next;
		IInputElement current = focus.GetFocusedElement();
		IInputElement start = current is Visual v && IsInside(v, layer) ? current : null;
		for (int i = 0; i < MaxSkipped; i++)
		{
			IInputElement next = start == null ? null : focus.FindNextElement(direction, new FindNextElementOptions { FocusedElement = start as InputElement });
			if (next is not Visual nv || !IsInside(nv, layer) || ReferenceEquals(next, start))
			{
				// Past the layer's last stop (or its first, going back): round to the other end
				next = direction == NavigationDirection.Next ? FocusManager.FindFirstFocusableElement(layer) : FocusManager.FindLastFocusableElement(layer);
				if (next == null || ReferenceEquals(next, start) && i > 0)
				{
					break;
				}
			}
			if (CanTakeFocus(next) && next is InputElement stop && KeyboardNavigation.GetIsTabStop(stop) && next.Focus(NavigationMethod.Tab, e.KeyModifiers))
			{
				e.Handled = true;
				return;
			}
			start = next;
		}
		e.Handled = true;                                   // nothing else to reach: stay where it is, inside the layer
	}

	/// <summary>
	/// Space in a table checks or unchecks the selected row (its CheckBox.rowCheck), so a table is one Tab stop and
	/// its rows' check boxes are still in reach of the keyboard.
	/// </summary>
	private static void OnSpaceKey(object sender, KeyEventArgs e)
	{
		if (e.Handled || e.Key != Key.Space || e.KeyModifiers != KeyModifiers.None || e.Source is not Control source || source is TextBox or ToggleButton or Button)
		{
			return;
		}
		DataGrid grid = source as DataGrid ?? source.FindAncestorOfType<DataGrid>();
		if (grid?.SelectedItem is not object item)
		{
			return;
		}
		CheckBox box = grid.GetVisualDescendants().OfType<CheckBox>()
			.FirstOrDefault(c => c.Classes.Contains("rowCheck") && ReferenceEquals(c.DataContext, item) && c.IsEffectivelyEnabled);
		if (box != null)
		{
			box.IsChecked = box.IsChecked != true;
			e.Handled = true;
		}
	}

	/// <summary>Tab or Shift-Tab that nothing else handled: focus the next stop that can take it.</summary>
	private static void OnTabKey(object sender, KeyEventArgs e)
	{
		if (e.Handled || e.Key != Key.Tab || (e.KeyModifiers & ~KeyModifiers.Shift) != KeyModifiers.None || sender is not TopLevel top)
		{
			return;
		}
		NavigationDirection direction = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? NavigationDirection.Previous : NavigationDirection.Next;
		e.Handled = MoveFocus(top, direction, e.KeyModifiers);
	}

	/// <summary>Moves focus to the next (or previous) tab stop that can take it. False when there is none.</summary>
	public static bool MoveFocus(TopLevel top, NavigationDirection direction, KeyModifiers modifiers = KeyModifiers.None)
	{
		if (top.FocusManager is not FocusManager focus || focus.GetFocusedElement() is not IInputElement current)
		{
			return false;
		}
		IInputElement start = current;
		for (int i = 0; i < MaxSkipped; i++)
		{
			IInputElement next = focus.FindNextElement(direction, new FindNextElementOptions { FocusedElement = start as InputElement });
			if (next == null || ReferenceEquals(next, current) || ReferenceEquals(next, start))
			{
				return false;
			}
			if (CanTakeFocus(next) && next.Focus(NavigationMethod.Tab, modifiers))
			{
				return true;
			}
			start = next;
		}
		return false;
	}

	/// <summary>Visible, enabled and focusable: a stop Tab can land on.</summary>
	public static bool CanTakeFocus(IInputElement element) =>
		element is InputElement e && e.Focusable && e.IsEffectivelyEnabled && e.IsEffectivelyVisible && e.IsAttachedToVisualTree();

	/// <summary>
	/// While <paramref name="shown"/> shows, Tab cycles inside <paramref name="layer"/>, which can take focus as it opens
	/// (its first text field, else the layer itself, so Tab starts inside it and Return still reaches the default
	/// button); when it hides, focus goes back to where it was.
	/// </summary>
	private static void TrapFocus(Window window, Control shown, Control layer, bool takeFocus)
	{
		KeyboardNavigation.SetTabNavigation(layer, KeyboardNavigationMode.Cycle);
		IInputElement before = null;
		bool showing = false;
		void Opened()
		{
			if (!showing)
			{
				IInputElement focused = window.FocusManager?.GetFocusedElement();
				before = focused is Visual v && !IsInside(v, layer) ? focused : null;
				showing = true;
			}
			if (takeFocus)
			{
				// After layout, so the sheet's controls are there to be found
				Dispatcher.UIThread.Post(() => FocusFirst(layer), DispatcherPriority.Background);
			}
		}
		void Closed()
		{
			if (!showing)
			{
				return;
			}
			showing = false;
			IInputElement focused = window.FocusManager?.GetFocusedElement();
			bool focusLost = focused == null || focused is Visual v && (IsInside(v, layer) || !v.IsEffectivelyVisible);
			if (before != null && focusLost && CanTakeFocus(before))
			{
				before.Focus(NavigationMethod.Unspecified);
			}
			before = null;
		}
		shown.PropertyChanged += (_, e) =>
		{
			if (e.Property == Visual.IsVisibleProperty)
			{
				if (shown.IsVisible)
				{
					Opened();
				}
				else
				{
					Closed();
				}
			}
		};
		if (takeFocus && layer is Decorator { Child: Panel panel })
		{
			// A new sheet in place of the last one (one closes and the next opens): focus moves into the new one.
			foreach (ContentControl content in panel.Children.OfType<ContentControl>())
			{
				content.PropertyChanged += (_, e) =>
				{
					if (e.Property == ContentControl.ContentProperty && shown.IsVisible && e.NewValue != null)
					{
						Opened();
					}
				};
			}
		}
	}

	/// <summary>Focuses a layer's first single-line text field, or the layer itself when it has none.</summary>
	private static void FocusFirst(Control layer)
	{
		if (!layer.IsEffectivelyVisible)
		{
			return;
		}
		IInputElement focused = TopLevel.GetTopLevel(layer)?.FocusManager?.GetFocusedElement();
		if (focused is Visual already && already != layer && IsInside(already, layer))
		{
			return;                                     // the sheet put the cursor somewhere itself
		}
		TextBox field = layer.GetVisualDescendants().OfType<TextBox>()
			.FirstOrDefault(t => !t.AcceptsReturn && !t.IsReadOnly && CanTakeFocus(t) && KeyboardNavigation.GetIsTabStop(t));
		if (field != null)
		{
			field.Focus(NavigationMethod.Unspecified);
			return;
		}
		// Not a tab stop itself: Tab from it goes to the first control inside
		KeyboardNavigation.SetIsTabStop(layer, false);
		layer.Focusable = true;
		layer.Focus(NavigationMethod.Unspecified);
	}

	private static bool IsInside(Visual visual, Visual layer) => visual == layer || visual.GetVisualAncestors().Contains(layer);
}
