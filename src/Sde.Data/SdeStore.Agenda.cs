using System.Data;
using Dapper;
using Sde.Core.Records;
using Sde.Data.Infrastructure;

namespace Sde.Data;

/// <summary>
/// What kind of date an <see cref="AgendaEntry"/> is. The order is the agenda's order for entries on the same day; the
/// first seven match Sde.Interop's AgendaKind, which the calendar file's identifiers are made from.
/// </summary>
public enum AgendaEntryKind
{
	/// <summary>The date an open compliance case must be resolved by.</summary>
	ComplianceDeadline,

	/// <summary>The date an appeal was received.</summary>
	AppealReceived,

	/// <summary>The date an appeal was decided.</summary>
	AppealDecision,

	/// <summary>The date a letter was marked as sent to an owner.</summary>
	LetterSent,

	/// <summary>The date of a compliance case's final inspection.</summary>
	FinalInspection,

	/// <summary>The first day of an event.</summary>
	EventStart,

	/// <summary>The last day of an event.</summary>
	EventEnd,

	/// <summary>The date a compliance case's elevation certificate was received.</summary>
	ElevationCertificateReceived,
}

/// <summary>One dated item on the agenda (what is coming up and what is overdue), with the IDs to follow it up.</summary>
public sealed class AgendaEntry
{
	public AgendaEntryKind Kind { get; init; }

	/// <summary>The day it falls on (no time).</summary>
	public DateTime Date { get; init; }

	/// <summary>A short heading, e.g. "Compliance deadline (SD): 123 River Street". Quotes text people typed.</summary>
	public string Title { get; init; }

	/// <summary>More detail (a case's path and permit, an appeal's basis), or null.</summary>
	public string Detail { get; init; }

	/// <summary>The structure's street address, or null for an event's dates.</summary>
	public string Address { get; init; }

	public Guid? PropertyId { get; init; }

	public Guid? AssessmentId { get; init; }

	/// <summary>The compliance case, for a deadline, final inspection or elevation certificate.</summary>
	public Guid? CaseId { get; init; }

	/// <summary>The event, for an event's first and last days.</summary>
	public Guid? EventId { get; init; }

	/// <summary>
	/// The record the date belongs to (a case, an appeal's assessment, a letter, an event). With <see cref="Kind"/> it
	/// identifies the entry, so a calendar file written again updates it instead of adding another.
	/// </summary>
	public Guid RecordId { get; init; }

	/// <summary>True for an open case's deadline before "today".</summary>
	public bool IsOverdue { get; init; }

	/// <summary>Days past the deadline (0 when not overdue).</summary>
	public int DaysOverdue { get; init; }
}

public sealed partial class SdeStore
{
	/// <summary>
	/// The agenda: dated items from <paramref name="from"/> to <paramref name="to"/> (both days included; null leaves
	/// that end open), oldest first, then by kind and title. It lists open compliance cases' deadlines (marked overdue
	/// relative to <paramref name="today"/>, which defaults to the store's clock), final inspections, elevation
	/// certificates received, appeals received and decided, letters marked as sent, and events' first and last days.
	/// Only structures and assessments not in Recently deleted count; with <paramref name="eventId"/>, only that event's
	/// items (the cases the Compliance page shows for it, its assessments' appeals and letters, its own dates). Closed
	/// cases are left out unless <paramref name="includeClosedCases"/>, which adds their final inspection and elevation
	/// certificate dates; a closed case's deadline is never listed, since nothing is due.
	/// </summary>
	public List<AgendaEntry> GetAgenda(DateTime? from, DateTime? to, Guid? eventId = null, bool includeClosedCases = false, DateTime? today = null)
	{
		DateTime day = (today ?? Clock()).Date;
		DateTime? first = from?.Date;
		DateTime? last = to?.Date;
		bool InWindow(DateTime d) => (first == null || d.Date >= first) && (last == null || d.Date <= last);

		return Database.Read(c =>
		{
			Dictionary<Guid, PropertyRecord> properties = TableMap<PropertyRecord>.Select(c, "Active = 1").ToDictionary(p => p.PropertyID);
			Dictionary<Guid, Guid> inUse = c.Query<AssessmentStructureRow>(
				"SELECT a.AssessmentID, a.PropertyID FROM Assessment a JOIN Property p ON p.PropertyID = a.PropertyID WHERE a.Active = 1 AND p.Active = 1")
				.ToDictionary(r => r.AssessmentID, r => r.PropertyID);
			HashSet<Guid> inEvent = eventId is Guid id ? AssessmentsInEvent(c, id) : null;
			bool Counts(Guid assessmentId) => inUse.ContainsKey(assessmentId) && (inEvent == null || inEvent.Contains(assessmentId));
			string AddressOf(Guid propertyId) => properties.TryGetValue(propertyId, out PropertyRecord p) ? AddressLabel(p) : null;

			List<AgendaEntry> items = new List<AgendaEntry>();
			foreach (ComplianceCaseRecord cc in GetComplianceCasesInScope(c, eventId).Where(x => includeClosedCases || x.IsOpen))
			{
				string address = AddressOf(cc.PropertyID);
				AgendaEntry Case(AgendaEntryKind kind, DateTime date, string title, string detail = null, bool overdue = false) => new AgendaEntry
				{
					Kind = kind,
					Date = date.Date,
					Title = title,
					Detail = detail,
					Address = address,
					PropertyId = cc.PropertyID,
					AssessmentId = cc.AssessmentID,
					CaseId = cc.CaseID,
					RecordId = cc.CaseID,
					IsOverdue = overdue,
					DaysOverdue = overdue ? (day - date.Date).Days : 0,
				};
				if (cc.IsOpen && cc.Deadline is DateTime deadline && InWindow(deadline))
				{
					string path = cc.Path == null ? null : "Path: " + CompliancePaths.Label(cc.Path) + ".";
					string permit = string.IsNullOrWhiteSpace(cc.PermitNumber) ? null : "Permit " + cc.PermitNumber.Trim() + ".";
					items.Add(Case(AgendaEntryKind.ComplianceDeadline, deadline, $"Compliance deadline ({cc.Kind}): {address}", JoinParts(" ", path, permit), cc.IsOverdue(day)));
				}
				if (cc.FinalInspectionOn is DateTime inspection && InWindow(inspection))
				{
					items.Add(Case(AgendaEntryKind.FinalInspection, inspection, "Final inspection: " + address));
				}
				if (cc.EcReceivedOn is DateTime ec && InWindow(ec))
				{
					items.Add(Case(AgendaEntryKind.ElevationCertificateReceived, ec, "Elevation certificate received: " + address));
				}
			}

			foreach (AppealRecord ap in GetAppeals(c, null).Where(x => Counts(x.AssessmentID)))
			{
				Guid propertyId = inUse[ap.AssessmentID];
				string address = AddressOf(propertyId);
				AgendaEntry Appeal(AgendaEntryKind kind, DateTime date, string title, string detail) => new AgendaEntry
				{
					Kind = kind,
					Date = date.Date,
					Title = title,
					Detail = detail,
					Address = address,
					PropertyId = propertyId,
					AssessmentId = ap.AssessmentID,
					RecordId = ap.AssessmentID,
				};
				if (ap.ReceivedOn is DateTime received && InWindow(received))
				{
					items.Add(Appeal(AgendaEntryKind.AppealReceived, received, "Appeal received: " + address, string.IsNullOrWhiteSpace(ap.Basis) ? null : ap.Basis.Trim()));
				}
				if (ap.DecidedOn is DateTime decided && InWindow(decided))
				{
					items.Add(Appeal(AgendaEntryKind.AppealDecision, decided, $"Appeal decided ({ap.Outcome ?? AppealOutcomes.Pending}): {address}", null));
				}
			}

			foreach (LetterLogRecord l in TableMap<LetterLogRecord>.Select(c, "SentOn IS NOT NULL").Where(x => Counts(x.AssessmentID) && InWindow(x.SentOn.Value)))
			{
				Guid propertyId = inUse[l.AssessmentID];
				string address = AddressOf(propertyId);
				items.Add(new AgendaEntry
				{
					Kind = AgendaEntryKind.LetterSent,
					Date = l.SentOn.Value.Date,
					Title = $"Letter sent ({(string.IsNullOrWhiteSpace(l.Kind) ? "letter" : l.Kind.Trim())}): {address}",
					Address = address,
					PropertyId = propertyId,
					AssessmentId = l.AssessmentID,
					RecordId = l.LetterID,
				});
			}

			List<EventRecord> events = eventId is Guid only
				? TableMap<EventRecord>.Select(c, "EventID = @only", new { only = only.ToString("D") }).ToList()
				: TableMap<EventRecord>.Select(c).ToList();
			foreach (EventRecord ev in events)
			{
				if (ev.StartDate is DateTime start && InWindow(start))
				{
					items.Add(new AgendaEntry { Kind = AgendaEntryKind.EventStart, Date = start.Date, Title = ev.Label + " starts", EventId = ev.EventID, RecordId = ev.EventID });
				}
				if (ev.EndDate is DateTime end && InWindow(end))
				{
					items.Add(new AgendaEntry { Kind = AgendaEntryKind.EventEnd, Date = end.Date, Title = ev.Label + " ends", EventId = ev.EventID, RecordId = ev.EventID });
				}
			}

			return items.OrderBy(e => e.Date).ThenBy(e => e.Kind).ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase).ToList();
		});
	}

	/// <summary>
	/// How many compliance cases are overdue on <paramref name="today"/>: open, with a deadline before it, among the
	/// cases the Compliance page counts for <paramref name="eventId"/> (all events when null). The same rule as the
	/// sidebar's badge (ComplianceViewModel.CountOverdue in the app).
	/// </summary>
	public int GetOverdueCount(DateTime today, Guid? eventId = null) =>
		Database.Read(c => GetComplianceCasesInScope(c, eventId).Count(x => x.IsOverdue(today)));

	/// <summary>
	/// How many compliance cases fall due within <paramref name="days"/> days of <paramref name="today"/>: open, with a
	/// deadline from today to today plus <paramref name="days"/> (both included), among the same cases as
	/// <see cref="GetOverdueCount"/>. Overdue cases are not counted again.
	/// </summary>
	public int GetDueSoonCount(int days, DateTime today, Guid? eventId = null)
	{
		if (days < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(days), "The number of days can't be negative.");
		}
		DateTime first = today.Date;
		DateTime last = first.AddDays(days);
		return Database.Read(c => GetComplianceCasesInScope(c, eventId)
			.Count(x => x.IsOpen && x.Deadline is DateTime d && d.Date >= first && d.Date <= last));
	}

	/// <summary>An assessment in use and its structure.</summary>
	private sealed class AssessmentStructureRow
	{
		public Guid AssessmentID { get; set; }

		public Guid PropertyID { get; set; }
	}
}
