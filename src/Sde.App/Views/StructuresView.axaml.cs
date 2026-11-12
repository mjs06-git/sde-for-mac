using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Layout;
using Sde.App.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Sde.App.ViewModels;

namespace Sde.App.Views;

public partial class StructuresView : UserControl
{
	private StructuresViewModel _subscribed;

	/// <summary>Each column's header text, as written in the view (the sorted one also shows a caret).</summary>
	private readonly Dictionary<DataGridColumn, string> _headers = new();

	public StructuresView()
	{
		InitializeComponent();
		// A right-click selects the row first, so the row menu acts on the row that was clicked.
		Grid.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel);
	}

	private StructuresViewModel Vm => (StructuresViewModel)DataContext;

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		if (_subscribed != null)
		{
			_subscribed.FocusSearchRequested -= FocusSearch;
			_subscribed.ColumnsChanged -= ApplyColumns;
			_subscribed.PropertyChanged -= OnVmPropertyChanged;
			_subscribed.FilterEditRequested -= OpenChipEditor;
		}
		_subscribed = DataContext as StructuresViewModel;
		if (_subscribed != null)
		{
			_subscribed.FocusSearchRequested += FocusSearch;
			_subscribed.ColumnsChanged += ApplyColumns;
			_subscribed.PropertyChanged += OnVmPropertyChanged;
			_subscribed.FilterEditRequested += OpenChipEditor;
			ApplyColumns();
		}
	}

	private void OnVmPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(StructuresViewModel.SortKey) or nameof(StructuresViewModel.SortDescending))
		{
			ApplyColumns();
		}
	}

	/// <summary>Shows the chosen columns and marks the sorted one with a caret (up for ascending, down for descending).</summary>
	private void ApplyColumns()
	{
		if (Vm == null)
		{
			return;
		}
		foreach (DataGridColumn c in Grid.Columns)
		{
			string key = c.SortMemberPath;
			if (key == null)
			{
				continue;
			}
			c.IsVisible = Vm.IsColumnVisible(key);
			if (!_headers.TryGetValue(c, out string text))
			{
				text = c.Header as string;
				_headers[c] = text;
			}
			c.Header = Vm.SortKey == key ? SortedHeader(text, Vm.SortDescending) : text;
		}
	}

	/// <summary>A sorted column's header: its text and a caret in the accent colour.</summary>
	private static Control SortedHeader(string text, bool descending) => new StackPanel
	{
		Orientation = Orientation.Horizontal,
		Spacing = 4,
		Children =
		{
			new TextBlock { Text = text, Classes = { "sortedHeader" }, VerticalAlignment = VerticalAlignment.Center },
			new Icon { Kind = descending ? "CaretDown" : "CaretUp", Size = 12, Classes = { "accent" }, VerticalAlignment = VerticalAlignment.Center },
		},
	};

	/// <summary>Header clicks sort in the view model (so the list's order is also the order reports print in).</summary>
	private void OnSorting(object sender, DataGridColumnEventArgs e)
	{
		e.Handled = true;
		if (e.Column.SortMemberPath is string key)
		{
			Vm.SortBy(key);
		}
	}

	/// <summary>⌘F: the toolbar's search field (it filters this list).</summary>
	private void FocusSearch()
	{
		TextBox box = TopLevel.GetTopLevel(this)?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "SearchBox");
		box?.Focus();
		box?.SelectAll();
	}

	private void OnGridPointerPressed(object sender, PointerPressedEventArgs e)
	{
		if (!e.GetCurrentPoint(Grid).Properties.IsRightButtonPressed || e.Source is not Control source)
		{
			return;
		}
		if (source.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext is StructureRowViewModel row)
		{
			Vm.Selected = row;
		}
	}

	private void OnRowDoubleTapped(object sender, TappedEventArgs e)
	{
		if (Vm.Selected != null && e.Source is Control c && c.FindAncestorOfType<DataGridColumnHeader>(includeSelf: true) == null)
		{
			Vm.OpenCommand.Execute(Vm.Selected);
		}
	}

	/// <summary>Add Filter, then a field: the menu closes (the new chip's editor opens instead).</summary>
	private void OnAddFilterClick(object sender, RoutedEventArgs e) => AddFilterButton.Flyout?.Hide();

	/// <summary>Opens the editor under a filter's chip (after Add Filter made it).</summary>
	private void OpenChipEditor(string key)
	{
		Dispatcher.UIThread.Post(() =>
		{
			Button label = FilterChipList.GetVisualDescendants().OfType<Button>()
				.FirstOrDefault(b => b.Classes.Contains("filterChipLabel") && (b.DataContext as FilterChipViewModel)?.Key == key);
			label?.Flyout?.ShowAt(label);
		}, DispatcherPriority.Background);
	}

	private static string ChipKey(object sender) => ((sender as FlyoutBase)?.Target?.DataContext as FilterChipViewModel)?.Key;

	/// <summary>A chip's editor opened: the chip stays while it is edited.</summary>
	private void OnChipEditorOpened(object sender, EventArgs e)
	{
		if (ChipKey(sender) is string key)
		{
			Vm?.BeginEditFilter(key);
		}
	}

	/// <summary>A chip's editor closed: a chip left with no value goes.</summary>
	private void OnChipEditorClosed(object sender, EventArgs e)
	{
		if (ChipKey(sender) is string key)
		{
			Vm?.DropPendingFilter(key);
		}
	}

	/// <summary>Add Tag opened: the cursor goes in its name box, ready to type.</summary>
	private void OnAddTagOpened(object sender, EventArgs e)
	{
		Dispatcher.UIThread.Post(() =>
		{
			TextBox box = ((sender as Flyout)?.Content as Control)?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()
				?? (sender as Flyout)?.Content as TextBox;
			box?.Focus();
		}, DispatcherPriority.Background);
	}

	/// <summary>Return in Add Tag's box: the one tag that matches, or a new tag with that name.</summary>
	private void OnTagQueryKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter || Vm == null)
		{
			return;
		}
		if (Vm.CanCreateTag)
		{
			Vm.CreateTagForSelectedCommand.Execute(null);
		}
		else if (Vm.TagSuggestions.Count > 0)
		{
			Vm.AddTagToSelectedCommand.Execute(Vm.TagSuggestions[0]);
		}
		e.Handled = true;
	}

	private void OnListKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter && Vm.Selected != null)
		{
			Vm.OpenCommand.Execute(Vm.Selected);
			e.Handled = true;
		}
	}
}
