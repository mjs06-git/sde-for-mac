using System.ComponentModel;
using Dapper;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Sde.Core.Records;
using Sde.Data;
using Sde.Interop;

namespace Sde.Mcp;

/// <summary>
/// The agenda: dated items across the database or one event (compliance deadlines, final inspections, appeals received
/// and decided, letters sent, events' first and last days), as the shared <see cref="AgendaItem"/> that SDE's calendar
/// file is written from.
/// </summary>
public sealed partial class ReadTools
{
	/// <summary>The most items one page of agenda returns.</summary>
	public const int MaxAgendaPage = 100;

	/// <summary>How many days the agenda covers when no end date is given.</summary>
	public const int DefaultAgendaDays = 30;

	[McpServerTool(Name = "agenda", Title = "What's coming up", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
	[Description("Lists dated items between two dates, oldest first, a page at a time, for the whole database or one event: compliance deadlines of open cases, final inspections, appeals received and decided, letters marked as sent, and events' first and last days. Without from, it starts today and also lists open compliance deadlines that have already passed (marked overdue); without to, it covers 30 days. overdueOnly lists only the overdue deadlines, whatever the dates." + Untrusted)]
	public AgendaResult Agenda(
		[Description("The first day, as yyyy-MM-dd (default: today, with overdue deadlines too).")] string from = null,
		[Description("The last day, as yyyy-MM-dd (default: 30 days after the first).")] string to = null,
		[Description("true to list only open compliance deadlines that have passed.")] bool overdueOnly = false,
		[Description("Only this event's items: its name or ID (list_events lists them). Leave out for the whole database.")] string @event = null,
		[Description("The page to return, starting at 1.")] int page = 1,
		[Description("Items per page, 1 to 100 (default 50).")] int pageSize = 50)
	{
		sde.RequireDatabase();
		SdeStore store = sde.Reader;
		EventRecord e = Lookups.OptionalEvent(store, @event);
		DateTime today = store.Clock().Date;
		DateTime? fromGiven = Lookups.ParseDate(from, "from");
		DateTime start = fromGiven ?? today;
		DateTime end = Lookups.ParseDate(to, "to") ?? start.AddDays(DefaultAgendaDays);
		if (end < start)
		{
			throw new McpException("to can't be before from.");
		}
		// Without a first day, deadlines that have already passed belong on the list too: they are the most pressing.
		bool withOverdue = fromGiven == null;
		pageSize = Math.Clamp(pageSize, 1, MaxAgendaPage);
		page = Math.Max(page, 1);

		Dictionary<Guid, Guid> inUse = AssessmentStructures(store);
		HashSet<Guid> inEvent = e == null ? null : store.AssessmentsInEvent(e.EventID);
		bool Counts(Guid assessmentId) => inUse.ContainsKey(assessmentId) && (inEvent == null || inEvent.Contains(assessmentId));
		bool InWindow(DateTime d) => !overdueOnly && d.Date >= start && d.Date <= end;
		Dictionary<Guid, PropertyRecord> properties = store.GetProperties().ToDictionary(p => p.PropertyID);
		string AddressOf(Guid propertyId) => properties.TryGetValue(propertyId, out PropertyRecord p) ? Describe.Address(p, store.Reference) : "(no address)";

		List<Dated> items = new List<Dated>();
		foreach (ComplianceCaseRecord c in store.GetComplianceCases())
		{
			// SI cases have no assessment, so they belong to no event.
			if (!properties.ContainsKey(c.PropertyID) || (inEvent != null && !(c.AssessmentID is Guid a && inEvent.Contains(a))))
			{
				continue;
			}
			string address = AddressOf(c.PropertyID);
			if (c.IsOpen && c.Deadline is DateTime deadline)
			{
				bool overdue = c.IsOverdue(today);
				if (InWindow(deadline) || (overdue && (overdueOnly || withOverdue)))
				{
					string path = c.Path == null ? null : "Path: " + CompliancePaths.Label(c.Path) + ".";
					string permit = string.IsNullOrWhiteSpace(c.PermitNumber) ? null : "Permit " + c.PermitNumber.Trim() + ".";
					items.Add(new Dated(new AgendaItem
					{
						Date = deadline.Date,
						Kind = AgendaKind.ComplianceDeadline,
						Title = $"Compliance deadline ({c.Kind}): {address}",
						Notes = Join(path, permit),
						RecordId = c.CaseID.ToString(),
						Address = address,
					}, c.AssessmentID, c.PropertyID, c.CaseID, null, overdue, overdue ? (today - deadline.Date).Days : 0));
				}
			}
			if (c.FinalInspectionOn is DateTime inspection && InWindow(inspection))
			{
				items.Add(new Dated(new AgendaItem
				{
					Date = inspection.Date,
					Kind = AgendaKind.FinalInspection,
					Title = "Final inspection: " + address,
					RecordId = c.CaseID.ToString(),
					Address = address,
				}, c.AssessmentID, c.PropertyID, c.CaseID, null, false, 0));
			}
		}
		(List<AppealRecord> appeals, List<LetterLogRecord> letters) = store.Database.Read(c => (
			c.Query<AppealRecord>("SELECT * FROM Appeal").ToList(),
			c.Query<LetterLogRecord>("SELECT * FROM LetterLog WHERE SentOn IS NOT NULL").ToList()));
		foreach (AppealRecord ap in appeals.Where(x => Counts(x.AssessmentID)))
		{
			Guid propertyId = inUse[ap.AssessmentID];
			string address = AddressOf(propertyId);
			if (ap.ReceivedOn is DateTime received && InWindow(received))
			{
				items.Add(new Dated(new AgendaItem
				{
					Date = received.Date,
					Kind = AgendaKind.AppealReceived,
					Title = "Appeal received: " + address,
					Notes = Describe.Shorten(Blank(ap.Basis)),
					RecordId = ap.AssessmentID.ToString(),
					Address = address,
				}, ap.AssessmentID, propertyId, null, null, false, 0));
			}
			if (ap.DecidedOn is DateTime decided && InWindow(decided))
			{
				items.Add(new Dated(new AgendaItem
				{
					Date = decided.Date,
					Kind = AgendaKind.AppealDecision,
					Title = $"Appeal decided ({ap.Outcome ?? AppealOutcomes.Pending}): {address}",
					RecordId = ap.AssessmentID.ToString(),
					Address = address,
				}, ap.AssessmentID, propertyId, null, null, false, 0));
			}
		}
		foreach (LetterLogRecord l in letters.Where(x => Counts(x.AssessmentID) && x.SentOn is DateTime sent && InWindow(sent)))
		{
			Guid propertyId = inUse[l.AssessmentID];
			string address = AddressOf(propertyId);
			items.Add(new Dated(new AgendaItem
			{
				Date = l.SentOn.Value.Date,
				Kind = AgendaKind.LetterSent,
				Title = $"Letter sent ({Blank(l.Kind) ?? "letter"}): {address}",
				RecordId = l.LetterID.ToString(),
				Address = address,
			}, l.AssessmentID, propertyId, null, null, false, 0));
		}
		foreach (EventRecord ev in e == null ? store.GetEvents() : new List<EventRecord> { e })
		{
			if (ev.StartDate is DateTime first && InWindow(first))
			{
				items.Add(new Dated(new AgendaItem { Date = first.Date, Kind = AgendaKind.EventStart, Title = Describe.Shorten(ev.Label, 120) + " starts", RecordId = ev.EventID.ToString() },
					null, null, null, ev.EventID, false, 0));
			}
			if (ev.EndDate is DateTime last && InWindow(last))
			{
				items.Add(new Dated(new AgendaItem { Date = last.Date, Kind = AgendaKind.EventEnd, Title = Describe.Shorten(ev.Label, 120) + " ends", RecordId = ev.EventID.ToString() },
					null, null, null, ev.EventID, false, 0));
			}
		}

		List<Dated> sorted = items.OrderBy(d => d.Item.Date).ThenBy(d => d.Item.Kind).ThenBy(d => d.Item.Title, StringComparer.OrdinalIgnoreCase).ToList();
		int pages = Math.Max(1, (sorted.Count + pageSize - 1) / pageSize);
		List<AgendaEntry> entries = sorted.Skip((page - 1) * pageSize).Take(pageSize).Select(d => new AgendaEntry(Describe.Date(d.Item.Date), d.Item.Kind.ToString(),
			d.Item.Title, d.Item.Notes, d.Item.Address, d.AssessmentId?.ToString(), d.PropertyId?.ToString(), d.CaseId?.ToString(), d.EventId?.ToString(),
			d.Overdue, d.DaysOverdue)).ToList();
		string next = page < pages ? $"Showing page {page} of {pages}. For more, call agenda again with page = {page + 1} and the same arguments." : null;
		string window = $"{Describe.Date(start)} to {Describe.Date(end)}";
		string note = overdueOnly
			? (sorted.Count == 0 ? "No open compliance case is past its deadline." : "Only open compliance deadlines that have passed are listed, whatever the dates.")
			: sorted.Count == 0
				? $"Nothing is dated from {window}{(withOverdue ? ", and no open compliance case is past its deadline" : "")}."
				: withOverdue ? "Open compliance deadlines that have already passed are listed too, marked overdue." : null;
		return new AgendaResult(sde.DatabasePath, e == null ? "the whole database" : $"the event {e.Label}", overdueOnly ? null : Describe.Date(start),
			overdueOnly ? null : Describe.Date(end), overdueOnly, sorted.Count, sorted.Count(d => d.Overdue), page, pageSize, pages, entries, next, note);
	}

	/// <summary>Every assessment in use (active, of an active structure), with its structure.</summary>
	private static Dictionary<Guid, Guid> AssessmentStructures(SdeStore store) => store.Database.Read(c => c.Query<AssessmentStructure>(
		"SELECT a.AssessmentID, a.PropertyID FROM Assessment a JOIN Property p ON p.PropertyID = a.PropertyID WHERE a.Active = 1 AND p.Active = 1")
		.ToDictionary(r => r.AssessmentID, r => r.PropertyID));

	private static string Join(params string[] parts)
	{
		string text = string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
		return text.Length == 0 ? null : text;
	}

	/// <summary>An agenda item with the IDs a tool needs to follow it up.</summary>
	private sealed record Dated(AgendaItem Item, Guid? AssessmentId, Guid? PropertyId, Guid? CaseId, Guid? EventId, bool Overdue, int DaysOverdue);

	/// <summary>One row of <see cref="AssessmentStructures"/>.</summary>
	internal sealed class AssessmentStructure
	{
		public Guid AssessmentID { get; set; }

		public Guid PropertyID { get; set; }
	}
}
