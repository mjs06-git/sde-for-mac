using Avalonia.Controls;

namespace Sde.App.Views;

/// <summary>The full-window overlay shown while files are dragged over the window (its DataContext is the <see cref="ViewModels.DropPlan"/>).</summary>
public partial class DropOverlay : UserControl
{
	public DropOverlay() => InitializeComponent();
}
