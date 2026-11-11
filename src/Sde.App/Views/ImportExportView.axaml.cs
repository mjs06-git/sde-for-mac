using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Sde.App.ViewModels;

namespace Sde.App.Views;

public partial class ImportExportView : UserControl
{
	public ImportExportView()
	{
		InitializeComponent();
		DragDrop.SetAllowDrop(this, true);
		AddHandler(DragDrop.DragOverEvent, OnDragOver);
		AddHandler(DragDrop.DragLeaveEvent, (_, _) => ShowDragOver(false));
		AddHandler(DragDrop.DropEvent, OnDrop);
	}

	private void OnDragOver(object sender, DragEventArgs e)
	{
		bool one = e.DataTransfer.TryGetFiles() is { Length: 1 };
		e.DragEffects = one ? DragDropEffects.Copy : DragDropEffects.None;
		ShowDragOver(one);
	}

	/// <summary>Tints the drop zone while a file is held over the page.</summary>
	private void ShowDragOver(bool over) => DropZone.Classes.Set("dragOver", over);

	private async void OnDrop(object sender, DragEventArgs e)
	{
		ShowDragOver(false);
		if (e.DataTransfer.TryGetFiles() is { Length: 1 } files && files[0].TryGetLocalPath() is string path && DataContext is ImportExportViewModel vm)
		{
			await vm.DropAsync(path);
		}
	}
}
