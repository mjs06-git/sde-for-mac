using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;

namespace Sde.App.ViewModels;

/// <summary>
/// The window's resizable, remembered layout: the sidebar's width and whether it shows, the inspector's width (one width
/// shared by Structures' inspector and the editor's result panel, so the pages agree) and whether Structures shows it,
/// and the tables' column widths. Kept in the app settings (<see cref="LayoutState"/>), saved a moment after a change and
/// when the window closes.
/// </summary>
public sealed partial class MainViewModel
{
	/// <summary>How long after the last change the layout is saved (a drag makes many changes).</summary>
	private static readonly TimeSpan LayoutSaveDelay = TimeSpan.FromMilliseconds(600);

	private double _sidebarWidth = DefaultSidebarWidth;
	private double _inspectorWidth = DefaultInspectorWidth;
	private Dictionary<string, Dictionary<string, string>> _columnWidths = new();
	private DispatcherTimer _layoutTimer;
	private bool _restoringLayout;

	/// <summary>A size token from Styles/Tokens.axaml, or <paramref name="fallback"/> before the app's resources load.</summary>
	private static double Token(string key, double fallback) =>
		Application.Current != null && Application.Current.TryFindResource(key, out object value) && value is double d ? d : fallback;

	public static double DefaultSidebarWidth => Token("SidebarWidth", 232);

	public static double MinSidebarWidth => Token("SidebarMinWidth", 184);

	public static double MaxSidebarWidth => Token("SidebarMaxWidth", 380);

	public static double DefaultInspectorWidth => Token("InspectorWidth", 288);

	public static double MinInspectorWidth => Token("InspectorMinWidth", 240);

	public static double MaxInspectorWidth => Token("InspectorMaxWidth", 460);

	private static double Clamp(double value, double fallback, double min, double max) =>
		double.IsNaN(value) || double.IsInfinity(value) || value <= 0 ? fallback : Math.Clamp(value, min, max);

	/// <summary>The sidebar's width, kept between the SidebarMinWidth and SidebarMaxWidth tokens (its edge drags it).</summary>
	public double SidebarWidth
	{
		get => _sidebarWidth;
		set
		{
			if (SetProperty(ref _sidebarWidth, Clamp(value, DefaultSidebarWidth, MinSidebarWidth, MaxSidebarWidth)))
			{
				OnPropertyChanged(nameof(SidebarInset));
				LayoutChanged();
			}
		}
	}

	/// <summary>How far the page starts from the window's left edge: the sidebar's width, or 0 while it is hidden (toasts centre on the page).</summary>
	public double SidebarInset => IsSidebarVisible ? SidebarWidth : 0;

	/// <summary>The inspector's width (Structures' inspector and the editor's result panel), between the InspectorMinWidth and InspectorMaxWidth tokens.</summary>
	public double InspectorWidth
	{
		get => _inspectorWidth;
		set
		{
			if (SetProperty(ref _inspectorWidth, Clamp(value, DefaultInspectorWidth, MinInspectorWidth, MaxInspectorWidth)))
			{
				LayoutChanged();
			}
		}
	}

	/// <summary>A double-click on the sidebar's edge: back to its default width.</summary>
	[RelayCommand]
	private void ResetSidebarWidth() => SidebarWidth = DefaultSidebarWidth;

	/// <summary>A double-click on an inspector's edge: back to its default width.</summary>
	[RelayCommand]
	private void ResetInspectorWidth() => InspectorWidth = DefaultInspectorWidth;

	/// <summary>A table's remembered column widths (column key to DataGridLength text), or an empty list.</summary>
	public IReadOnlyDictionary<string, string> ColumnWidths(string table) =>
		table != null && _columnWidths.TryGetValue(table, out Dictionary<string, string> widths) ? widths : new Dictionary<string, string>();

	/// <summary>Remembers a table's column widths (only the columns the user changed; none forgets them).</summary>
	public void SetColumnWidths(string table, IReadOnlyDictionary<string, string> widths)
	{
		if (string.IsNullOrEmpty(table))
		{
			return;
		}
		Dictionary<string, string> copy = widths == null ? new Dictionary<string, string>() : new Dictionary<string, string>(widths);
		if (copy.Count == 0 && !_columnWidths.ContainsKey(table))
		{
			return;
		}
		if (_columnWidths.TryGetValue(table, out Dictionary<string, string> old) && old.Count == copy.Count && !old.Except(copy).Any())
		{
			return;
		}
		if (copy.Count == 0)
		{
			_columnWidths.Remove(table);
		}
		else
		{
			_columnWidths[table] = copy;
		}
		LayoutChanged();
	}

	/// <summary>The layout as it is now, for the app settings.</summary>
	public LayoutState CaptureLayout() => new LayoutState
	{
		SidebarVisible = IsSidebarVisible,
		SidebarWidth = SidebarWidth,
		InspectorVisible = Structures.IsInspectorVisible,
		InspectorWidth = InspectorWidth,
		ColumnWidths = _columnWidths.ToDictionary(t => t.Key, t => new Dictionary<string, string>(t.Value)),
	};

	/// <summary>Puts back a remembered layout (widths kept within their limits); null keeps the defaults.</summary>
	public void RestoreLayout(LayoutState state)
	{
		if (state == null)
		{
			return;
		}
		_restoringLayout = true;
		try
		{
			IsSidebarVisible = state.SidebarVisible;
			SidebarWidth = state.SidebarWidth;
			InspectorWidth = state.InspectorWidth;
			Structures.IsInspectorVisible = state.InspectorVisible;
			_columnWidths = state.ColumnWidths?.Where(t => t.Key != null && t.Value != null)
				.ToDictionary(t => t.Key, t => new Dictionary<string, string>(t.Value)) ?? new Dictionary<string, Dictionary<string, string>>();
		}
		finally
		{
			_restoringLayout = false;
		}
	}

	/// <summary>Something in the layout changed: it is saved shortly, once the drag (or typing) settles.</summary>
	private void LayoutChanged()
	{
		if (_restoringLayout)
		{
			return;
		}
		if (_layoutTimer == null)
		{
			_layoutTimer = new DispatcherTimer { Interval = LayoutSaveDelay };
			_layoutTimer.Tick += (_, _) => SaveLayout();
		}
		_layoutTimer.Stop();
		_layoutTimer.Start();
	}

	/// <summary>Writes the layout to the app settings now.</summary>
	public void SaveLayout()
	{
		_layoutTimer?.Stop();
		try
		{
			_app.Settings.Layout = CaptureLayout();
			_app.Settings.Save();
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			AppLog.Write("Couldn't remember the window's layout", ex);
		}
	}

	/// <summary>The window closed: a save still waiting is dropped (closing saves the layout with the rest of the settings).</summary>
	public void CancelLayoutSave() => _layoutTimer?.Stop();
}
