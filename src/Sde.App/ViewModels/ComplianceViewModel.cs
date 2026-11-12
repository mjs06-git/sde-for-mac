using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Controls;
using Sde.App.Services;
using Sde.App.ViewModels.Sheets;
using Sde.Core.Assessments;
using Sde.Data;
using Sde.Reports;
using Sde.Reports.Letters;

namespace Sde.App.ViewModels;

/// <summary>One compliance case in the list, with its structure.</summary>
public sealed class ComplianceRowViewModel(ComplianceRow row)
{
	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	public ComplianceRow Row { get; } = row;

	public ComplianceCaseRecord Case => Row.Case;

	public string Address => string.IsNullOrWhiteSpace(Row.Address) ? "(no address)" : Row.Address;

	public string Owner => Row.Owner;

	/// <summary>SD (substantial damage) or SI (substantial improvement).</summary>
	public string Kind => Case.Kind ?? "SD";

	public string KindTip => Kind == "SI" ? "Substantial improvement" : "Substantial damage";

	public string PathText => Case.Path == null ? "Not chosen yet" : CompliancePaths.Label(Case.Path);

	public string Permit => Case.PermitNumber ?? "";

	public string DeadlineText => Case.Deadline?.ToString("MMM d, yyyy", Us) ?? "";

	/// <summary>The deadline for sorting (cases with none last).</summary>
	public DateTime DeadlineSort => Case.Deadline ?? DateTime.MaxValue;

	public bool IsOverdue => Row.IsOverdue;

	/// <summary>"12 days overdue".</summary>
	public string OverdueText => !IsOverdue ? null : Row.DaysOverdue == 1 ? "1 day overdue" : $"{Row.DaysOverdue:N0} days overdue";

	/// <summary>"12 days late", short enough for the table's deadline cell (the inspector says "overdue").</summary>
	public string LateText => !IsOverdue ? null : Row.DaysOverdue == 1 ? "1 day late" : $"{Row.DaysOverdue:N0} days late";

	/// <summary>"Paid · $30,000".</summary>
	public string IccText => IccStatuses.Label(Case.IccStatus) + (Case.IccAmount is decimal m ? " · " + m.ToString("C0", Us) : "");

	public string EcText => Case.EcReceivedOn?.ToString("MMM d, yyyy", Us) ?? "";

	public DateTime EcSort => Case.EcReceivedOn ?? DateTime.MaxValue;

	public string FinalText => Case.FinalInspectionOn?.ToString("MMM d, yyyy", Us) ?? "";

	public DateTime FinalSort => Case.FinalInspectionOn ?? DateTime.MaxValue;

	public bool IsOpen => Case.IsOpen;

	public string StatusText => IsOpen ? "Open" : "Closed";

	public Tone StatusTone => IsOpen ? Tone.Accent : Tone.Neutral;

	public string AccessibleOpen => "Open the case for " + Address;

	/// <summary>"Substantial damage", for the inspector.</summary>
	public string KindText => KindTip;

	/// <summary>"Oct 30, 2026", or "None set".</summary>
	public string DeadlineOrNone => Case.Deadline == null ? "None set" : DeadlineText;

	/// <summary>"Nov 8, 2026", or "Not received".</summary>
	public string EcOrNone => Case.EcReceivedOn == null ? "Not received" : EcText;

	/// <summary>"Nov 8, 2026", or "Not done".</summary>
	public string FinalOrNone => Case.FinalInspectionOn == null ? "Not done" : FinalText;

	public bool HasAssessment => Case.AssessmentID != null;

	/// <summary>True for an open case due from <paramref name="today"/> to <paramref name="days"/> days later (the Due soon filter).</summary>
	public bool IsDueSoon(DateTime today, int days) =>
		Case.IsOpen && Case.Deadline is DateTime d && d.Date >= today.Date && d.Date <= today.Date.AddDays(days);
}

/// <summary>
/// The Compliance page: the compliance cases of the substantially damaged (and substantially improved) structures in
/// the chosen event, with overdue, path, ICC and open or closed filters, opening cases one by one or in bulk, the case
/// sheet, and the PDF and CSV. Its sidebar badge counts open cases past their deadline.
/// </summary>
public sealed partial class ComplianceViewModel : ViewModelBase
{
	public static IReadOnlyList<string> ShowChoices { get; } = new[] { "Open cases", "Closed cases", "Every case" };

	/// <summary>"Any path", "Not chosen yet", then each path.</summary>
	public static IReadOnlyList<string> PathChoices { get; } = new[] { "Any path", CompliancePaths.Label(null) }.Concat(CompliancePaths.All.Select(CompliancePaths.Label)).ToList();

	/// <summary>"Any ICC status", "In progress (filed or approved)", then each status.</summary>
	public static IReadOnlyList<string> IccChoices { get; } = new[] { "Any ICC status", "In progress (filed or approved)" }.Concat(IccStatuses.All.Select(IccStatuses.Label)).ToList();

	private const int IccInProgress = 1;
	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	private readonly AppServices _app;
	private readonly MainViewModel _main;
	private List<ComplianceRowViewModel> _all = new();
	private List<Guid> _missing = new();
	private int _iccPaidCount;

	public ComplianceViewModel(AppServices app, MainViewModel main)
	{
		_app = app;
		_main = main;
	}

	/// <summary>Set by the window while this is the page showing (it then reloads when the data changes).</summary>
	public bool IsActive { get; set; }

	public ObservableCollection<ComplianceRowViewModel> Rows { get; } = new();

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(OpenSelectedCommand), nameof(OpenSelectedAssessmentCommand))]
	[NotifyPropertyChangedFor(nameof(HasSelection))]
	public partial ComplianceRowViewModel Selected { get; set; }

	partial void OnSelectedChanged(ComplianceRowViewModel value) => LoadTimeline();

	public bool HasSelection => Selected != null;

	/// <summary>The selected case's timeline, newest first, for the inspector.</summary>
	public ObservableCollection<ComplianceEntryViewModel> SelectedTimeline { get; } = new();

	public bool HasTimeline => SelectedTimeline.Count > 0;

	/// <summary>The three compliance letters, written for the selected case from the inspector.</summary>
	public IReadOnlyList<ComplianceLetterChoice> Letters => _letters ??= LetterKinds.Compliance
		.Select(k => new ComplianceLetterChoice(k, LetterLabel(k), WriteLetterCommand)).ToList();

	/// <summary>The letters' names, as the case sheet shows them.</summary>
	private static string LetterLabel(string kind) => kind switch
	{
		LetterKinds.ComplianceOptions => "Options and Deadline",
		LetterKinds.IccSupport => "ICC Support",
		_ => "Deadline Reminder",
	};

	private IReadOnlyList<ComplianceLetterChoice> _letters;

	/// <summary>The page's shell, for the shared inspector width and the inspector switch.</summary>
	public MainViewModel Shell => _main;

	/// <summary>How many days ahead Due soon looks (both ends included, as the sidebar's Agenda count does).</summary>
	public int DueSoonDays { get; set; } = 14;

	/// <summary>Only open cases due within <see cref="DueSoonDays"/> days (and not overdue yet).</summary>
	[ObservableProperty]
	public partial bool DueSoonOnly { get; set; }

	/// <summary>Open cases due soon in scope, counted by the store as the Agenda and the sidebar count them.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(DueSoonChipText), nameof(OverdueChipText))]
	public partial int DueSoonCount { get; private set; }

	/// <summary>"Overdue 1", on the filter chip.</summary>
	public string OverdueChipText => $"Overdue {OverdueCount:N0}";

	/// <summary>"Due soon 2", on the filter chip.</summary>
	public string DueSoonChipText => $"Due soon {DueSoonCount:N0}";

	/// <summary>Today, for overdue and due soon: the store's clock, which tests can set.</summary>
	private DateTime Today => _app.Store.Clock().Date;

	/// <summary>Open, closed or every case (an index into <see cref="ShowChoices"/>).</summary>
	[ObservableProperty]
	public partial int ShowFilter { get; set; }

	[ObservableProperty]
	public partial int PathFilter { get; set; }

	[ObservableProperty]
	public partial int IccFilter { get; set; }

	[ObservableProperty]
	public partial bool OverdueOnly { get; set; }

	partial void OnShowFilterChanged(int value) => Fill();

	partial void OnPathFilterChanged(int value) => Fill();

	partial void OnIccFilterChanged(int value) => Fill();

	partial void OnOverdueOnlyChanged(bool value)
	{
		if (value)
		{
			DueSoonOnly = false;
		}
		Fill();
	}

	partial void OnDueSoonOnlyChanged(bool value)
	{
		if (value)
		{
			OverdueOnly = false;
		}
		Fill();
	}

	// ───────────── Headline numbers ─────────────

	[ObservableProperty]
	public partial int OpenCount { get; private set; }

	[ObservableProperty]
	public partial string ClosedCaption { get; private set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(OverdueChipText))]
	public partial int OverdueCount { get; private set; }

	[ObservableProperty]
	public partial int IccInProgressCount { get; private set; }

	[ObservableProperty]
	public partial string IccPaidText { get; private set; }

	[ObservableProperty]
	public partial string IccPaidCaption { get; private set; }

	/// <summary>Substantially damaged structures in scope that have no compliance case yet.</summary>
	public int MissingCount => _missing.Count;

	public bool HasMissing => _missing.Count > 0;

	/// <summary>The banner above the list (when the list is empty, its empty state offers the same).</summary>
	public bool ShowMissingBanner => HasMissing && !IsEmpty;

	public string MissingText => _missing.Count == 1
		? "1 substantially damaged structure has no compliance case yet."
		: $"{_missing.Count:N0} substantially damaged structures have no compliance case yet.";

	/// <summary>What the last export or bulk action did.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasResult))]
	public partial string LastResult { get; private set; }

	public bool HasResult => LastResult != null;

	public string Subtitle => _app.Scope.IsAll
		? "Follow up on substantially damaged and improved structures: the owner's path, permits, deadlines, ICC claims and final inspections."
		: $"Follow up on the substantially damaged structures in {_app.Scope.Event.Label}.";

	public string CountText => Rows.Count == 1 ? "1 case" : $"{Rows.Count:N0} cases";

	/// <summary>
	/// The status bar under the list: "2 cases · 1 overdue · 1 ICC claim in progress · $12,000 ICC paid", and how many
	/// substantially damaged structures still need a case.
	/// </summary>
	public string StatusText => string.Join(" · ", new[]
	{
		CountText,
		$"{OverdueCount:N0} overdue",
		IccInProgressCount == 0 ? null : IccInProgressCount == 1 ? "1 ICC claim in progress" : $"{IccInProgressCount:N0} ICC claims in progress",
		_iccPaidCount == 0 ? null : $"{IccPaidText} ICC paid",
		_missing.Count == 0 ? null : _missing.Count == 1 ? "1 structure without a case" : $"{_missing.Count:N0} structures without a case",
	}.Where(s => s != null));

	/// <summary>The filters on besides open or closed ("Overdue, Elevate, ICC paid"), for the toolbar; null when none.</summary>
	public string FilterSummary
	{
		get
		{
			List<string> on = new();
			if (OverdueOnly)
			{
				on.Add("Overdue");
			}
			if (DueSoonOnly)
			{
				on.Add("Due soon");
			}
			if (PathFilter == 1)
			{
				on.Add("No path chosen");
			}
			else if (PathFilter > 1 && PathFilter < PathChoices.Count)
			{
				on.Add(PathChoices[PathFilter]);
			}
			if (IccFilter == IccInProgress)
			{
				on.Add("ICC in progress");
			}
			else if (IccFilter > IccInProgress && IccFilter < IccChoices.Count)
			{
				on.Add("ICC " + IccChoices[IccFilter].ToLowerInvariant());
			}
			return on.Count == 0 ? null : string.Join(", ", on);
		}
	}

	/// <summary>True while a path, ICC or overdue filter is on (the toolbar's filter button then names them).</summary>
	public bool HasFilters => FilterSummary != null;

	public bool HasRows => Rows.Count > 0;

	/// <summary>No cases at all in scope.</summary>
	public bool IsEmpty => _all.Count == 0;

	public bool NoMatches => _all.Count > 0 && Rows.Count == 0;

	public string EmptyText => HasMissing
		? "Open a case for each substantially damaged structure."
		: "Cases appear here once opened, from the editor or the button above.";

	/// <summary>Every case in scope: all of them, or for an event its assessments' cases and the SI cases of its structures.</summary>
	public static List<ComplianceCaseRecord> CasesInScope(AppServices app)
	{
		List<ComplianceCaseRecord> cases = app.Store.GetActiveComplianceCases();
		if (app.Scope.IsAll)
		{
			return cases;
		}
		HashSet<Guid> structures = app.Store.PropertiesInEvent(app.Scope.EventId!.Value);
		return cases.Where(c => c.AssessmentID is Guid a ? app.Scope.Includes(a) : structures.Contains(c.PropertyID)).ToList();
	}

	/// <summary>The sidebar's badge: open cases in scope past their deadline.</summary>
	public static int CountOverdue(AppServices app) => CasesInScope(app).Count(c => c.IsOverdue(app.Store.Clock().Date));

	/// <summary>Reads the cases again, and which substantially damaged structures have none.</summary>
	public void Reload()
	{
		Guid? selected = Selected?.Case.CaseID;
		List<ComplianceCaseRecord> cases = CasesInScope(_app);
		_all = ComplianceReport.Rows(_app.Store, Today, cases).Select(r => new ComplianceRowViewModel(r)).ToList();
		DueSoonCount = CountDueSoon(_app, DueSoonDays);
		_missing = MissingCases(_app.Store.GetComplianceCases());

		List<ComplianceCaseRecord> open = cases.Where(c => c.IsOpen).ToList();
		OpenCount = open.Count;
		int closed = cases.Count - open.Count;
		ClosedCaption = closed == 1 ? "1 closed" : $"{closed:N0} closed";
		OverdueCount = open.Count(c => c.IsOverdue(Today));
		IccInProgressCount = cases.Count(c => c.IccStatus is IccStatuses.Filed or IccStatuses.Approved);
		List<ComplianceCaseRecord> paid = cases.Where(c => c.IccStatus == IccStatuses.Paid).ToList();
		_iccPaidCount = paid.Count;
		IccPaidText = paid.Sum(c => c.IccAmount ?? 0m).ToString("C0", Us);
		IccPaidCaption = paid.Count == 1 ? "1 claim paid" : $"{paid.Count:N0} claims paid";
		Fill();
		Selected = selected is Guid id ? Rows.FirstOrDefault(r => r.Case.CaseID == id) : null;
		OnPropertyChanged(nameof(Subtitle));
		OnPropertyChanged(nameof(MissingCount));
		OnPropertyChanged(nameof(HasMissing));
		OnPropertyChanged(nameof(ShowMissingBanner));
		OnPropertyChanged(nameof(MissingText));
		OnPropertyChanged(nameof(EmptyText));
	}

	/// <summary>
	/// The newest assessment in scope of each structure that is substantially damaged, for structures with no
	/// substantial damage case yet (open or closed).
	/// </summary>
	private List<Guid> MissingCases(List<ComplianceCaseRecord> allCases)
	{
		HashSet<Guid> withCase = allCases.Where(c => c.Kind != "SI").Select(c => c.PropertyID).ToHashSet();
		return _app.Store.ListStructures()
			.Where(s => s.AssessmentID != null && _app.Scope.Includes(s.AssessmentID) && !withCase.Contains(s.PropertyID))
			.GroupBy(s => s.PropertyID)
			.Select(g => g.OrderByDescending(s => s.DateofInspection).ThenByDescending(s => s.LastUpdate).First())
			.Where(s => Determinations.Classify(s.DamageMessage, s.PercentDamage) == DeterminationClass.Substantial)
			.Select(s => s.AssessmentID!.Value)
			.ToList();
	}

	private void Fill()
	{
		Rows.Clear();
		foreach (ComplianceRowViewModel r in _all.Where(Matches))
		{
			Rows.Add(r);
		}
		OnPropertyChanged(nameof(HasRows));
		OnPropertyChanged(nameof(IsEmpty));
		OnPropertyChanged(nameof(NoMatches));
		OnPropertyChanged(nameof(CountText));
		OnPropertyChanged(nameof(StatusText));
		OnPropertyChanged(nameof(FilterSummary));
		OnPropertyChanged(nameof(HasFilters));
	}

	private bool Matches(ComplianceRowViewModel r)
	{
		ComplianceCaseRecord c = r.Case;
		bool show = ShowFilter switch
		{
			0 => c.IsOpen,
			1 => !c.IsOpen,
			_ => true,
		};
		bool path = PathFilter switch
		{
			0 => true,
			1 => c.Path == null,
			_ => c.Path == CompliancePaths.All[PathFilter - 2],
		};
		string icc = c.IccStatus ?? IccStatuses.NotFiled;
		bool iccMatches = IccFilter switch
		{
			0 => true,
			IccInProgress => icc is IccStatuses.Filed or IccStatuses.Approved,
			_ => icc == IccStatuses.All[IccFilter - 2],
		};
		return show && path && iccMatches && (!OverdueOnly || r.IsOverdue) && (!DueSoonOnly || r.IsDueSoon(Today, DueSoonDays));
	}

	/// <summary>Open cases in scope due within <paramref name="days"/> days, as the store counts them for the Agenda and the sidebar.</summary>
	public static int CountDueSoon(AppServices app, int days) => app.Store.GetDueSoonCount(days, app.Store.Clock().Date, app.Scope.EventId);

	/// <summary>The selected case's timeline, newest first.</summary>
	private void LoadTimeline()
	{
		SelectedTimeline.Clear();
		if (Selected != null)
		{
			try
			{
				foreach (ComplianceEntryRecord e in _app.Store.GetComplianceEntries(Selected.Case.CaseID).AsEnumerable().Reverse())
				{
					SelectedTimeline.Add(new ComplianceEntryViewModel(e));
				}
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				AppLog.Write("Couldn't read a compliance case's timeline", ex);
			}
		}
		OnPropertyChanged(nameof(HasTimeline));
	}

	/// <summary>The Due soon chip: open cases due within two weeks (again to show every open case).</summary>
	[RelayCommand]
	private void ToggleDueSoon()
	{
		bool on = !DueSoonOnly;
		ClearFilters();
		DueSoonOnly = on;
	}

	/// <summary>Opens the selected case's assessment; closing it comes back here.</summary>
	[RelayCommand(CanExecute = nameof(CanOpenSelectedAssessment))]
	private void OpenSelectedAssessment()
	{
		if (Selected?.Case.AssessmentID is Guid id)
		{
			_main.OpenAssessmentFrom(id, Page.Compliance);
		}
	}

	private bool CanOpenSelectedAssessment() => Selected?.HasAssessment == true;

	/// <summary>Writes one of the compliance letters for the selected case, as the case sheet does, and adds it to the timeline.</summary>
	[RelayCommand]
	private async Task WriteLetter(string kind)
	{
		if (Selected == null || kind == null)
		{
			return;
		}
		ComplianceCaseSheetViewModel letters;
		try
		{
			letters = new ComplianceCaseSheetViewModel(_app, Selected.Case.CaseID, id => _main.OpenAssessmentFrom(id, Page.Compliance));
		}
		catch (ArgumentException ex)
		{
			_app.Dialogs.Notify("Not found", ex.Message, isError: true);
			Reload();
			return;
		}
		await letters.WriteLetterCommand.ExecuteAsync(kind);
		if (letters.LetterText != null)
		{
			LastResult = letters.LetterText;
		}
		LoadTimeline();
	}

	// ───────────── Filters and the headline tiles ─────────────

	/// <summary>The Open tile: every open case.</summary>
	[RelayCommand]
	private void ShowOpen()
	{
		ClearFilters();
		ShowFilter = 0;
	}

	/// <summary>The Overdue tile: open cases past their deadline (again to show every open case).</summary>
	[RelayCommand]
	private void ToggleOverdue()
	{
		bool on = !OverdueOnly;
		ClearFilters();
		OverdueOnly = on;
	}

	/// <summary>The ICC in progress tile: claims filed or approved and not paid yet.</summary>
	[RelayCommand]
	private void ShowIccInProgress()
	{
		ClearFilters();
		ShowFilter = 2;
		IccFilter = IccInProgress;
	}

	/// <summary>The ICC paid tile: the cases whose claims were paid, open or closed.</summary>
	[RelayCommand]
	private void ShowIccPaid()
	{
		ClearFilters();
		ShowFilter = 2;
		IccFilter = 2 + IccStatuses.All.ToList().IndexOf(IccStatuses.Paid);
	}

	[RelayCommand]
	private void ClearFilters()
	{
		ShowFilter = 0;
		PathFilter = 0;
		IccFilter = 0;
		OverdueOnly = false;
		DueSoonOnly = false;
	}

	// ───────────── Cases ─────────────

	/// <summary>Opens a case in its sheet; the list (and the sidebar badge) catch up when it closes.</summary>
	[RelayCommand]
	private async Task OpenCase(ComplianceRowViewModel row)
	{
		if (row == null)
		{
			return;
		}
		ComplianceCaseSheetViewModel sheet;
		try
		{
			sheet = new ComplianceCaseSheetViewModel(_app, row.Case.CaseID, id => _main.OpenAssessmentFrom(id, Page.Compliance));
		}
		catch (ArgumentException ex)
		{
			_app.Dialogs.Notify("Not found", ex.Message, isError: true);
			Reload();
			return;
		}
		await _app.Sheets.ShowAsync(sheet);
		_app.RaiseDataChanged();
	}

	[RelayCommand(CanExecute = nameof(CanOpenSelected))]
	private Task OpenSelected() => OpenCase(Selected);

	private bool CanOpenSelected() => Selected != null;

	/// <summary>
	/// Opens a case for each substantially damaged structure in scope that has none yet (for its newest assessment in
	/// scope), and says how many were opened.
	/// </summary>
	[RelayCommand]
	private async Task OpenMissingCases()
	{
		Reload();
		if (_missing.Count == 0)
		{
			_app.Dialogs.Notify("Nothing to open", "Every substantially damaged structure here already has a compliance case.");
			return;
		}
		string these = _missing.Count == 1 ? "1 substantially damaged structure" : $"{_missing.Count:N0} substantially damaged structures";
		if (!await _app.Dialogs.ConfirmAsync("Open compliance cases?",
			$"Opens a compliance case for {these} with none yet. Structures that already have one are left as they are.", "Open cases"))
		{
			return;
		}
		int opened = 0;
		List<string> problems = new();
		foreach (Guid id in _missing)
		{
			try
			{
				_app.Store.OpenComplianceCase(id);
				opened++;
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				problems.Add(ErrorPresenter.Describe(null, ex).Message);
			}
		}
		LastResult = (opened == 1 ? "Opened 1 compliance case." : $"Opened {opened:N0} compliance cases.")
			+ (problems.Count == 0 ? "" : " Not opened: " + string.Join(" ", problems.Distinct()));
		_app.RaiseDataChanged();
		if (!IsActive)
		{
			Reload();
		}
	}

	// ───────────── Exports ─────────────

	/// <summary>The cases shown, as the compliance PDF (headline numbers, then open and closed cases), opened in Preview.</summary>
	[RelayCommand]
	private async Task ExportPdf()
	{
		if (Rows.Count == 0)
		{
			_app.Dialogs.Notify("Nothing to export", "No cases match the filters.");
			return;
		}
		string path = await _app.Dialogs.SaveFileAsync("Save the compliance report", FileName(".pdf"), "PDF document", "pdf");
		if (string.IsNullOrEmpty(path))
		{
			return;
		}
		List<ComplianceRow> rows = Rows.Select(r => r.Row).ToList();
		LetterSender sender = _app.Store.GetSetting<LetterSender>(LetterSender.SettingKey);
		ComplianceReportOptions options = new ComplianceReportOptions { CommunityName = sender?.CommunityName, EventName = _app.Scope.Event?.Label };
		JobOutcome<bool> result = await _app.Jobs.RunAsync("Writing the compliance report", (progress, ct) =>
		{
			ComplianceReport.RenderToFile(rows, options, path);
			return true;
		}, "Couldn't write the compliance report");
		if (result.Completed)
		{
			LastResult = $"Saved {Path.GetFileName(path)} ({CountText}).";
			_app.Dialogs.Open(path);
		}
	}

	/// <summary>The cases shown, as a CSV for a spreadsheet.</summary>
	[RelayCommand]
	private async Task ExportCsv()
	{
		if (Rows.Count == 0)
		{
			_app.Dialogs.Notify("Nothing to export", "No cases match the filters.");
			return;
		}
		string path = await _app.Dialogs.SaveFileAsync("Save the compliance list", FileName(".csv"), "CSV file", "csv");
		if (string.IsNullOrEmpty(path))
		{
			return;
		}
		try
		{
			ComplianceReport.WriteCsv(Rows.Select(r => r.Row).ToList(), path);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't write the compliance list", ex);
			return;
		}
		LastResult = $"Saved {Path.GetFileName(path)} ({CountText}).";
		_app.Dialogs.Reveal(path);
	}

	/// <summary>"Compliance (September flood).pdf", or "Compliance.pdf" for all events.</summary>
	private string FileName(string extension)
	{
		string name = _app.Scope.IsAll ? "Compliance" : "Compliance (" + _app.Scope.Event.Label + ")";
		return string.Concat((name + extension).Split(Path.GetInvalidFileNameChars())).Replace(':', '-');
	}
}
