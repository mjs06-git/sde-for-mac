using Avalonia;
using Avalonia.Controls;

namespace Sde.App.Views;

/// <summary>
/// A page's toolbar items, declared in the page's own XAML (<c>&lt;v:PageChrome.Toolbar&gt;</c>) and shown in the
/// window's toolbar while the page is open, the way a Mac app puts a view's actions in its toolbar. The items bind to the
/// page's view model (MainWindow sets their DataContext).
/// </summary>
public static class PageChrome
{
	public static readonly AttachedProperty<Control> ToolbarProperty = AvaloniaProperty.RegisterAttached<Control, Control>("Toolbar", typeof(PageChrome));

	public static Control GetToolbar(Control page) => page.GetValue(ToolbarProperty);

	public static void SetToolbar(Control page, Control value) => page.SetValue(ToolbarProperty, value);
}
