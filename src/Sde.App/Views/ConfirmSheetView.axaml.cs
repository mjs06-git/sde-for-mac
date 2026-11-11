using Avalonia.Controls;

namespace Sde.App.Views;

/// <summary>The window's confirmation (IDialogs.ConfirmAsync): a question, Cancel and the action, red when it destroys something.</summary>
public partial class ConfirmSheetView : UserControl
{
	public ConfirmSheetView() => InitializeComponent();
}
