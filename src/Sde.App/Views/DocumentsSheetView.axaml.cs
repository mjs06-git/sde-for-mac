using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Sde.App.ViewModels.Sheets;

namespace Sde.App.Views;

/// <summary>The structure's documents: the table, Add Document…, and files dropped on the sheet.</summary>
public partial class DocumentsSheetView : UserControl
{
	public DocumentsSheetView()
	{
		InitializeComponent();
		AddHandler(DragDrop.DragOverEvent, OnDragOver);
		AddHandler(DragDrop.DropEvent, OnDrop);
	}

	private DocumentsSheetViewModel Vm => DataContext as DocumentsSheetViewModel;

	private void OnDragOver(object sender, DragEventArgs e)
	{
		e.DragEffects = e.DataTransfer.TryGetFiles() is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;
		e.Handled = true;
	}

	private void OnDrop(object sender, DragEventArgs e)
	{
		IStorageItem[] files = e.DataTransfer.TryGetFiles();
		if (files != null && Vm != null)
		{
			Vm.Dropped(files.Select(f => f.TryGetLocalPath()).Where(p => p != null).ToList());
		}
		e.Handled = true;
	}

	/// <summary>A double-click on a row opens the document.</summary>
	private void OnRowDoubleTapped(object sender, TappedEventArgs e)
	{
		if (sender is Control { DataContext: DocumentRowViewModel row } && Vm != null)
		{
			Vm.OpenCommand.Execute(row);
		}
	}
}
