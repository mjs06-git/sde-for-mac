using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Sde.App.ViewModels;

namespace Sde.App.Views;

/// <summary>The Assignments page: the teams in a pane on the left, the selected team's route edge to edge on the right.</summary>
public partial class AssignmentsView : UserControl
{
	public AssignmentsView() => InitializeComponent();

	private AssignmentsViewModel Vm => (AssignmentsViewModel)DataContext;

	/// <summary>Double-clicking a row (not a column header, nor its check box, status menu or directions) opens its assessment.</summary>
	private void OnRouteDoubleTapped(object sender, TappedEventArgs e)
	{
		if (Vm.SelectedStop != null && e.Source is Control c && c.FindAncestorOfType<DataGridColumnHeader>(includeSelf: true) == null
			&& c.FindAncestorOfType<ComboBox>(includeSelf: true) == null && c.FindAncestorOfType<Button>(includeSelf: true) == null
			&& c.FindAncestorOfType<CheckBox>(includeSelf: true) == null)
		{
			Vm.OpenCommand.Execute(Vm.SelectedStop);
		}
	}

	/// <summary>Return opens the selected row's assessment.</summary>
	private void OnRouteKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter && Vm.SelectedStop != null)
		{
			Vm.OpenCommand.Execute(Vm.SelectedStop);
			e.Handled = true;
		}
	}
}
