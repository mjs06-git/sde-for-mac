using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Sde.Data;

namespace Sde.Mcp;

/// <summary>
/// The agenda: dated items across the database or one event (compliance deadlines, final inspections, elevation
/// certificates received, appeals received and decided, letters sent, events' first and last days). The items come
/// from <see cref="SdeStore.GetAgenda"/>, the same list the app shows and SDE's calendar file is written from, so the
/// assistant and the app agree.
/// </summary>
public sealed partial class ReadTools
{
	/// <summary>The most items one page of agenda returns.</summary>
	public const int MaxAgendaPage = 100;

	/// <summary>How many days the agenda covers when no end date is given.</summary>
	public const int DefaultAgendaDays = 30;

	[McpServerTool(Name = "agenda", Title = "What's coming up", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
	[Description("Lists dated items between two dates, oldest first, a page at a time, for the whole database or one event: compliance deadlines of open cases, final inspections, elevation certificates received, appeals received and decided, letters marked as sent, and events' first and last days. Without from, it starts today and also lists open compliance deadlines that have already passed (marked overdue); without to, it covers 30 days. overdueOnly lists only the overdue deadlines, whatever the dates." + Untrusted)]
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

		// Closed cases' final inspections and certificates are on record and stay on the assistant's agenda; a closed
		// case's deadline never is.
		List<Sde.Data.AgendaEntry> sorted = overdueOnly
			? store.GetAgenda(null, null, e?.EventID, includeClosedCases: true, today: today).Where(x => x.IsOverdue).ToList()
			: store.GetAgenda(withOverdue ? null : start, end, e?.EventID, includeClosedCases: true, today: today)
				.Where(x => x.Date >= start || (withOverdue && x.IsOverdue)).ToList();
		int pages = Math.Max(1, (sorted.Count + pageSize - 1) / pageSize);
		List<AgendaEntry> entries = sorted.Skip((page - 1) * pageSize).Take(pageSize).Select(x => new AgendaEntry(Describe.Date(x.Date), x.Kind.ToString(),
			Describe.Shorten(x.Title, 160), Describe.Shorten(x.Detail), x.Address, x.AssessmentId?.ToString(), x.PropertyId?.ToString(), x.CaseId?.ToString(),
			x.EventId?.ToString(), x.IsOverdue, x.DaysOverdue)).ToList();
		string next = page < pages ? $"Showing page {page} of {pages}. For more, call agenda again with page = {page + 1} and the same arguments." : null;
		string window = $"{Describe.Date(start)} to {Describe.Date(end)}";
		string note = overdueOnly
			? (sorted.Count == 0 ? "No open compliance case is past its deadline." : "Only open compliance deadlines that have passed are listed, whatever the dates.")
			: sorted.Count == 0
				? $"Nothing is dated from {window}{(withOverdue ? ", and no open compliance case is past its deadline" : "")}."
				: withOverdue ? "Open compliance deadlines that have already passed are listed too, marked overdue." : null;
		return new AgendaResult(sde.DatabasePath, e == null ? "the whole database" : $"the event {e.Label}", overdueOnly ? null : Describe.Date(start),
			overdueOnly ? null : Describe.Date(end), overdueOnly, sorted.Count, sorted.Count(x => x.IsOverdue), page, pageSize, pages, entries, next, note);
	}
}
