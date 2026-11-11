using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Sde.App.ViewModels;

namespace Sde.App.Views;

public partial class SourceImportView : UserControl
{
	public SourceImportView()
	{
		InitializeComponent();
		DragDrop.SetAllowDrop(this, true);
		// One file over the page: this page takes it (handled, so the window's drop overlay stays away).
		AddHandler(DragDrop.DragOverEvent, (_, e) =>
		{
			bool one = e.DataTransfer.TryGetFiles() is { Length: 1 };
			e.DragEffects = one ? DragDropEffects.Copy : DragDropEffects.None;
			e.Handled = one;
		});
		AddHandler(DragDrop.DropEvent, (_, e) =>
		{
			if (e.DataTransfer.TryGetFiles() is { Length: 1 } files && files[0].TryGetLocalPath() is string path && DataContext is SourceImportViewModel vm)
			{
				e.Handled = true;
				vm.OpenFile(path);
			}
		});
	}
}
