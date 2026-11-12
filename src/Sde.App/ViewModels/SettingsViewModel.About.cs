using CommunityToolkit.Mvvm.Input;
using Sde.App.ViewModels.Sheets;

namespace Sde.App.ViewModels;

/// <summary>Settings, About: the brand mark, the version, what's new, the help and the FEMA manual.</summary>
public sealed partial class SettingsViewModel
{
	/// <summary>The product's full name, under the brand mark.</summary>
	public const string ProductName = "Substantial Damage Estimator for Mac";

	/// <summary>Who made it and what it is based on, under the version.</summary>
	public const string AboutNote = "Unofficial Mac port of FEMA's SDE 3.0.0, using FEMA's engine, cost tables and rules. Not produced or endorsed by FEMA. Determinations remain the floodplain administrator's.";

	private Action _showWhatsNew;

	/// <summary>
	/// Shows what is new in this version. Set by the main view model when the app has a What's New page; until then the
	/// About card leaves the button out.
	/// </summary>
	public Action ShowWhatsNew
	{
		get => _showWhatsNew;
		set
		{
			if (SetProperty(ref _showWhatsNew, value))
			{
				OnPropertyChanged(nameof(HasWhatsNew));
				OpenWhatsNewCommand.NotifyCanExecuteChanged();
			}
		}
	}

	public bool HasWhatsNew => ShowWhatsNew != null;

	/// <summary>The About card's What's New button.</summary>
	[RelayCommand(CanExecute = nameof(HasWhatsNew))]
	private void OpenWhatsNew() => ShowWhatsNew?.Invoke();

	/// <summary>SDE Help: every field's help in a searchable list (as Help, then SDE Help).</summary>
	[RelayCommand]
	private void ShowHelp() => _ = _app.Sheets.ShowAsync(new HelpIndexSheetViewModel(_app));

	/// <summary>FEMA's SDE user manual, or where to read it when the PDF is not here (as Help, then FEMA SDE User Manual).</summary>
	[RelayCommand]
	private void OpenManual()
	{
		if (!_app.Manual.OpenFull())
		{
			_ = _app.Sheets.ShowAsync(new ManualMissingSheetViewModel(_app));
		}
	}
}
