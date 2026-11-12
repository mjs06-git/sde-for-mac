using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Sde.App.ViewModels;

namespace Sde.App.Views;

/// <summary>
/// Remembers a table's column widths: give the DataGrid a name with <c>v:TableLayout.Key="Structures"</c> and the widths
/// the user drags are kept in the window's layout (<see cref="MainViewModel.SetColumnWidths"/>) and put back each time the
/// table is shown. Only columns whose width differs from the one in the view are kept, so a later change to the view's
/// defaults still reaches everyone else. Columns are known by their sort key, or their header text. Which columns show
/// and the sort are a separate setting (the list's own remembered view) and are left alone.
/// </summary>
public static class TableLayout
{
	public static readonly AttachedProperty<string> KeyProperty = AvaloniaProperty.RegisterAttached<DataGrid, string>("Key", typeof(TableLayout));

	/// <summary>The widths written in the view, before anything was restored.</summary>
	private static readonly AttachedProperty<Dictionary<DataGridColumn, DataGridLength>> DefaultsProperty =
		AvaloniaProperty.RegisterAttached<DataGrid, Dictionary<DataGridColumn, DataGridLength>>("Defaults", typeof(TableLayout));

	static TableLayout()
	{
		KeyProperty.Changed.AddClassHandler<DataGrid>((grid, e) =>
		{
			grid.AttachedToVisualTree -= OnAttached;
			grid.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleased);
			if (e.NewValue is string)
			{
				grid.AttachedToVisualTree += OnAttached;
				grid.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
			}
		});
	}

	public static string GetKey(DataGrid grid) => grid.GetValue(KeyProperty);

	public static void SetKey(DataGrid grid, string value) => grid.SetValue(KeyProperty, value);

	private static MainViewModel Main(DataGrid grid) => TopLevel.GetTopLevel(grid)?.DataContext as MainViewModel;

	private static void OnAttached(object sender, VisualTreeAttachmentEventArgs e) => Restore((DataGrid)sender);

	/// <summary>A drag on a column header's edge ends with the pointer let go there: remember the widths.</summary>
	private static void OnPointerReleased(object sender, PointerReleasedEventArgs e)
	{
		if (e.Source is Visual source && source.FindAncestorOfType<DataGridColumnHeader>(includeSelf: true) != null)
		{
			Remember((DataGrid)sender);
		}
	}

	private static string ColumnKey(DataGridColumn column) => column.SortMemberPath ?? column.Header as string;

	private static Dictionary<DataGridColumn, DataGridLength> Defaults(DataGrid grid)
	{
		Dictionary<DataGridColumn, DataGridLength> defaults = grid.GetValue(DefaultsProperty);
		if (defaults == null)
		{
			defaults = grid.Columns.ToDictionary(c => c, c => c.Width);
			grid.SetValue(DefaultsProperty, defaults);
		}
		return defaults;
	}

	/// <summary>Puts the remembered widths back (columns not remembered keep the view's width).</summary>
	public static void Restore(DataGrid grid)
	{
		MainViewModel main = Main(grid);
		string table = GetKey(grid);
		if (main == null || table == null)
		{
			return;
		}
		Dictionary<DataGridColumn, DataGridLength> defaults = Defaults(grid);
		IReadOnlyDictionary<string, string> widths = main.ColumnWidths(table);
		foreach (DataGridColumn column in grid.Columns)
		{
			string key = ColumnKey(column);
			DataGridLength width = key != null && widths.TryGetValue(key, out string text) && Parse(text) is DataGridLength saved ? saved : defaults[column];
			if (!Same(column.Width, width))
			{
				column.Width = width;
			}
		}
	}

	/// <summary>Keeps the widths that differ from the view's.</summary>
	public static void Remember(DataGrid grid)
	{
		MainViewModel main = Main(grid);
		string table = GetKey(grid);
		if (main == null || table == null)
		{
			return;
		}
		Dictionary<DataGridColumn, DataGridLength> defaults = Defaults(grid);
		Dictionary<string, string> widths = new();
		foreach (DataGridColumn column in grid.Columns)
		{
			string key = ColumnKey(column);
			if (key != null && column.CanUserResize && !Same(column.Width, defaults[column]))
			{
				widths[key] = Format(column.Width);
			}
		}
		main.SetColumnWidths(table, widths);
	}

	private static bool Same(DataGridLength a, DataGridLength b) => a.UnitType == b.UnitType && Math.Abs(a.Value - b.Value) < 0.5;

	/// <summary>"140" for pixels, "1.5*" for a share of the rest, "Auto".</summary>
	public static string Format(DataGridLength width) => width.UnitType switch
	{
		DataGridLengthUnitType.Star => width.Value.ToString("0.###", CultureInfo.InvariantCulture) + "*",
		DataGridLengthUnitType.Auto => "Auto",
		_ => Math.Round(width.Value).ToString(CultureInfo.InvariantCulture),
	};

	/// <summary>The width written by <see cref="Format"/>, or null when it can't be read.</summary>
	public static DataGridLength? Parse(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		text = text.Trim();
		if (text == "Auto")
		{
			return DataGridLength.Auto;
		}
		bool star = text.EndsWith('*');
		if (!double.TryParse(star ? text[..^1] : text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value <= 0 || value > 10000)
		{
			return null;
		}
		return new DataGridLength(value, star ? DataGridLengthUnitType.Star : DataGridLengthUnitType.Pixel);
	}
}
