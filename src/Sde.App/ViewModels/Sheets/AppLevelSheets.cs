using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.Interop.Online;

namespace Sde.App.ViewModels.Sheets;

/// <summary>
/// Help, then What's New in SDE (also Settings, About, and once at launch after an update): the brand mark, the large
/// heading and the changes in a few groups, from <see cref="WhatsNew"/>.
/// </summary>
public sealed partial class WhatsNewSheetViewModel() : SheetViewModel("What's New")
{
	public override SheetSize Size => SheetSize.Large;

	/// <summary>"What's new in SDE 3.5".</summary>
	public string Heading => WhatsNew.Heading;

	/// <summary>The groups of changes, each shown as a card.</summary>
	public IReadOnlyList<WhatsNewGroup> Groups => WhatsNew.Groups;

	[RelayCommand]
	private void Done() => Close(true);
}

/// <summary>
/// Help, then Check for Updates: asks GitHub for the latest release (only now, because it was chosen), then says SDE is
/// up to date, that a newer version exists (with its version, date and Open the Download Page), or that it couldn't
/// check. Nothing is downloaded or installed.
/// </summary>
public sealed partial class UpdateSheetViewModel : SheetViewModel
{
	private readonly AppServices _app;
	private readonly string _runningVersion;
	private CancellationTokenSource _cancel;

	/// <param name="app">The app's services: the shared HTTP client and the dialogs that open the release page.</param>
	/// <param name="runningVersion">This copy's version, such as "3.5.0".</param>
	public UpdateSheetViewModel(AppServices app, string runningVersion) : base("Check for Updates")
	{
		_app = app;
		_runningVersion = runningVersion;
		// Closing the sheet while GitHub hasn't answered stops waiting for it.
		Closing += _ => _cancel?.Cancel();
	}

	public override SheetSize Size => SheetSize.Small;

	/// <summary>The check's state: checking, up to date, newer available, or could not check.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsChecking), nameof(IsUpToDate), nameof(IsNewer), nameof(IsFailed), nameof(PrimaryText), nameof(PrimaryCommand), nameof(HasPrimary), nameof(CancelText), nameof(Headline), nameof(Detail))]
	public partial UpdateSheetState State { get; private set; }

	/// <summary>The newer release, when there is one.</summary>
	public ReleaseInfo Latest { get; private set; }

	/// <summary>Why the check failed, in plain English.</summary>
	public string FailureMessage { get; private set; }

	public bool IsChecking => State == UpdateSheetState.Checking;

	public bool IsUpToDate => State == UpdateSheetState.UpToDate;

	public bool IsNewer => State == UpdateSheetState.Newer;

	public bool IsFailed => State == UpdateSheetState.Failed;

	/// <summary>The footer's main button: Open the Download Page for a newer version, Try Again when the check failed.</summary>
	public string PrimaryText => State switch
	{
		UpdateSheetState.Newer => "Open the Download Page",
		UpdateSheetState.Failed => "Try Again",
		_ => null,
	};

	public System.Windows.Input.ICommand PrimaryCommand => State == UpdateSheetState.Newer ? OpenDownloadPageCommand : TryAgainCommand;

	public bool HasPrimary => State is UpdateSheetState.Newer or UpdateSheetState.Failed;

	/// <summary>The footer's other button: Cancel while checking, Not Now beside a newer version, Close after a failure.</summary>
	public string CancelText => State switch
	{
		UpdateSheetState.Newer => "Not Now",
		UpdateSheetState.Failed => "Close",
		_ => "Cancel",
	};

	public string Headline => State switch
	{
		UpdateSheetState.UpToDate => "SDE is up to date",
		UpdateSheetState.Newer => $"SDE {Latest?.Version} is available",
		UpdateSheetState.Failed => "Couldn't check for updates",
		_ => "Checking for updates…",
	};

	public string Detail => State switch
	{
		UpdateSheetState.UpToDate => $"Version {_runningVersion} is the latest.",
		UpdateSheetState.Newer => (Latest?.PublishedOn is DateTime on ? "Published " + on.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.GetCultureInfo("en-US")) + ". " : "")
			+ $"You have version {_runningVersion}.",
		UpdateSheetState.Failed => FailureMessage,
		_ => "Asking GitHub for the latest release.",
	};

	/// <summary>Asks GitHub once. The sheet shows "Checking" until the answer comes.</summary>
	public async Task CheckAsync()
	{
		_cancel?.Cancel();
		CancellationTokenSource cancel = _cancel = new CancellationTokenSource();
		Latest = null;
		FailureMessage = null;
		State = UpdateSheetState.Checking;
		UpdateCheckResult result;
		try
		{
			UpdateChecker checker = new UpdateChecker(_app.HttpClientFactory(), AppVersion.Name, _runningVersion);
			result = await checker.CheckAsync(_runningVersion, cancel.Token);
		}
		catch (OperationCanceledException)
		{
			return;
		}
		if (cancel.IsCancellationRequested || IsClosed)
		{
			return;
		}
		if (result.Outcome == UpdateCheckOutcome.CouldNotCheck && result.Detail != null)
		{
			AppLog.Write("Couldn't check for updates: " + result.Detail);
		}
		Latest = result.Latest;
		FailureMessage = result.Message;
		State = result.Outcome switch
		{
			UpdateCheckOutcome.NewerAvailable => UpdateSheetState.Newer,
			UpdateCheckOutcome.UpToDate => UpdateSheetState.UpToDate,
			_ => UpdateSheetState.Failed,
		};
		OnPropertyChanged(nameof(Latest));
		OnPropertyChanged(nameof(Headline));
		OnPropertyChanged(nameof(Detail));
	}

	/// <summary>Opens the release's page on github.com in the browser (only a page in SDE's repository), and closes.</summary>
	[RelayCommand]
	private void OpenDownloadPage()
	{
		if (Latest != null && UpdateChecker.IsReleasePage(Latest.PageUrl))
		{
			_app.Dialogs.Open(Latest.PageUrl);
			Close(true);
		}
	}

	[RelayCommand]
	private Task TryAgain() => CheckAsync();

	[RelayCommand]
	private void Done() => Close(true);
}

/// <summary>Where Check for Updates is.</summary>
public enum UpdateSheetState
{
	Checking,
	UpToDate,
	Newer,
	Failed,
}

/// <summary>What a dropped spreadsheet holds, chosen in <see cref="SpreadsheetDropSheetViewModel"/>.</summary>
public enum SpreadsheetUse
{
	None,

	/// <summary>Import Inspections or Parcels.</summary>
	InspectionsOrParcels,

	/// <summary>Enterprise Import: structures from a parcel spreadsheet.</summary>
	StructuresFromParcels,

	/// <summary>Field Work's coordinates import.</summary>
	Coordinates,

	/// <summary>The open assessment's flood depth from high-water marks.</summary>
	HighWaterMarks,
}

/// <summary>One choice in the spreadsheet chooser.</summary>
public sealed partial class SpreadsheetUseChoice(SpreadsheetUse use, string title, string tip) : ObservableObject
{
	public SpreadsheetUse Use { get; } = use;

	public string Title { get; } = title;

	/// <summary>Where the file goes, as the choice's tooltip.</summary>
	public string Tip { get; } = tip;

	[ObservableProperty]
	public partial bool IsChosen { get; set; }
}

/// <summary>
/// A spreadsheet dropped on the window could hold several things, so this asks: inspections or parcels, structures from
/// a parcel spreadsheet, coordinates (a .xlsx) or high-water marks (a .csv, while an assessment is open). Closes with
/// the <see cref="SpreadsheetUse"/> chosen.
/// </summary>
public sealed partial class SpreadsheetDropSheetViewModel : SheetViewModel
{
	public SpreadsheetDropSheetViewModel(string path, bool includeCoordinates, bool includeHighWaterMarks) : base("What does this spreadsheet hold?")
	{
		FileName = Path.GetFileName(path);
		List<SpreadsheetUseChoice> choices = new List<SpreadsheetUseChoice>
		{
			new(SpreadsheetUse.InspectionsOrParcels, "Inspections or parcels", "Opens Import Inspections or Parcels with this file."),
			new(SpreadsheetUse.StructuresFromParcels, "Structures from a parcel spreadsheet", "Opens Enterprise Import on the Import tab with this file."),
		};
		if (includeCoordinates)
		{
			choices.Add(new(SpreadsheetUse.Coordinates, "Coordinates", "Reads the coordinates spreadsheet on the Field Work tab. Nothing is saved until you apply it."));
		}
		if (includeHighWaterMarks)
		{
			choices.Add(new(SpreadsheetUse.HighWaterMarks, "High-water marks", "Opens Flood Depth for the open assessment with these marks."));
		}
		choices[0].IsChosen = true;
		Choices = choices;
	}

	public override SheetSize Size => SheetSize.Small;

	/// <summary>The dropped file's name, under the title.</summary>
	public string FileName { get; }

	public IReadOnlyList<SpreadsheetUseChoice> Choices { get; }

	/// <summary>The choice ticked now.</summary>
	public SpreadsheetUse Chosen => Choices.FirstOrDefault(c => c.IsChosen)?.Use ?? SpreadsheetUse.None;

	[RelayCommand]
	private void Continue()
	{
		if (Chosen != SpreadsheetUse.None)
		{
			Close(Chosen);
		}
	}
}
