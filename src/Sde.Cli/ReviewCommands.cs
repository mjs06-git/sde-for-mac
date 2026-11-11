using System.Globalization;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Depth;
using Sde.Core.Events;
using Sde.Core.Quality;
using Sde.Core.Records;
using Sde.Data;
using Sde.Reports;

namespace Sde.Cli;

/// <summary>
/// The commands that only read: events, qa, compliance and report. They open the database read-only whenever it is
/// up to date, so running them never changes it.
/// </summary>
public static class ReviewCommands
{
	/// <summary>The exit code when a command ran but found something to fix (a required quality finding).</summary>
	public const int FoundProblems = 1;

	/// <summary>Today's date. Tests replace it to get repeatable results.</summary>
	public static Func<DateTime> Today { get; set; } = () => DateTime.Today;

	public const string EventsHelp = """
		Usage: sde [--db PATH] events

		Lists the events (disasters) in the database, newest first, with their dates and how many assessments each has.
		Only active assessments of active structures are counted.
		""";

	public const string QaHelp = """
		Usage: sde [--db PATH] qa [--event NAME-OR-ID] [--all] [--marks FILE]

		Runs the quality checks that SDE's Review page runs and prints what they find. By default it checks every
		active assessment in the database; --event checks only the assessments in one event. Findings that someone
		set aside in SDE are not listed unless you give --all.

		The exit code is 0 when there are no required findings, and 1 when there is at least one open finding of
		the most serious level (Required). Other findings never change the exit code.
		The flood map zone check compares with a flood lookup stored earlier, and the high-water mark check compares
		with the marks kept with each event in SDE's depth tool, or with the marks in --marks FILE (a .csv or .geojson
		file of marks) instead. Neither goes online. A check with nothing
		to compare with is listed on a "Not checked" line at the end; that never changes the exit code.
		""";

	public const string ComplianceHelp = """
		Usage: sde [--db PATH] compliance [--overdue] [--due-within DAYS] [--all]

		Lists compliance cases with their status and deadline. By default only open cases are listed.
		  --overdue            only open cases whose deadline has passed
		  --due-within DAYS    only open cases whose deadline is today or within the next DAYS days
		  --all                include closed cases (cannot be combined with the two filters above)
		""";

	public const string ReportHelp = """
		Usage: sde [--db PATH] report structure ASSESSMENT-ID FILE.pdf [--kind summary|detail] [--no-photos]
		       sde [--db PATH] report event FILE.pdf [--event NAME-OR-ID]

		Writes a PDF report.
		  structure   the summary report (default) or the detail report for one assessment
		  event       the one-page event summary, for one event or for the whole database
		An existing file with the same name is replaced.
		""";

	/// <summary>Lists events with their dates and assessment counts.</summary>
	public static int Events(SdeStore s)
	{
		List<EventRecord> events = s.GetEvents();
		Dictionary<Guid, int> counts = s.EventCounts();
		Console.WriteLine($"{"Event",-36}  {"Name",-30} {"Kind",-10} {"Start",-10} {"End",-10} {"Assessments",11}");
		foreach (EventRecord e in events)
		{
			Console.WriteLine($"{e.EventID,-36}  {Trunc(e.Label, 30),-30} {Trunc(e.Kind, 10),-10} {Date(e.StartDate),-10} {Date(e.EndDate),-10} {counts.GetValueOrDefault(e.EventID),11}");
		}
		Console.WriteLine($"{events.Count} events");
		return 0;
	}

	/// <summary>Runs the quality checks and prints the findings.</summary>
	public static int Qa(SdeStore s, string eventText, bool includeDismissed) => Qa(s, eventText, includeDismissed, null);

	/// <summary>
	/// Runs the quality checks and prints the findings. A marks file turns on the high-water mark check; checks that
	/// have nothing to compare with are named on a "Not checked" line. Nothing goes online.
	/// </summary>
	public static int Qa(SdeStore s, string eventText, bool includeDismissed, string marksFile)
	{
		EventRecord e = eventText == null ? null : FindEvent(s, eventText);
		List<Guid> ids = null;
		if (e != null)
		{
			HashSet<Guid> members = s.AssessmentsInEvent(e.EventID);
			ids = s.ListStructures().Where(r => r.AssessmentID != null && members.Contains(r.AssessmentID.Value)).Select(r => r.AssessmentID.Value).ToList();
		}
		QaInput input = s.LoadQaInput(ids);
		if (!string.IsNullOrWhiteSpace(marksFile))
		{
			HighWaterMarkFile marks = HighWaterMarkFiles.Read(marksFile);
			if (marks.Marks.Count == 0)
			{
				throw new ArgumentException($"There are no high-water marks in {marksFile}." + (marks.Problems.Count > 0 ? " " + marks.Problems[0] : ""));
			}
			input = input with { HighWaterMarkEstimates = DepthFromMarks.QaEstimates(s, input.Assessments.Select(f => f.Assessment.AssessmentID).ToList(), marks.Marks), AssessmentsWithoutMarks = null };
		}
		List<QaFinding> all = QaRules.Check(input);
		List<QaNotChecked> notChecked = QaRules.NotChecked(input);
		List<QaFinding> shown = includeDismissed ? all : all.Where(f => !f.IsDismissed).ToList();
		Dictionary<Guid, AssessmentFile> byAssessment = input.Assessments.ToDictionary(f => f.Assessment.AssessmentID);
		Dictionary<Guid, AssessmentFile> byProperty = input.Assessments.GroupBy(f => f.Property.PropertyID).ToDictionary(g => g.Key, g => g.First());

		Console.WriteLine($"Checked {input.Assessments.Count} assessments in {(e == null ? "the whole database" : "the event " + e.Label)}.");
		foreach (QaFinding f in shown.OrderByDescending(f => f.Severity))
		{
			string about = About(f, byAssessment, byProperty, input, s.Reference);
			Console.WriteLine($"{Level(f.Severity),-9} {Trunc(QaRules.Rule(f.RuleId)?.Title ?? f.RuleId, 32),-32} {Trunc(about, 36),-36} {f.Message}{(f.IsDismissed ? " (set aside)" : "")}");
		}
		int required = all.Count(f => f.Severity == QaSeverity.Error && !f.IsDismissed);
		int warnings = all.Count(f => f.Severity == QaSeverity.Warning && !f.IsDismissed);
		int notes = all.Count(f => f.Severity == QaSeverity.Info && !f.IsDismissed);
		int setAside = all.Count(f => f.IsDismissed);
		Console.WriteLine($"{required} required, {warnings} warnings, {notes} notes, {setAside} set aside");
		if (notChecked.Count > 0)
		{
			Console.WriteLine("Not checked: " + string.Join("; ", notChecked.Select(n => $"{n.Title} ({n.Message.Replace("Not checked: ", "").TrimEnd('.')})")) + ".");
		}
		return required > 0 ? FoundProblems : 0;
	}

	/// <summary>Lists compliance cases, optionally only the overdue ones or those due soon.</summary>
	public static int Compliance(SdeStore s, bool overdue, int? dueWithinDays, bool includeClosed)
	{
		DateTime today = Today().Date;
		bool filtered = overdue || dueWithinDays != null;
		List<ComplianceCaseRecord> cases = s.GetComplianceCases(openOnly: !includeClosed || filtered);
		if (overdue)
		{
			cases = cases.Where(c => c.IsOverdue(today)).ToList();
		}
		if (dueWithinDays is int days)
		{
			DateTime last = today.AddDays(days);
			cases = cases.Where(c => c.IsOpen && c.Deadline is DateTime d && d.Date >= today && d.Date <= last).ToList();
		}
		Dictionary<Guid, string> addresses = s.ListStructures().GroupBy(r => r.PropertyID).ToDictionary(g => g.Key, g => g.First().Address);
		Console.WriteLine($"{"Case",-36}  {"Kind",-4} {"Status",-7} {"Deadline",-10} {"Left",9}  Address");
		foreach (ComplianceCaseRecord c in cases)
		{
			string left = c.Deadline is DateTime d && c.IsOpen ? (d.Date < today ? $"{(today - d.Date).Days} late" : $"{(d.Date - today).Days} days") : "";
			Console.WriteLine($"{c.CaseID,-36}  {c.Kind,-4} {c.Status,-7} {Date(c.Deadline),-10} {left,9}  {Trunc(addresses.GetValueOrDefault(c.PropertyID), 40)}");
		}
		Console.WriteLine($"{cases.Count} cases");
		return 0;
	}

	/// <summary>Writes the summary or detail report for one assessment.</summary>
	public static int ReportStructure(SdeStore s, Guid assessmentId, string path, string kindText, bool includePhotos)
	{
		ReportKind kind = (kindText ?? "summary").ToLowerInvariant() switch
		{
			"summary" => ReportKind.Summary,
			"detail" => ReportKind.Detail,
			_ => throw new ArgumentException($"Unknown report kind '{kindText}'. Use summary or detail."),
		};
		AssessmentFile file = s.LoadAssessment(assessmentId) ?? throw new ArgumentException("No assessment " + assessmentId);
		ReportRequest request = new ReportRequest
		{
			Kind = kind,
			Assessments = new[] { file },
			IncludePhotos = includePhotos,
			PhotosFolder = s.AttachmentsFolder,
		};
		ReportGenerator.RenderToFile(request, s.Reference, path);
		Console.WriteLine($"Wrote the {kind.ToString().ToLowerInvariant()} report to {path}");
		return 0;
	}

	/// <summary>Writes the one-page event summary, for one event or the whole database.</summary>
	public static int ReportEvent(SdeStore s, string path, string eventText)
	{
		EventRecord e = eventText == null ? null : FindEvent(s, eventText);
		List<MapPointRow> rows;
		if (e == null)
		{
			rows = s.ListMapPoints();
		}
		else
		{
			HashSet<Guid> members = s.AssessmentsInEvent(e.EventID);
			rows = s.ListMapPoints(members.Contains);
		}
		List<AssessmentFile> files = s.LoadAssessments(rows.Where(r => r.AssessmentID != null).Select(r => r.AssessmentID.Value).ToList());
		EventSummary summary = EventSummary.Build(files, e == null ? s.GetProperties() : null, e?.Label, s.Reference, details: e?.ToDetails());
		EventSummaryReport.RenderToFile(summary, new EventSummaryOptions(), path);
		Console.WriteLine($"Wrote the event summary for {(e == null ? "the whole database" : e.Label)} ({files.Count} assessments) to {path}");
		return 0;
	}

	/// <summary>An event by its ID or, ignoring case, its name or label.</summary>
	private static EventRecord FindEvent(SdeStore s, string text)
	{
		List<EventRecord> events = s.GetEvents();
		string wanted = text.Trim();
		EventRecord found = Guid.TryParse(wanted, out Guid id)
			? events.FirstOrDefault(e => e.EventID == id)
			: events.FirstOrDefault(e => string.Equals(e.Name, wanted, StringComparison.OrdinalIgnoreCase) || string.Equals(e.Label, wanted, StringComparison.OrdinalIgnoreCase));
		return found ?? throw new ArgumentException($"There is no event '{text}'. Run 'sde events' to see them.");
	}

	private static string About(QaFinding f, Dictionary<Guid, AssessmentFile> byAssessment, Dictionary<Guid, AssessmentFile> byProperty, QaInput input, ReferenceData reference)
	{
		if (f.AssessmentId is Guid a && byAssessment.TryGetValue(a, out AssessmentFile file))
		{
			return QaRules.Address(file.Property, reference);
		}
		if (f.PropertyId is Guid p && byProperty.TryGetValue(p, out AssessmentFile owner))
		{
			return QaRules.Address(owner.Property, reference);
		}
		return input.Events != null && input.Events.TryGetValue(f.ObjectId, out QaEvent e) ? "Event " + e.Name : "";
	}

	private static string Level(QaSeverity severity) => severity switch
	{
		QaSeverity.Error => "Required",
		QaSeverity.Warning => "Warning",
		_ => "Note",
	};

	private static string Date(DateTime? d) => d == null ? "" : d.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

	private static string Trunc(string s, int n) => s == null ? "" : s.Length <= n ? s : s[..(n - 1)] + "…";
}
