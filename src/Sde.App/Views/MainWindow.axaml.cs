using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Styling;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Sde.App.Controls;
using Sde.App.Services;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Shell;
using Sde.App.ViewModels.Sheets;

namespace Sde.App.Views;

/// <summary>
/// The main window; also provides the app's dialogs: file pickers, confirmations (a sheet over the window) and
/// notifications (toasts at the bottom of the window).
/// </summary>
public partial class MainWindow : Window, IDialogs
{
	/// <summary>How many View menu items and ⌘ number keys come from the sidebar (⌘1 to ⌘9).</summary>
	private const int MaxPageShortcuts = 9;

	private readonly List<NativeMenuItem> _pageMenuItems = new();
	private readonly List<KeyBinding> _pageKeys = new();

	public MainWindow()
	{
		InitializeComponent();
		// Styles animate only under this class, so Reduce Motion (and the off-screen tests) get none.
		if (Motion.Enabled)
		{
			Classes.Add(Motion.Class);
		}
		this.FindControl<ItemsControl>("ToastList").ItemsSource = Toasts.Items;
		ContentControl pages = this.FindControl<ContentControl>("PageHost");
		pages.TemplateApplied += (_, _) =>
		{
			if (pages.Presenter is Avalonia.Controls.Presenters.ContentPresenter presenter)
			{
				presenter.PropertyChanged += (_, e) =>
				{
					if (e.Property == Avalonia.Controls.Presenters.ContentPresenter.ChildProperty)
					{
						ShowPageToolbar(pages.Content, presenter.Child);
					}
				};
			}
		};
	}

	/// <summary>Puts the open page's toolbar items (<see cref="PageChrome"/>) in the window's toolbar.</summary>
	private void ShowPageToolbar(object page, Control view)
	{
		ContentControl bar = this.FindControl<ContentControl>("PageToolbar");
		bar.Content = null;
		bar.DataContext = page;
		bar.Content = view is null ? null : PageChrome.GetToolbar(view);
	}

	/// <summary>The toasts shown at the bottom of the window (<see cref="Notify(string, string, bool)"/> adds one).</summary>
	public ToastHost Toasts { get; } = new ToastHost();

	// ───────────── Shell: the sidebar, the palette ─────────────

	/// <summary>How long the sidebar takes to slide in or out (only with motion on).</summary>
	private static readonly TimeSpan SidebarSlide = TimeSpan.FromMilliseconds(180);

	/// <summary>
	/// Shows or hides the sidebar. With motion on, its width slides between nothing and its full width (the host clips
	/// it); otherwise, and in the tests, it simply appears or goes.
	/// </summary>
	private async void ShowSidebar(MainViewModel main, bool animate)
	{
		Panel host = this.FindControl<Panel>("SidebarHost");
		bool visible = main.IsSidebarVisible;
		if (!animate || !Classes.Contains(Motion.Class) || !IsLoaded)
		{
			host.IsVisible = visible;
			return;
		}
		double full = main.SidebarWidth;
		Animation slide = new Animation
		{
			Duration = SidebarSlide,
			Easing = new CubicEaseOut(),
			Children =
			{
				new KeyFrame { Cue = new Cue(0), Setters = { new Setter(WidthProperty, visible ? 0d : full) } },
				new KeyFrame { Cue = new Cue(1), Setters = { new Setter(WidthProperty, visible ? full : 0d) } },
			},
		};
		host.IsVisible = true;
		await slide.RunAsync(host);
		host.IsVisible = main.IsSidebarVisible;
	}

	/// <summary>A click outside the command palette closes it.</summary>
	private void OnPaletteScrimPressed(object sender, PointerPressedEventArgs e) => (DataContext as MainViewModel)?.Palette.Close();

	// ───────────── Edit menu: acts on the text box that has the cursor ─────────────

	private TextBox FocusedTextBox => FocusManager?.GetFocusedElement() as TextBox;

	private void OnUndo(object sender, EventArgs e)
	{
		if (FocusedTextBox is TextBox box && box.CanUndo)
		{
			box.Undo();
		}
		else
		{
			(DataContext as MainViewModel)?.UndoCommand.Execute(null);
		}
	}

	private void OnRedo(object sender, EventArgs e)
	{
		if (FocusedTextBox is TextBox box && box.CanRedo)
		{
			box.Redo();
		}
		else
		{
			(DataContext as MainViewModel)?.RedoCommand.Execute(null);
		}
	}

	private void OnCut(object sender, EventArgs e) => FocusedTextBox?.Cut();

	private void OnCopy(object sender, EventArgs e) => FocusedTextBox?.Copy();

	private void OnPaste(object sender, EventArgs e) => FocusedTextBox?.Paste();

	private void OnSelectAll(object sender, EventArgs e) => FocusedTextBox?.SelectAll();

	// ───────────── Window menu ─────────────

	private void OnMinimize(object sender, EventArgs e) => WindowState = WindowState.Minimized;

	private void OnZoom(object sender, EventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

	// ───────────── File, then Open Recent ─────────────

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		if (DataContext is MainViewModel main)
		{
			AddPageShortcuts(main);
			ShowSidebar(main, animate: false);
			Closed += (_, _) => main.CancelLayoutSave();
			// Others' saves are checked for while the window is active (and at once when it becomes active).
			Activated += (_, _) => main.WindowActivated(true);
			Deactivated += (_, _) => main.WindowActivated(false);
			main.PropertyChanged += (_, args) =>
			{
				if (args.PropertyName == nameof(MainViewModel.RecentDatabases))
				{
					RebuildRecent(main);
				}
				else if (args.PropertyName == nameof(MainViewModel.IsSidebarVisible))
				{
					ShowSidebar(main, animate: true);
				}
			};
			RebuildRecent(main);
		}
	}

	/// <summary>
	/// View › the sidebar's pages, in its order, with ⌘1 to ⌘9 (also as key bindings, for where there is no menu bar), so
	/// the menu and the keys always match the sidebar.
	/// </summary>
	private void AddPageShortcuts(MainViewModel main)
	{
		NativeMenu view = NativeMenu.GetMenu(this)?.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == "View")?.Menu;
		foreach (NativeMenuItem old in _pageMenuItems)
		{
			view?.Items.Remove(old);
		}
		foreach (KeyBinding old in _pageKeys)
		{
			KeyBindings.Remove(old);
		}
		_pageMenuItems.Clear();
		_pageKeys.Clear();
		int at = 0;
		foreach (NavItemViewModel item in main.SidebarItems.Where(i => i.Number is >= 1 and <= MaxPageShortcuts))
		{
			KeyGesture gesture = new KeyGesture((Key)((int)Key.D0 + item.Number), KeyModifiers.Meta);
			NativeMenuItem menuItem = new NativeMenuItem(item.Title) { Gesture = gesture, Command = main.ShowCommand, CommandParameter = item.Page };
			view?.Items.Insert(at++, menuItem);
			_pageMenuItems.Add(menuItem);
			KeyBinding key = new KeyBinding { Gesture = gesture, Command = main.ShowCommand, CommandParameter = item.Page };
			KeyBindings.Add(key);
			_pageKeys.Add(key);
		}
		if (view != null && at > 0)
		{
			NativeMenuItemSeparator separator = new NativeMenuItemSeparator();
			view.Items.Insert(at, separator);
			_pageMenuItems.Add(separator);
		}
	}

	private void RebuildRecent(MainViewModel main)
	{
		NativeMenu menu = NativeMenu.GetMenu(this)?.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == "File")?.Menu?
			.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == "Open Recent")?.Menu;
		if (menu == null)
		{
			return;
		}
		menu.Items.Clear();
		foreach (string path in main.RecentDatabases)
		{
			menu.Items.Add(new NativeMenuItem(Path.GetFileName(path)) { Command = main.Settings.OpenRecentCommand, CommandParameter = path, ToolTip = path });
		}
		if (menu.Items.Count == 0)
		{
			menu.Items.Add(new NativeMenuItem("No Recent Databases") { IsEnabled = false });
		}
	}

	// ───────────── Remembering where the window was ─────────────

	/// <summary>Puts the window where it was last time, if that is still on a screen.</summary>
	public void Restore(WindowPlacement placement)
	{
		if (placement == null || placement.Width < MinWidth || placement.Height < MinHeight)
		{
			return;
		}
		PixelPoint at = new PixelPoint(placement.X, placement.Y);
		bool onScreen = Screens.All.Any(screen => screen.WorkingArea.Contains(at));
		Width = placement.Width;
		Height = placement.Height;
		if (onScreen)
		{
			WindowStartupLocation = WindowStartupLocation.Manual;
			Position = at;
		}
		if (placement.Maximized)
		{
			WindowState = WindowState.Maximized;
		}
	}

	public WindowPlacement Placement() => new WindowPlacement
	{
		X = Position.X,
		Y = Position.Y,
		Width = Bounds.Width,
		Height = Bounds.Height,
		Maximized = WindowState == WindowState.Maximized,
	};

	public async Task<IReadOnlyList<string>> PickFilesAsync(string title, bool multiple, string filterName, params string[] patterns)
	{
		IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
		{
			Title = title,
			AllowMultiple = multiple,
			FileTypeFilter = patterns.Length == 0 ? null : new[] { new FilePickerFileType(filterName) { Patterns = patterns } },
		});
		return files.Select(f => f.TryGetLocalPath()).Where(p => p != null).ToList();
	}

	public async Task<string> PickFolderAsync(string title)
	{
		IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
		return folders.Count == 1 ? folders[0].TryGetLocalPath() : null;
	}

	public async Task<string> SaveFileAsync(string title, string suggestedName, string filterName, string extension)
	{
		IStorageFile file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
		{
			Title = title,
			SuggestedFileName = suggestedName,
			DefaultExtension = extension,
			FileTypeChoices = new[] { new FilePickerFileType(filterName) { Patterns = new[] { "*." + extension } } },
		});
		return file?.TryGetLocalPath();
	}

	/// <summary>Asks a question in a small sheet over the window; true when the user confirms.</summary>
	public async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false)
	{
		if (DataContext is not MainViewModel main)
		{
			return false;
		}
		object answer = await main.Sheets.ShowAsync(new ConfirmSheetViewModel(title, message, confirmText, destructive));
		return answer is true;
	}

	/// <summary>A toast at the bottom of the window: a few seconds for news, longer for an error.</summary>
	public void Notify(string title, string message, bool isError = false) =>
		Toasts.Show(title, message, isError ? Tone.Danger : Tone.Success);

	/// <summary>A toast with a button (such as Undo) that runs <paramref name="action"/>.</summary>
	public void Notify(string title, string message, string actionText, Action action, bool isError = false) =>
		Toasts.Show(title, message, isError ? Tone.Danger : Tone.Success, actionText, action);

	public void Open(string path) => Process.Start(new ProcessStartInfo("open") { ArgumentList = { path }, UseShellExecute = false });

	public void Reveal(string path) => Process.Start(new ProcessStartInfo("open") { ArgumentList = { "-R", path }, UseShellExecute = false });

	public Task CopyTextAsync(string text) => Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
}

/// <summary>Small converters for the window's sheets.</summary>
public static class SheetConverters
{
	public static readonly IValueConverter DetailsToggle = new FuncValueConverter<bool, string>(shown => shown ? "Hide details" : "Show details");

	/// <summary>True when the sheet size equals the parameter ("Small", "Medium" or "Large"), for the sheet chrome's width class.</summary>
	public static readonly IValueConverter IsSize = new FuncValueConverter<SheetSize, string, bool>((size, name) => size.ToString() == name);
}
