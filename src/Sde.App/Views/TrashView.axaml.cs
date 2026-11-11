using Avalonia.Controls;
using Avalonia.Input;
using Sde.App.ViewModels;

namespace Sde.App.Views;

/// <summary>Recently Deleted: the deleted items as a table; Delete removes the selected rows for good (after asking).</summary>
public partial class TrashView : UserControl
{
	public TrashView() => InitializeComponent();

	private TrashViewModel Vm => DataContext as TrashViewModel;

	/// <summary>The table's selection (one or several rows) goes to the view model, for Restore and Delete Now.</summary>
	private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (sender is DataGrid grid && Vm != null)
		{
			Vm.SetSelection(grid.SelectedItems.OfType<DeletedItemViewModel>());
		}
	}

	/// <summary>Delete (or Backspace, the Mac's delete key) asks to remove the selected rows for good.</summary>
	private void OnListKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key is Key.Delete or Key.Back && Vm != null && Vm.HasSelection)
		{
			Vm.DeleteNowCommand.Execute(null);
			e.Handled = true;
		}
	}
}
