using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Sheets;
using Sde.Core.Assessments;
using Sde.Core.Improvements;
using Sde.Data;
using Sde.Reports;

namespace Sde.App.ViewModels;

/// <summary>A month heading in the case file's timeline ("September 2026").</summary>
public sealed class CaseFileMonthViewModel(string title)
{
	public string Title { get; } = title;
}

/// <summary>One line of the case file's timeline: when, what, more detail and who, with an icon for what kind of thing it was.</summary>
public sealed class CaseFileEntryViewModel
{
	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	public CaseFileEntryViewModel(CaseFileEntry entry, IRelayCommand<CaseFileEntryViewModel> open)
	{
		Entry = entry;
		Open = open;
	}

	public CaseFileEntry Entry { get; }

	/// <summary>Opens the entry's assessment (set when it has one).</summary>
	public IRelayCommand<CaseFileEntryViewModel> Open { get; }

	/// <summary>"Sep 21".</summary>
	public string DateText => Entry.When.ToString("MMM d", Us);

	/// <summary>"8:05 AM", or empty for an entry that only has a day.</summary>
	public string TimeText => Entry.IsDateOnly ? "" : Entry.When.ToString("h:mm tt", Us);

	public bool HasTime => !Entry.IsDateOnly;

	public string Title => Entry.Title;

	public string Detail => Entry.Detail;

	public bool HasDetail => !string.IsNullOrWhiteSpace(Entry.Detail);

	public string Who => Entry.Who;

	public bool HasWho => !string.IsNullOrWhiteSpace(Entry.Who);

	public bool IsEdit => Entry.IsEdit;

	/// <summary>True when the entry belongs to an assessment that can be opened from here.</summary>
	public bool CanOpen { get; init; }

	/// <summary>The icon on the timeline's rail, by what kind of thing happened.</summary>
	public string Icon => Entry.Kind switch
	{
		CaseFileEntryKind.Inspection => "Clipboard",
		CaseFileEntryKind.Status => "CheckCircle",
		CaseFileEntryKind.EventMembership => "Event",
		CaseFileEntryKind.LetterWritten or CaseFileEntryKind.LetterSent => "Letter",
		CaseFileEntryKind.AppealReceived or CaseFileEntryKind.AppealDecided => "Flag",
		CaseFileEntryKind.ContractorBid => "Cost",
		CaseFileEntryKind.ImprovementProject => "Building",
		CaseFileEntryKind.SiDetermination => "Result",
		CaseFileEntryKind.Document => "Attachment",
		CaseFileEntryKind.Comment => "Comment",
		CaseFileEntryKind.Compliance => "Compliance",
		CaseFileEntryKind.Merge => "Copy",
		CaseFileEntryKind.RecentlyDeleted => "Trash",
		CaseFileEntryKind.Edit => "Edit",
		_ => "History",
	};

	/// <summary>What VoiceOver reads for the line.</summary>
	public string AccessibleName => string.Join(", ", new[]
	{
		Entry.When.ToString(Entry.IsDateOnly ? "MMMM d, yyyy" : "MMMM d, yyyy, h:mm tt", Us), Title, Detail, Who,
	}.Where(x => !string.IsNullOrWhiteSpace(x)));
}

/// <summary>One assessment in the case file's Assessments card: event, date, status, percent with its gauge and result.</summary>
public sealed class CaseFileAssessmentRowViewModel
{
	public CaseFileAssessmentRowViewModel(CaseFileAssessment a, bool isLatest, IRelayCommand<CaseFileAssessmentRowViewModel> open)
	{
		Source = a;
		IsLatest = isLatest;
		Open = open;
		State = CaseFileViewModel.StateOf(a.Assessment);
	}

	public CaseFileAssessment Source { get; }

	public Guid Id => Source.Assessment.AssessmentID;

	public IRelayCommand<CaseFileAssessmentRowViewModel> Open { get; }

	/// <summary>The assessment whose result is the structure's current result.</summary>
	public bool IsLatest { get; }

	public bool IsTrashed => Source.IsTrashed;

	/// <summary>Assessments in Recently deleted are listed but not opened from here.</summary>
	public bool CanOpen => !Source.IsTrashed;

	public string EventText => Source.Event?.Label ?? "No event";

	public string DateText => Source.Assessment.DateofInspection?.ToString("MMM d, yyyy", CultureInfo.GetCultureInfo("en-US")) ?? "No inspection date";

	public string StatusText => Source.IsTrashed ? "In Recently deleted" : AssessmentStatuses.Label(Source.Status.Value);

	/// <summary>"Sep 21, 2026 · Final" under the event.</summary>
	public string Detail => DateText + " · " + StatusText;

	public ResultState State { get; }

	public bool HasPercent => State is not (ResultState.Incomplete or ResultState.Undetermined);

	public string PercentText => HasPercent ? Source.Assessment.PercentDamage?.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "–";

	public double PercentValue => HasPercent ? (double)Math.Clamp(Source.Assessment.PercentDamage ?? 0m, 0m, 100m) : 0;

	public string Result => Source.Assessment.DamageMessage ?? "Data Entry Incomplete";

	public string AccessibleName => string.Join(", ", EventText, DateText, StatusText, HasPercent ? PercentText + " damaged" : null, Result);
}

/// <summary>One line in a case file card (a letter, an appeal, a compliance case, a document...): a title, a detail line and a figure or date on the right.</summary>
public sealed record CaseFileLine(string Title, string Detail, string Trailing)
{
	public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

	public string AccessibleName => string.Join(", ", new[] { Title, Detail, Trailing }.Where(x => !string.IsNullOrWhiteSpace(x)));
}

/// <summary>
/// The case file page: everything on record about one structure across every event. A dark hero band names the structure
/// and shows its current result; under it the timeline (edits hidden unless "Show edits" is on) and a column of cards:
/// assessments, compliance, letters, appeals and bids, improvements and SI determinations, documents and photos. Empty
/// cards are left out. Read from <see cref="SdeStore.GetCaseFile"/> each time the page shows.
/// </summary>
public sealed partial class CaseFileViewModel : ViewModelBase
{
	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	private readonly AppServices _app;
	private readonly MainViewModel _main;

	public CaseFileViewModel(AppServices app, MainViewModel main, Guid propertyId)
	{
		_app = app;
		_main = main;
		PropertyId = propertyId;
		_app.DataChanged += OnDataChanged;
	}

	public Guid PropertyId { get; }

	/// <summary>What was read, or null before <see cref="Load"/>.</summary>
	[ObservableProperty]
	public partial CaseFile File { get; private set; }

	/// <summary>Reads the case file. False (after saying why) when the structure is gone or in Recently deleted.</summary>
	public bool Load()
	{
		try
		{
			Apply(_app.Store.GetCaseFile(PropertyId));
			return true;
		}
		catch (ArgumentException ex)
		{
			_app.Dialogs.Notify("No case file", ex.Message, isError: true);
			return false;
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't read the case file", ex);
			return false;
		}
	}

	/// <summary>Reads the case file again (the page showed again, or something was saved). Leaves the page when the structure is gone.</summary>
	public void Reload()
	{
		if (!Load() && _main.ActivePage == Page.CaseFile && _main.CaseFilePage == this)
		{
			_main.CloseCaseFile();
		}
	}

	/// <summary>Stops listening for saves (the page was closed).</summary>
	public void Detach() => _app.DataChanged -= OnDataChanged;

	private void OnDataChanged()
	{
		if (_main.ActivePage == Page.CaseFile && _main.CaseFilePage == this)
		{
			Reload();
		}
	}

	// ───────────── The hero band ─────────────

	public string Address => File?.Address ?? "";

	public string CityStateZip => File?.CityStateZip ?? "";

	public bool HasCityStateZip => !string.IsNullOrWhiteSpace(File?.CityStateZip);

	public string Owner => string.IsNullOrWhiteSpace(File?.Owner) ? "Not recorded" : File.Owner;

	public string Parcel => string.IsNullOrWhiteSpace(File?.ParcelNumber) ? "Not recorded" : File.ParcelNumber;

	public string Community => string.Join(" ", new[] { File?.CommunityId, File?.CommunityName }.Where(x => !string.IsNullOrWhiteSpace(x))) is string c && c.Length > 0 ? c : "Not recorded";

	private CaseFileAssessment Latest => File?.Latest;

	public bool HasLatest => Latest != null;

	public ResultState LatestState => StateOf(Latest?.Assessment);

	public bool HasPercent => LatestState is not (ResultState.Incomplete or ResultState.Undetermined);

	public string PercentText => HasPercent ? Latest.Assessment.PercentDamage?.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "";

	public double PercentValue => HasPercent ? (double)Math.Clamp(Latest.Assessment.PercentDamage ?? 0m, 0m, 100m) : 0;

	public string Verdict => Latest == null ? "No assessment" : Latest.Assessment.DamageMessage ?? "Data Entry Incomplete";

	public bool IsSubstantial => LatestState == ResultState.Substantial;

	public string LineText => HasPercent ? ResultLine.Describe(PercentValue) : "";

	/// <summary>"Inspected Sep 21, 2026 · September flood · Final", under the gauge.</summary>
	public string LatestText => Latest == null ? "" : string.Join(" · ", new[]
	{
		Latest.Assessment.DateofInspection is DateTime d ? "Inspected " + d.ToString("MMM d, yyyy", Us) : "No inspection date",
		Latest.Event?.Label,
		AssessmentStatuses.Label(Latest.Status.Value),
	}.Where(x => !string.IsNullOrWhiteSpace(x)));

	public bool HasCumulative => File?.Cumulative is CumulativeResult c && c.Status == CumulativeStatus.Evaluated;

	/// <summary>"Cumulative over 10 years: 82.5%, meets the threshold".</summary>
	public string CumulativeText
	{
		get
		{
			if (!HasCumulative)
			{
				return "";
			}
			CumulativeResult c = File.Cumulative;
			string period = c.LookbackYears == 0 ? "all events" : c.LookbackYears == 1 ? "1 year" : c.LookbackYears.ToString(Us) + " years";
			string total = c.TotalPercent is decimal tp ? tp.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "not worked out";
			string verdict = c.TotalPercent is null ? "" : c.MeetsThreshold ? ", meets the threshold" : ", below the threshold";
			return $"Cumulative over {period}: {total}{verdict}";
		}
	}

	public bool CumulativeMeetsThreshold => HasCumulative && File.Cumulative.MeetsThreshold;

	// ───────────── The timeline ─────────────

	/// <summary>Field changes are listed too (off at first: they are many, and the PDF leaves them out unless asked).</summary>
	[ObservableProperty]
	public partial bool ShowEdits { get; set; }

	partial void OnShowEditsChanged(bool value) => BuildTimeline();

	/// <summary>Month headings and entries, newest first.</summary>
	public ObservableCollection<object> Timeline { get; } = new();

	public bool HasTimeline => Timeline.Count > 0;

	public int EditCount => File?.Timeline.Count(e => e.IsEdit) ?? 0;

	public bool HasEdits => EditCount > 0;

	/// <summary>The switch's tooltip: how many field changes it shows.</summary>
	public string EditsTip => EditCount == 1 ? "1 field change in the edit history" : $"{EditCount:N0} field changes in the edit history";

	private void BuildTimeline()
	{
		Timeline.Clear();
		if (File == null)
		{
			return;
		}
		HashSet<Guid> openable = File.Assessments.Where(a => !a.IsTrashed).Select(a => a.Assessment.AssessmentID).ToHashSet();
		string month = null;
		foreach (CaseFileEntry e in File.Timeline.Where(e => ShowEdits || !e.IsEdit))
		{
			string m = e.When.ToString("MMMM yyyy", Us);
			if (m != month)
			{
				Timeline.Add(new CaseFileMonthViewModel(m));
				month = m;
			}
			Timeline.Add(new CaseFileEntryViewModel(e, OpenEntryCommand) { CanOpen = e.AssessmentId is Guid id && openable.Contains(id) });
		}
		OnPropertyChanged(nameof(HasTimeline));
	}

	// ───────────── The cards ─────────────

	public IReadOnlyList<CaseFileAssessmentRowViewModel> Assessments { get; private set; } = Array.Empty<CaseFileAssessmentRowViewModel>();

	public bool HasAssessments => Assessments.Count > 0;

	/// <summary>Two or more assessments in use: they can be compared.</summary>
	public bool CanCompare => Assessments.Count(a => !a.IsTrashed) >= 2;

	public IReadOnlyList<CaseFileLine> Compliance { get; private set; } = Array.Empty<CaseFileLine>();

	public bool HasCompliance => Compliance.Count > 0;

	public IReadOnlyList<CaseFileLine> Letters { get; private set; } = Array.Empty<CaseFileLine>();

	public bool HasLetters => Letters.Count > 0;

	public IReadOnlyList<CaseFileLine> Appeals { get; private set; } = Array.Empty<CaseFileLine>();

	public bool HasAppeals => Appeals.Count > 0;

	public IReadOnlyList<CaseFileLine> Improvements { get; private set; } = Array.Empty<CaseFileLine>();

	public bool HasImprovements => Improvements.Count > 0;

	public IReadOnlyList<CaseFileLine> Documents { get; private set; } = Array.Empty<CaseFileLine>();

	public bool HasDocuments => Documents.Count > 0;

	public IReadOnlyList<CaseFileLine> Photos { get; private set; } = Array.Empty<CaseFileLine>();

	public bool HasPhotos => Photos.Count > 0;

	/// <summary>"3 photos" in the Photos card's heading.</summary>
	public string PhotosText { get; private set; } = "";

	private void Apply(CaseFile file)
	{
		File = file;
		Assessments = file.Assessments.Select(a => new CaseFileAssessmentRowViewModel(a, a == file.Latest, OpenAssessmentCommand)).ToList();
		Compliance = file.ComplianceCases.Select(c => c.Case).Select(c => new CaseFileLine(
			Join(" · ", c.Kind, c.Path == null ? "Path not chosen" : CompliancePaths.Label(c.Path)),
			Join(" · ", c.PermitNumber == null ? null : "Permit " + c.PermitNumber, c.Deadline is DateTime d ? "Deadline " + Date(d) : null),
			c.IsOpen ? "Open" : "Closed")).ToList();
		Dictionary<Guid, CaseFileAssessment> byId = file.Assessments.ToDictionary(a => a.Assessment.AssessmentID);
		string Of(Guid id) => byId.TryGetValue(id, out CaseFileAssessment a) ? Join(" · ", a.Event?.Label, Date(a.Assessment.DateofInspection)) : null;
		Letters = file.Letters.Select(l => new CaseFileLine(
			string.IsNullOrWhiteSpace(l.Kind) ? "Letter" : l.Kind.Trim(),
			Join(" · ", "Written " + Date(l.GeneratedOn), Of(l.AssessmentID)),
			l.SentOn is DateTime s ? "Sent " + Date(s) : "Not sent")).ToList();
		List<CaseFileLine> appeals = new();
		foreach (CaseFileAppeal ap in file.Appeals)
		{
			if (ap.Appeal is AppealRecord r)
			{
				string outcome = r.Outcome ?? AppealOutcomes.Pending;
				appeals.Add(new CaseFileLine("Appeal", Join(" · ", "Received " + Date(r.ReceivedOn), r.Basis), outcome));
			}
			foreach (ContractorBidRecord b in ap.Bids)
			{
				appeals.Add(new CaseFileLine("Bid: " + (string.IsNullOrWhiteSpace(b.Contractor) ? "No contractor named" : b.Contractor.Trim()),
					Join(" · ", Date(b.BidDate), b.Notes), b.Amount?.ToString("C0", Us) ?? ""));
			}
		}
		Appeals = appeals;
		Improvements = file.Improvements.Select(p => new CaseFileLine(
				string.IsNullOrWhiteSpace(p.Description) ? "Improvement project" : p.Description.Trim(),
				Join(" · ", p.PermitNumber == null ? null : "Permit " + p.PermitNumber, Date(p.ProjectDate), p.Included ? null : "Not counted"),
				p.Cost?.ToString("C0", Us) ?? ""))
			.Concat(file.SiDeterminations.Select(d => new CaseFileLine(
				"SI determination: " + (d.Result ?? "No result"),
				Join(" · ", Date(d.DeterminedOn), d.DeterminedBy),
				d.Percent is decimal pc ? pc.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "")))
			.ToList();
		Documents = file.Documents.Select(d => new CaseFileLine(DocumentsSheetViewModel.NameOf(d), DocumentsSheetViewModel.KindLabel(d.Kind), Date(d.AddedOn))).ToList();
		Photos = file.Assessments.Where(a => !a.IsTrashed && a.PhotoCount > 0).Select(a => new CaseFileLine(
			a.Event?.Label ?? "No event", Date(a.Assessment.DateofInspection), Count(a.PhotoCount, "photo"))).ToList();
		int photos = file.Assessments.Where(a => !a.IsTrashed).Sum(a => a.PhotoCount);
		PhotosText = Count(photos, "photo");
		BuildTimeline();
		OnPropertyChanged(string.Empty);
		CompareCommand.NotifyCanExecuteChanged();
		OpenLatestCommand.NotifyCanExecuteChanged();
	}

	/// <summary>How the result is coloured, by the rule in <see cref="Determinations"/>.</summary>
	public static ResultState StateOf(Sde.Core.Records.AssessmentRecord a) => a == null ? ResultState.Incomplete
		: Determinations.Classify(a.DamageMessage, a.PercentDamage) switch
		{
			DeterminationClass.Substantial => ResultState.Substantial,
			DeterminationClass.Close => ResultState.Close,
			DeterminationClass.NotSubstantial => ResultState.NotSubstantial,
			DeterminationClass.Undetermined => ResultState.Undetermined,
			_ => ResultState.Incomplete,
		};

	private static string Date(DateTime? d) => d?.ToString("MMM d, yyyy", Us) ?? "No date";

	private static string Join(string separator, params string[] parts) => string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));

	private static string Count(int n, string noun) => n == 1 ? "1 " + noun : $"{n:N0} {noun}s";

	// ───────────── Actions ─────────────

	[RelayCommand]
	private void OpenAssessment(CaseFileAssessmentRowViewModel row)
	{
		if (row != null && row.CanOpen)
		{
			_main.OpenAssessmentFromCaseFile(row.Id);
		}
	}

	[RelayCommand]
	private void OpenEntry(CaseFileEntryViewModel entry)
	{
		if (entry?.CanOpen == true && entry.Entry.AssessmentId is Guid id)
		{
			_main.OpenAssessmentFromCaseFile(id);
		}
	}

	private bool CanOpenLatest => Latest != null && !Latest.IsTrashed;

	/// <summary>The title band's Open Latest Assessment: the assessment whose result is the structure's current result.</summary>
	[RelayCommand(CanExecute = nameof(CanOpenLatest))]
	private void OpenLatest()
	{
		if (CanOpenLatest)
		{
			_main.OpenAssessmentFromCaseFile(Latest.Assessment.AssessmentID);
		}
	}

	/// <summary>Print Case File…: asks what to include, where to save the PDF, writes it and opens it.</summary>
	[RelayCommand]
	private async Task Print()
	{
		if (await _app.Sheets.ShowAsync(new PrintCaseFileSheetViewModel(EditCount)) is not CaseFileReportOptions options)
		{
			return;
		}
		string suggested = string.Concat(("Case file – " + Address + ".pdf").Split(Path.GetInvalidFileNameChars())).Replace(':', '-');
		string path = await _app.Dialogs.SaveFileAsync("Save the case file", suggested, "PDF document", "pdf");
		if (path == null)
		{
			return;
		}
		try
		{
			CaseFile file = _app.Store.GetCaseFile(PropertyId);
			Sde.Core.ReferenceData reference = _app.Store.Reference;
			await Task.Run(() => CaseFileReport.RenderToFile(file, reference, path, options));
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't create the case file", ex);
			return;
		}
		_app.Dialogs.Open(path);
	}

	/// <summary>The Documents card's Manage…: the structure's documents, to open, add or remove.</summary>
	[RelayCommand]
	private async Task ShowDocuments()
	{
		await _app.Sheets.ShowAsync(new DocumentsSheetViewModel(_app, PropertyId, null));
		Reload();
	}

	/// <summary>The Assessments card's Compare…: two of the structure's assessments side by side.</summary>
	[RelayCommand(CanExecute = nameof(CanCompare))]
	private async Task Compare()
	{
		List<Guid> ids = Assessments.Where(a => !a.IsTrashed).Select(a => a.Id).ToList();
		if (ids.Count >= 2)
		{
			await _app.Sheets.ShowAsync(new CompareAssessmentsSheetViewModel(_app, PropertyId, ids[0], ids[1]));
		}
	}
}
