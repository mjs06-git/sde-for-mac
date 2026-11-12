using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.ViewModels.Shell;
using Sde.App.Services;
using Sde.App.ViewModels.Sheets;
using Sde.Data;
using Sde.Interop;

namespace Sde.App.ViewModels;

/// <summary>One dated item on the Agenda: its kind's icon, the day, what it is, where, and how late when overdue.</summary>
public sealed class AgendaEntryViewModel(AgendaEntry entry)
{
	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	public AgendaEntry Entry { get; } = entry;

	/// <summary>The icon for the kind of date.</summary>
	public string Icon => Entry.Kind switch
	{
		AgendaEntryKind.ComplianceDeadline => "Flag",
		AgendaEntryKind.AppealReceived => "Comment",
		AgendaEntryKind.AppealDecision => "Result",
		AgendaEntryKind.LetterSent => "Letter",
		AgendaEntryKind.FinalInspection => "CheckCircle",
		AgendaEntryKind.ElevationCertificateReceived => "Report",
		_ => "Event",
	};

	/// <summary>"Mon, Nov 16".</summary>
	public string DateText => Entry.Date.ToString("ddd, MMM d", Us);

	/// <summary>The title without the address the store adds after a colon (the address has a line of its own).</summary>
	public string Heading
	{
		get
		{
			string title = Entry.Title ?? "";
			string suffix = ": " + Entry.Address;
			return Entry.Address != null && title.EndsWith(suffix, StringComparison.Ordinal) ? title[..^suffix.Length] : title;
		}
	}

	/// <summary>"123 River Street · Path: Elevate. Permit BP-0001."</summary>
	public string Where => string.Join(" · ", new[] { Entry.Address, Entry.Detail }.Where(s => !string.IsNullOrWhiteSpace(s)));

	public bool HasWhere => Where.Length > 0;

	public bool IsOverdue => Entry.IsOverdue;

	/// <summary>"12 days late".</summary>
	public string LateText => !Entry.IsOverdue ? null : Entry.DaysOverdue == 1 ? "1 day late" : $"{Entry.DaysOverdue:N0} days late";

	/// <summary>Whether clicking the row leads somewhere (a case, an assessment or an event).</summary>
	public bool CanOpen => Entry.CaseId != null || Entry.AssessmentId != null || Entry.EventId != null;

	public string AccessibleName => $"{Heading}, {Entry.Date.ToString("MMMM d, yyyy", Us)}" + (Entry.Address == null ? "" : ", " + Entry.Address)
		+ (IsOverdue ? ", " + LateText : "");

	/// <summary>Opens the entry's case or assessment (the Agenda page's command).</summary>
	public IAsyncRelayCommand<AgendaEntryViewModel> Open { get; init; }
}

/// <summary>The Agenda's entries for one span: Overdue, This week, Next week, or a month.</summary>
public sealed class AgendaGroupViewModel(string title, IReadOnlyList<AgendaEntryViewModel> entries, bool isOverdue = false)
{
	public string Title { get; } = title;

	public IReadOnlyList<AgendaEntryViewModel> Entries { get; } = entries;

	public bool IsOverdue { get; } = isOverdue;

	/// <summary>"3 dates".</summary>
	public string CountText => Entries.Count == 1 ? "1 date" : $"{Entries.Count:N0} dates";
}

/// <summary>
/// The Agenda: what is coming up and what is overdue, from the store's agenda (compliance deadlines, final inspections,
/// elevation certificates, appeals, letters and event dates), grouped as Overdue, This week, Next week and then by
/// month, for the next 30 days, 90 days or everything ahead. It follows the event chosen in the sidebar as Compliance
/// does. A row opens its compliance case or assessment; Add to Calendar writes the shown dates to an .ics file. Its
/// sidebar badge counts the overdue cases and those due in the next 14 days, as the store counts them.
/// </summary>
public sealed partial class AgendaViewModel : ViewModelBase
{
	/// <summary>The range switch's choices.</summary>
	public static IReadOnlyList<string> RangeChoices { get; } = new[] { "30 Days", "90 Days", "All" };

	/// <summary>How many days ahead "due soon" looks, as the hero and the sidebar badge count it.</summary>
	public const int DueSoonDays = 14;

	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	private readonly AppServices _app;
	private readonly MainViewModel _main;
	private List<AgendaEntry> _shown = new();

	public AgendaViewModel(AppServices app, MainViewModel main)
	{
		_app = app;
		_main = main;
	}

	/// <summary>Set by the window while this is the page showing (it then reloads when the data changes).</summary>
	public bool IsActive { get; set; }

	/// <summary>The entries shown, by span.</summary>
	public ObservableCollection<AgendaGroupViewModel> Groups { get; } = new();

	/// <summary>The entries shown, oldest first (what Add to Calendar writes).</summary>
	public IReadOnlyList<AgendaEntry> Shown => _shown;

	/// <summary>0 = the next 30 days, 1 = the next 90 days, 2 = everything ahead (overdue items always show).</summary>
	[ObservableProperty]
	public partial int RangeIndex { get; set; }

	partial void OnRangeIndexChanged(int value) => Reload();

	/// <summary>Open compliance cases past their deadline, in scope.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasOverdue), nameof(Headline))]
	public partial int OverdueCount { get; private set; }

	/// <summary>Open compliance cases due in the next <see cref="DueSoonDays"/> days, in scope.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Headline))]
	public partial int DueSoonCount { get; private set; }

	public bool HasOverdue => OverdueCount > 0;

	/// <summary>The hero's headline: "3 dates in the next 30 days".</summary>
	public string Headline
	{
		get
		{
			int ahead = _shown.Count(e => !e.IsOverdue);
			string dates = ahead == 0 ? "Nothing" : ahead == 1 ? "1 date" : $"{ahead:N0} dates";
			string span = RangeIndex switch
			{
				0 => "in the next 30 days",
				1 => "in the next 90 days",
				_ => "ahead",
			};
			return $"{dates} {span}";
		}
	}

	/// <summary>"Overdue", the first hero figure's label.</summary>
	public string OverdueLabel => "Overdue";

	/// <summary>"Due in 14 days", the second hero figure's label.</summary>
	public string DueSoonLabel => $"Due in {DueSoonDays} days";

	public bool HasEntries => Groups.Count > 0;

	public bool IsEmpty => Groups.Count == 0;

	/// <summary>The empty state's one line.</summary>
	public string EmptyTitle => _app.Scope.IsAll ? "Nothing Coming Up" : $"Nothing Coming Up in {_app.Scope.Event.Label}";

	/// <summary>What the last export did.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasResult))]
	public partial string LastResult { get; private set; }

	public bool HasResult => LastResult != null;

	/// <summary>The status line: "5 dates · 1 overdue".</summary>
	public string StatusText => (_shown.Count == 1 ? "1 date" : $"{_shown.Count:N0} dates") + $" · {OverdueCount:N0} overdue";

	/// <summary>Today: the store's clock (which tests set), so overdue and the weeks agree with the store's counts.</summary>
	public DateTime Today => _app.Store.Clock().Date;

	private Guid? EventId => _app.Scope.IsAll ? null : _app.Scope.EventId;

	/// <summary>The sidebar's badge: overdue cases plus those due in the next 14 days, in scope.</summary>
	public static (int Overdue, int DueSoon) CountBadge(AppServices app)
	{
		DateTime today = app.Store.Clock().Date;
		Guid? eventId = app.Scope.IsAll ? null : app.Scope.EventId;
		return (app.Store.GetOverdueCount(today, eventId), app.Store.GetDueSoonCount(DueSoonDays, today, eventId));
	}

	/// <summary>Reads the agenda again for the range and the event in scope.</summary>
	public void Reload()
	{
		DateTime today = Today;
		DateTime? to = RangeIndex switch
		{
			0 => today.AddDays(30),
			1 => today.AddDays(90),
			_ => null,
		};
		try
		{
			// Everything up to the range's end, then only what is ahead and what is overdue (past letters and decisions are history).
			_shown = _app.Store.GetAgenda(null, to, EventId, today: today).Where(e => e.IsOverdue || e.Date >= today).ToList();
			(OverdueCount, DueSoonCount) = CountBadge(_app);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			AppLog.Write("Couldn't read the agenda", ex);
			_shown = new List<AgendaEntry>();
		}
		Groups.Clear();
		foreach (AgendaGroupViewModel group in Group(_shown, today, Describe))
		{
			Groups.Add(group);
		}
		OnPropertyChanged(nameof(HasEntries));
		OnPropertyChanged(nameof(IsEmpty));
		OnPropertyChanged(nameof(EmptyTitle));
		OnPropertyChanged(nameof(Headline));
		OnPropertyChanged(nameof(StatusText));
		OnPropertyChanged(nameof(Shown));
	}

	private AgendaEntryViewModel Describe(AgendaEntry e) => new AgendaEntryViewModel(e) { Open = OpenEntryCommand };

	/// <summary>
	/// The entries by span: Overdue (oldest first), This week (today to Sunday), Next week (Monday to Sunday), then each
	/// later month ("January 2027"). Spans with nothing are left out.
	/// </summary>
	public static List<AgendaGroupViewModel> Group(IEnumerable<AgendaEntry> entries, DateTime today, Func<AgendaEntry, AgendaEntryViewModel> describe)
	{
		DateTime day = today.Date;
		int sinceMonday = ((int)day.DayOfWeek + 6) % 7;
		DateTime nextMonday = day.AddDays(7 - sinceMonday);
		DateTime weekAfter = nextMonday.AddDays(7);
		List<AgendaEntry> list = entries.ToList();
		List<AgendaGroupViewModel> groups = new();
		void Add(string title, IEnumerable<AgendaEntry> items, bool overdue = false)
		{
			List<AgendaEntryViewModel> rows = items.Select(describe).ToList();
			if (rows.Count > 0)
			{
				groups.Add(new AgendaGroupViewModel(title, rows, overdue));
			}
		}
		Add("Overdue", list.Where(e => e.IsOverdue), overdue: true);
		List<AgendaEntry> ahead = list.Where(e => !e.IsOverdue && e.Date >= day).ToList();
		Add("This week", ahead.Where(e => e.Date < nextMonday));
		Add("Next week", ahead.Where(e => e.Date >= nextMonday && e.Date < weekAfter));
		foreach (IGrouping<DateTime, AgendaEntry> month in ahead.Where(e => e.Date >= weekAfter).GroupBy(e => new DateTime(e.Date.Year, e.Date.Month, 1)).OrderBy(g => g.Key))
		{
			Add(month.Key.ToString("MMMM yyyy", Us), month);
		}
		return groups;
	}

	/// <summary>
	/// A row: a case's date opens the case sheet; an appeal's or a letter's opens the assessment (closing it comes back
	/// here); an event's date shows that event's summary.
	/// </summary>
	[RelayCommand]
	private async Task OpenEntry(AgendaEntryViewModel row)
	{
		AgendaEntry e = row?.Entry;
		if (e == null)
		{
			return;
		}
		if (e.CaseId is Guid caseId)
		{
			ComplianceCaseSheetViewModel sheet;
			try
			{
				sheet = new ComplianceCaseSheetViewModel(_app, caseId, id => _main.OpenAssessmentFrom(id, Page.Agenda));
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
		else if (e.AssessmentId is Guid assessmentId)
		{
			_main.OpenAssessmentFrom(assessmentId, Page.Agenda);
		}
		else if (e.EventId is Guid eventId && _main.Events.Events.FirstOrDefault(c => c.Id == eventId) is EventChoice choice)
		{
			_main.Events.ChooseCommand.Execute(choice);
			_main.ShowCommand.Execute(Page.Dashboard);
		}
	}

	/// <summary>Writes the dates shown to a calendar file (.ics) that Calendar can add, and shows it in the Finder.</summary>
	[RelayCommand]
	private async Task AddToCalendar()
	{
		if (_shown.Count == 0)
		{
			_app.Dialogs.Notify("Nothing to add", "There are no dates in this range.");
			return;
		}
		string name = _app.Scope.IsAll ? "SDE agenda.ics" : $"SDE agenda ({_app.Scope.Event.Label}).ics";
		name = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Replace(':', '-');
		string path = await _app.Dialogs.SaveFileAsync("Add to Calendar", name, "Calendar file", "ics");
		if (string.IsNullOrEmpty(path))
		{
			return;
		}
		try
		{
			IcsWriter.Write(path, _shown.Select(AgendaItem.FromEntry));
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't write the calendar file", ex);
			return;
		}
		LastResult = $"Saved {Path.GetFileName(path)} ({(_shown.Count == 1 ? "1 date" : $"{_shown.Count:N0} dates")}).";
		_app.Dialogs.Reveal(path);
	}
}
