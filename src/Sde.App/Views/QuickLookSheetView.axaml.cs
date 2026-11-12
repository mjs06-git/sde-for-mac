using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Sde.App.ViewModels.Sheets;

namespace Sde.App.Views;

/// <summary>
/// Quick Look: the photo on a dark stage as tall as the window allows. Left and Right step through the photos and Space
/// closes (Esc closes too, as for every sheet); the keys are taken before a focused button sees them.
/// </summary>
public partial class QuickLookSheetView : UserControl
{
	/// <summary>The room the window keeps above and below the stage: the title band, the sheet's margins, title and footer.</summary>
	private const double Chrome = 250;

	private TopLevel _top;

	public QuickLookSheetView()
	{
		InitializeComponent();
		AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
	}

	private QuickLookSheetViewModel Vm => DataContext as QuickLookSheetViewModel;

	protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		_top = TopLevel.GetTopLevel(this);
		if (_top != null)
		{
			_top.PropertyChanged += OnTopChanged;
			FitStage();
		}
		Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Background);
	}

	protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
	{
		if (_top != null)
		{
			_top.PropertyChanged -= OnTopChanged;
			_top = null;
		}
		base.OnDetachedFromVisualTree(e);
	}

	private void OnTopChanged(object sender, AvaloniaPropertyChangedEventArgs e)
	{
		if (e.Property == BoundsProperty || e.Property == TopLevel.ClientSizeProperty)
		{
			FitStage();
		}
	}

	/// <summary>The stage takes the window's height less its chrome, so the photo is as large as the window allows.</summary>
	private void FitStage()
	{
		if (_top != null)
		{
			Stage.Height = Math.Max(Stage.MinHeight, _top.ClientSize.Height - Chrome);
		}
	}

	private void OnKey(object sender, KeyEventArgs e)
	{
		QuickLookSheetViewModel vm = Vm;
		if (vm == null || e.KeyModifiers != KeyModifiers.None || e.Source is TextBox)
		{
			return;
		}
		switch (e.Key)
		{
			case Key.Left:
				vm.PreviousCommand.Execute(null);
				e.Handled = true;
				break;
			case Key.Right:
				vm.NextCommand.Execute(null);
				e.Handled = true;
				break;
			case Key.Space:
				vm.Cancel();
				e.Handled = true;
				break;
		}
	}
}
