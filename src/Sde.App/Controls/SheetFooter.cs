using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Sde.App.Services;

namespace Sde.App.Controls;

/// <summary>
/// The one row of buttons at the foot of every sheet, always in the same order: an optional leading action at the far
/// left (red when it throws something away), anything the sheet adds after it (the content: a link, a count), then on
/// the right Cancel followed by the one default action in the accent colour. Return presses the default action and
/// Esc cancels (the window's Esc goes to <see cref="SheetHost.CancelCurrentCommand"/>, so this row needs no Esc button
/// of its own). A sheet whose edits apply as they are made uses <see cref="IsDone"/>: a single default Done button.
/// While <see cref="IsBusy"/> the buttons are off and the row shows progress and <see cref="BusyText"/>, with Stop in
/// Cancel's place when <see cref="StopCommand"/> is set. Styled in Styles/Sheets.axaml.
/// <c>&lt;c:SheetFooter PrimaryText="Save" PrimaryCommand="{Binding SaveCommand}" /&gt;</c>
/// </summary>
public class SheetFooter : ContentControl
{
	/// <summary>The default action's label, a specific verb ("Save", "Import", "Delete").</summary>
	public static readonly StyledProperty<string> PrimaryTextProperty = AvaloniaProperty.Register<SheetFooter, string>(nameof(PrimaryText));

	public static readonly StyledProperty<ICommand> PrimaryCommandProperty = AvaloniaProperty.Register<SheetFooter, ICommand>(nameof(PrimaryCommand));

	public static readonly StyledProperty<object> PrimaryCommandParameterProperty = AvaloniaProperty.Register<SheetFooter, object>(nameof(PrimaryCommandParameter));

	public static readonly StyledProperty<bool> IsPrimaryVisibleProperty = AvaloniaProperty.Register<SheetFooter, bool>(nameof(IsPrimaryVisible), true);

	public static readonly StyledProperty<bool> IsPrimaryEnabledProperty = AvaloniaProperty.Register<SheetFooter, bool>(nameof(IsPrimaryEnabled), true);

	/// <summary>The default action deletes or throws work away: it is red and Return does not press it.</summary>
	public static readonly StyledProperty<bool> IsPrimaryDestructiveProperty = AvaloniaProperty.Register<SheetFooter, bool>(nameof(IsPrimaryDestructive));

	/// <summary>Whether Return presses the default action (true unless it is destructive or the sheet takes Return itself).</summary>
	public static readonly StyledProperty<bool> IsPrimaryDefaultProperty = AvaloniaProperty.Register<SheetFooter, bool>(nameof(IsPrimaryDefault), true);

	public static readonly StyledProperty<string> PrimaryAutomationNameProperty = AvaloniaProperty.Register<SheetFooter, string>(nameof(PrimaryAutomationName));

	public static readonly StyledProperty<string> PrimaryToolTipProperty = AvaloniaProperty.Register<SheetFooter, string>(nameof(PrimaryToolTip));

	public static readonly StyledProperty<string> CancelTextProperty = AvaloniaProperty.Register<SheetFooter, string>(nameof(CancelText), "Cancel");

	/// <summary>What Cancel runs; by default the sheet's own <see cref="SheetViewModel.CancelCommand"/>.</summary>
	public static readonly StyledProperty<ICommand> CancelCommandProperty = AvaloniaProperty.Register<SheetFooter, ICommand>(nameof(CancelCommand));

	public static readonly StyledProperty<bool> IsCancelVisibleProperty = AvaloniaProperty.Register<SheetFooter, bool>(nameof(IsCancelVisible), true);

	public static readonly StyledProperty<bool> IsCancelEnabledProperty = AvaloniaProperty.Register<SheetFooter, bool>(nameof(IsCancelEnabled), true);

	public static readonly StyledProperty<string> CancelAutomationNameProperty = AvaloniaProperty.Register<SheetFooter, string>(nameof(CancelAutomationName));

	/// <summary>The far-left action's label ("Don't Save", "Delete Event…", "Undo This Change"); no button when empty.</summary>
	public static readonly StyledProperty<string> LeadingTextProperty = AvaloniaProperty.Register<SheetFooter, string>(nameof(LeadingText));

	public static readonly StyledProperty<ICommand> LeadingCommandProperty = AvaloniaProperty.Register<SheetFooter, ICommand>(nameof(LeadingCommand));

	public static readonly StyledProperty<bool> IsLeadingVisibleProperty = AvaloniaProperty.Register<SheetFooter, bool>(nameof(IsLeadingVisible), true);

	public static readonly StyledProperty<bool> IsLeadingEnabledProperty = AvaloniaProperty.Register<SheetFooter, bool>(nameof(IsLeadingEnabled), true);

	/// <summary>The leading action deletes or throws work away: its label is red.</summary>
	public static readonly StyledProperty<bool> IsLeadingDestructiveProperty = AvaloniaProperty.Register<SheetFooter, bool>(nameof(IsLeadingDestructive));

	public static readonly StyledProperty<string> LeadingAutomationNameProperty = AvaloniaProperty.Register<SheetFooter, string>(nameof(LeadingAutomationName));

	public static readonly StyledProperty<string> LeadingToolTipProperty = AvaloniaProperty.Register<SheetFooter, string>(nameof(LeadingToolTip));

	/// <summary>
	/// The edits apply as they are made: one default button, <see cref="DoneText"/>, that runs <see cref="DoneCommand"/>
	/// (by default it closes the sheet), and no Cancel. The primary text and command are not used while this is on.
	/// </summary>
	public static readonly StyledProperty<bool> IsDoneProperty = AvaloniaProperty.Register<SheetFooter, bool>(nameof(IsDone));

	public static readonly StyledProperty<string> DoneTextProperty = AvaloniaProperty.Register<SheetFooter, string>(nameof(DoneText), "Done");

	/// <summary>What Done runs; by default the sheet's own <see cref="SheetViewModel.CancelCommand"/> (it closes the sheet).</summary>
	public static readonly StyledProperty<ICommand> DoneCommandProperty = AvaloniaProperty.Register<SheetFooter, ICommand>(nameof(DoneCommand));

	/// <summary>Work is under way: the buttons are off and the row shows progress and <see cref="BusyText"/>.</summary>
	public static readonly StyledProperty<bool> IsBusyProperty = AvaloniaProperty.Register<SheetFooter, bool>(nameof(IsBusy));

	public static readonly StyledProperty<string> BusyTextProperty = AvaloniaProperty.Register<SheetFooter, string>(nameof(BusyText));

	/// <summary>How far the work has got, 0 to 100; below 0 (the default) the progress bar just moves.</summary>
	public static readonly StyledProperty<double> ProgressProperty = AvaloniaProperty.Register<SheetFooter, double>(nameof(Progress), -1);

	/// <summary>Stops the work; while busy, Stop takes Cancel's place when this is set.</summary>
	public static readonly StyledProperty<ICommand> StopCommandProperty = AvaloniaProperty.Register<SheetFooter, ICommand>(nameof(StopCommand));

	public static readonly DirectProperty<SheetFooter, ICommand> EffectiveCancelCommandProperty =
		AvaloniaProperty.RegisterDirect<SheetFooter, ICommand>(nameof(EffectiveCancelCommand), f => f.EffectiveCancelCommand);

	public static readonly DirectProperty<SheetFooter, ICommand> EffectivePrimaryCommandProperty =
		AvaloniaProperty.RegisterDirect<SheetFooter, ICommand>(nameof(EffectivePrimaryCommand), f => f.EffectivePrimaryCommand);

	public static readonly DirectProperty<SheetFooter, string> EffectivePrimaryTextProperty =
		AvaloniaProperty.RegisterDirect<SheetFooter, string>(nameof(EffectivePrimaryText), f => f.EffectivePrimaryText);

	public static readonly DirectProperty<SheetFooter, string> EffectiveCancelTextProperty =
		AvaloniaProperty.RegisterDirect<SheetFooter, string>(nameof(EffectiveCancelText), f => f.EffectiveCancelText);

	public static readonly DirectProperty<SheetFooter, bool> ShowsLeadingProperty =
		AvaloniaProperty.RegisterDirect<SheetFooter, bool>(nameof(ShowsLeading), f => f.ShowsLeading);

	public static readonly DirectProperty<SheetFooter, bool> ShowsCancelProperty =
		AvaloniaProperty.RegisterDirect<SheetFooter, bool>(nameof(ShowsCancel), f => f.ShowsCancel);

	public static readonly DirectProperty<SheetFooter, bool> ShowsPrimaryProperty =
		AvaloniaProperty.RegisterDirect<SheetFooter, bool>(nameof(ShowsPrimary), f => f.ShowsPrimary);

	public static readonly DirectProperty<SheetFooter, bool> PrimaryIsDefaultProperty =
		AvaloniaProperty.RegisterDirect<SheetFooter, bool>(nameof(PrimaryIsDefault), f => f.PrimaryIsDefault);

	public static readonly DirectProperty<SheetFooter, bool> PrimaryIsOnProperty =
		AvaloniaProperty.RegisterDirect<SheetFooter, bool>(nameof(PrimaryIsOn), f => f.PrimaryIsOn);

	public static readonly DirectProperty<SheetFooter, bool> CancelIsOnProperty =
		AvaloniaProperty.RegisterDirect<SheetFooter, bool>(nameof(CancelIsOn), f => f.CancelIsOn);

	public static readonly DirectProperty<SheetFooter, bool> LeadingIsOnProperty =
		AvaloniaProperty.RegisterDirect<SheetFooter, bool>(nameof(LeadingIsOn), f => f.LeadingIsOn);

	public static readonly DirectProperty<SheetFooter, bool> IsProgressIndeterminateProperty =
		AvaloniaProperty.RegisterDirect<SheetFooter, bool>(nameof(IsProgressIndeterminate), f => f.IsProgressIndeterminate);

	private Button _primary;
	private Button _leading;
	private ICommand _effectiveCancelCommand;
	private ICommand _effectivePrimaryCommand;
	private string _effectivePrimaryText;
	private string _effectiveCancelText = "Cancel";
	private bool _showsLeading;
	private bool _showsCancel = true;
	private bool _showsPrimary;
	private bool _primaryIsDefault;
	private bool _primaryIsOn = true;
	private bool _cancelIsOn = true;
	private bool _leadingIsOn = true;
	private bool _isProgressIndeterminate = true;

	public string PrimaryText
	{
		get => GetValue(PrimaryTextProperty);
		set => SetValue(PrimaryTextProperty, value);
	}

	public ICommand PrimaryCommand
	{
		get => GetValue(PrimaryCommandProperty);
		set => SetValue(PrimaryCommandProperty, value);
	}

	public object PrimaryCommandParameter
	{
		get => GetValue(PrimaryCommandParameterProperty);
		set => SetValue(PrimaryCommandParameterProperty, value);
	}

	public bool IsPrimaryVisible
	{
		get => GetValue(IsPrimaryVisibleProperty);
		set => SetValue(IsPrimaryVisibleProperty, value);
	}

	public bool IsPrimaryEnabled
	{
		get => GetValue(IsPrimaryEnabledProperty);
		set => SetValue(IsPrimaryEnabledProperty, value);
	}

	public bool IsPrimaryDestructive
	{
		get => GetValue(IsPrimaryDestructiveProperty);
		set => SetValue(IsPrimaryDestructiveProperty, value);
	}

	public bool IsPrimaryDefault
	{
		get => GetValue(IsPrimaryDefaultProperty);
		set => SetValue(IsPrimaryDefaultProperty, value);
	}

	public string PrimaryAutomationName
	{
		get => GetValue(PrimaryAutomationNameProperty);
		set => SetValue(PrimaryAutomationNameProperty, value);
	}

	public string PrimaryToolTip
	{
		get => GetValue(PrimaryToolTipProperty);
		set => SetValue(PrimaryToolTipProperty, value);
	}

	public string CancelText
	{
		get => GetValue(CancelTextProperty);
		set => SetValue(CancelTextProperty, value);
	}

	public ICommand CancelCommand
	{
		get => GetValue(CancelCommandProperty);
		set => SetValue(CancelCommandProperty, value);
	}

	public bool IsCancelVisible
	{
		get => GetValue(IsCancelVisibleProperty);
		set => SetValue(IsCancelVisibleProperty, value);
	}

	public bool IsCancelEnabled
	{
		get => GetValue(IsCancelEnabledProperty);
		set => SetValue(IsCancelEnabledProperty, value);
	}

	public string CancelAutomationName
	{
		get => GetValue(CancelAutomationNameProperty);
		set => SetValue(CancelAutomationNameProperty, value);
	}

	public string LeadingText
	{
		get => GetValue(LeadingTextProperty);
		set => SetValue(LeadingTextProperty, value);
	}

	public ICommand LeadingCommand
	{
		get => GetValue(LeadingCommandProperty);
		set => SetValue(LeadingCommandProperty, value);
	}

	public bool IsLeadingVisible
	{
		get => GetValue(IsLeadingVisibleProperty);
		set => SetValue(IsLeadingVisibleProperty, value);
	}

	public bool IsLeadingEnabled
	{
		get => GetValue(IsLeadingEnabledProperty);
		set => SetValue(IsLeadingEnabledProperty, value);
	}

	public bool IsLeadingDestructive
	{
		get => GetValue(IsLeadingDestructiveProperty);
		set => SetValue(IsLeadingDestructiveProperty, value);
	}

	public string LeadingAutomationName
	{
		get => GetValue(LeadingAutomationNameProperty);
		set => SetValue(LeadingAutomationNameProperty, value);
	}

	public string LeadingToolTip
	{
		get => GetValue(LeadingToolTipProperty);
		set => SetValue(LeadingToolTipProperty, value);
	}

	public bool IsDone
	{
		get => GetValue(IsDoneProperty);
		set => SetValue(IsDoneProperty, value);
	}

	public string DoneText
	{
		get => GetValue(DoneTextProperty);
		set => SetValue(DoneTextProperty, value);
	}

	public ICommand DoneCommand
	{
		get => GetValue(DoneCommandProperty);
		set => SetValue(DoneCommandProperty, value);
	}

	public bool IsBusy
	{
		get => GetValue(IsBusyProperty);
		set => SetValue(IsBusyProperty, value);
	}

	public string BusyText
	{
		get => GetValue(BusyTextProperty);
		set => SetValue(BusyTextProperty, value);
	}

	public double Progress
	{
		get => GetValue(ProgressProperty);
		set => SetValue(ProgressProperty, value);
	}

	public ICommand StopCommand
	{
		get => GetValue(StopCommandProperty);
		set => SetValue(StopCommandProperty, value);
	}

	/// <summary>What the Cancel button runs: Stop while busy, else <see cref="CancelCommand"/> or the sheet's own Cancel.</summary>
	public ICommand EffectiveCancelCommand
	{
		get => _effectiveCancelCommand;
		private set => SetAndRaise(EffectiveCancelCommandProperty, ref _effectiveCancelCommand, value);
	}

	/// <summary>What the default button runs: <see cref="PrimaryCommand"/>, or closing the sheet for Done.</summary>
	public ICommand EffectivePrimaryCommand
	{
		get => _effectivePrimaryCommand;
		private set => SetAndRaise(EffectivePrimaryCommandProperty, ref _effectivePrimaryCommand, value);
	}

	public string EffectivePrimaryText
	{
		get => _effectivePrimaryText;
		private set => SetAndRaise(EffectivePrimaryTextProperty, ref _effectivePrimaryText, value);
	}

	public string EffectiveCancelText
	{
		get => _effectiveCancelText;
		private set => SetAndRaise(EffectiveCancelTextProperty, ref _effectiveCancelText, value);
	}

	public bool ShowsLeading
	{
		get => _showsLeading;
		private set => SetAndRaise(ShowsLeadingProperty, ref _showsLeading, value);
	}

	public bool ShowsCancel
	{
		get => _showsCancel;
		private set => SetAndRaise(ShowsCancelProperty, ref _showsCancel, value);
	}

	public bool ShowsPrimary
	{
		get => _showsPrimary;
		private set => SetAndRaise(ShowsPrimaryProperty, ref _showsPrimary, value);
	}

	public bool PrimaryIsDefault
	{
		get => _primaryIsDefault;
		private set => SetAndRaise(PrimaryIsDefaultProperty, ref _primaryIsDefault, value);
	}

	public bool PrimaryIsOn
	{
		get => _primaryIsOn;
		private set => SetAndRaise(PrimaryIsOnProperty, ref _primaryIsOn, value);
	}

	public bool CancelIsOn
	{
		get => _cancelIsOn;
		private set => SetAndRaise(CancelIsOnProperty, ref _cancelIsOn, value);
	}

	public bool LeadingIsOn
	{
		get => _leadingIsOn;
		private set => SetAndRaise(LeadingIsOnProperty, ref _leadingIsOn, value);
	}

	public bool IsProgressIndeterminate
	{
		get => _isProgressIndeterminate;
		private set => SetAndRaise(IsProgressIndeterminateProperty, ref _isProgressIndeterminate, value);
	}

	/// <summary>The default button and the leading button, for tests.</summary>
	public Button PrimaryButton => _primary;

	public Button LeadingButton => _leading;

	protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
	{
		base.OnApplyTemplate(e);
		_primary = e.NameScope.Find<Button>("PART_Primary");
		_leading = e.NameScope.Find<Button>("PART_Leading");
		Update();
	}

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		Update();
	}

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property.OwnerType == typeof(SheetFooter) && !change.Property.IsDirect)
		{
			Update();
		}
	}

	/// <summary>Works out what each button shows and runs from the properties the sheet set.</summary>
	private void Update()
	{
		ICommand sheetCancel = CancelCommand ?? (DataContext as SheetViewModel)?.CancelCommand;
		bool stopping = IsBusy && StopCommand != null;
		EffectiveCancelCommand = stopping ? StopCommand : sheetCancel;
		EffectiveCancelText = stopping ? "Stop" : CancelText;
		EffectivePrimaryCommand = IsDone ? DoneCommand ?? sheetCancel : PrimaryCommand;
		EffectivePrimaryText = IsDone ? DoneText : PrimaryText;
		ShowsLeading = !string.IsNullOrEmpty(LeadingText) && IsLeadingVisible;
		ShowsCancel = !IsDone && IsCancelVisible;
		ShowsPrimary = (IsDone || IsPrimaryVisible) && !string.IsNullOrEmpty(EffectivePrimaryText);
		PrimaryIsDefault = IsPrimaryDefault && (IsDone || !IsPrimaryDestructive);
		PrimaryIsOn = (IsDone || IsPrimaryEnabled) && !IsBusy;
		CancelIsOn = stopping || IsCancelEnabled && !IsBusy;
		LeadingIsOn = IsLeadingEnabled && !IsBusy;
		IsProgressIndeterminate = Progress < 0;
		if (_primary != null)
		{
			bool red = IsPrimaryDestructive && !IsDone;
			SetClass(_primary, "primary", !red);
			SetClass(_primary, "destructive", red);
		}
		if (_leading != null)
		{
			SetClass(_leading, "destructive", IsLeadingDestructive);
		}
	}

	private static void SetClass(Control control, string name, bool on)
	{
		if (on && !control.Classes.Contains(name))
		{
			control.Classes.Add(name);
		}
		else if (!on)
		{
			control.Classes.Remove(name);
		}
	}
}
