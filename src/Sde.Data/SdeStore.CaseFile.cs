using System.Data;
using System.Globalization;
using Dapper;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Improvements;
using Sde.Core.Records;
using Sde.Data.Infrastructure;

namespace Sde.Data;

/// <summary>What a line of a structure's case file timeline is about.</summary>
public enum CaseFileEntryKind
{
	/// <summary>An assessment's inspection (its date of inspection).</summary>
	Inspection,

	/// <summary>An assessment's review status changed (Reviewed, Final, Letter sent, reopened).</summary>
	Status,

	/// <summary>An assessment was put in an event or taken out of one.</summary>
	EventMembership,

	/// <summary>A letter to the owner was written.</summary>
	LetterWritten,

	/// <summary>A letter was marked as sent.</summary>
	LetterSent,

	AppealReceived,

	AppealDecided,

	/// <summary>A contractor's bid sent in with an appeal (its bid date).</summary>
	ContractorBid,

	/// <summary>An improvement project (permit) of the structure.</summary>
	ImprovementProject,

	/// <summary>A substantial improvement determination.</summary>
	SiDetermination,

	/// <summary>A document added to the structure.</summary>
	Document,

	/// <summary>A reviewer's comment or reply.</summary>
	Comment,

	/// <summary>A line of a compliance case's timeline.</summary>
	Compliance,

	/// <summary>The structure was merged with a duplicate, or a merge was undone.</summary>
	Merge,

	/// <summary>Something was moved to Recently deleted or restored from it.</summary>
	RecentlyDeleted,

	/// <summary>Any other recorded event, such as "Assessment created" or an import.</summary>
	HistoryEvent,

	/// <summary>A change to one field (edit history). <see cref="CaseFileEntry.IsEdit"/> is true.</summary>
	Edit,
}

/// <summary>One dated line of a structure's case file timeline.</summary>
public sealed class CaseFileEntry
{
	public CaseFileEntryKind Kind { get; init; }

	/// <summary>When it happened. For a date-only entry (<see cref="IsDateOnly"/>) the time is midnight.</summary>
	public DateTime When { get; init; }

	/// <summary>True when only the day is known (an inspection date, a letter's sent date, a bid date).</summary>
	public bool IsDateOnly { get; init; }

	/// <summary>A short heading, e.g. "Letter sent: Substantial damage".</summary>
	public string Title { get; init; }

	/// <summary>More detail, or null.</summary>
	public string Detail { get; init; }

	/// <summary>Who did it (an inspector, a reviewer, the person who saved), or null.</summary>
	public string Who { get; init; }

	/// <summary>The assessment it belongs to, or null for the structure itself.</summary>
	public Guid? AssessmentId { get; init; }

	/// <summary>The event of that assessment, or null.</summary>
	public Guid? EventId { get; init; }

	/// <summary>The compliance case it belongs to, or null.</summary>
	public Guid? CaseId { get; init; }

	/// <summary>True for a change to a field (edit history); false for something that happened.</summary>
	public bool IsEdit { get; init; }
}

/// <summary>One assessment of a structure in its case file, with what hangs off it.</summary>
public sealed class CaseFileAssessment
{
	public AssessmentRecord Assessment { get; init; }

	/// <summary>Its review status (a Draft when it has none).</summary>
	public AssessmentStatusRecord Status { get; init; }

	/// <summary>The event it belongs to, or null.</summary>
	public EventRecord Event { get; init; }

	/// <summary>True when it is in Recently deleted. It is still listed, marked as such.</summary>
	public bool IsTrashed { get; init; }

	/// <summary>How many photos it has (not counting photos in Recently deleted).</summary>
	public int PhotoCount { get; init; }

	/// <summary>Its letters, newest first.</summary>
	public IReadOnlyList<LetterLogRecord> Letters { get; init; } = Array.Empty<LetterLogRecord>();

	/// <summary>Its appeal, or null.</summary>
	public AppealRecord Appeal { get; init; }

	/// <summary>Its contractor bids, oldest first.</summary>
	public IReadOnlyList<ContractorBidRecord> Bids { get; init; } = Array.Empty<ContractorBidRecord>();

	/// <summary>Its review comments and replies, oldest first.</summary>
	public IReadOnlyList<ReviewCommentRecord> Comments { get; init; } = Array.Empty<ReviewCommentRecord>();
}

/// <summary>A compliance case of the structure with its own timeline (oldest first).</summary>
public sealed class CaseFileCompliance
{
	public ComplianceCaseRecord Case { get; init; }

	public IReadOnlyList<ComplianceEntryRecord> Entries { get; init; } = Array.Empty<ComplianceEntryRecord>();
}

/// <summary>An assessment's appeal and the contractor bids sent with it (either may be missing, not both).</summary>
public sealed class CaseFileAppeal
{
	public Guid AssessmentId { get; init; }

	/// <summary>The appeal, or null when only bids were recorded.</summary>
	public AppealRecord Appeal { get; init; }

	public IReadOnlyList<ContractorBidRecord> Bids { get; init; } = Array.Empty<ContractorBidRecord>();
}

/// <summary>
/// Everything on record about one structure, across every event: who and where it is, its current result, each
/// assessment, compliance, letters, appeals, improvements, documents, comments and edit history, and one timeline of
/// all of it, newest first. Read with <see cref="SdeStore.GetCaseFile"/>.
/// </summary>
public sealed class CaseFile
{
	public PropertyRecord Property { get; init; }

	/// <summary>"123 River Street", or "Structure with no address".</summary>
	public string Address { get; init; }

	/// <summary>"Exampleville, Ohio 43702" (the parts that are known).</summary>
	public string CityStateZip { get; init; }

	/// <summary>"Pat Example" (first name, then last).</summary>
	public string Owner { get; init; }

	public string ParcelNumber { get; init; }

	public string CommunityId { get; init; }

	public string CommunityName { get; init; }

	/// <summary>The latest assessment in use (by inspection date), whose result is the structure's current result; null when there is none.</summary>
	public CaseFileAssessment Latest { get; init; }

	/// <summary>Cumulative damage across the structure's events, or null when Settings has it turned off.</summary>
	public CumulativeResult Cumulative { get; init; }

	/// <summary>Every assessment, newest inspection first, including those in Recently deleted (marked <see cref="CaseFileAssessment.IsTrashed"/>).</summary>
	public IReadOnlyList<CaseFileAssessment> Assessments { get; init; } = Array.Empty<CaseFileAssessment>();

	/// <summary>The structure's compliance cases (open ones first, then by deadline).</summary>
	public IReadOnlyList<CaseFileCompliance> ComplianceCases { get; init; } = Array.Empty<CaseFileCompliance>();

	/// <summary>Every letter of every assessment, newest first.</summary>
	public IReadOnlyList<LetterLogRecord> Letters { get; init; } = Array.Empty<LetterLogRecord>();

	/// <summary>Assessments with an appeal or contractor bids, newest inspection first.</summary>
	public IReadOnlyList<CaseFileAppeal> Appeals { get; init; } = Array.Empty<CaseFileAppeal>();

	/// <summary>Improvement projects, newest first.</summary>
	public IReadOnlyList<ImprovementProjectRecord> Improvements { get; init; } = Array.Empty<ImprovementProjectRecord>();

	/// <summary>Substantial improvement determinations, newest first.</summary>
	public IReadOnlyList<SIDeterminationRecord> SiDeterminations { get; init; } = Array.Empty<SIDeterminationRecord>();

	/// <summary>Documents kept with the structure, newest first.</summary>
	public IReadOnlyList<DocumentRecord> Documents { get; init; } = Array.Empty<DocumentRecord>();

	/// <summary>Photos per assessment (assessments with none are left out).</summary>
	public IReadOnlyDictionary<Guid, int> PhotoCounts { get; init; } = new Dictionary<Guid, int>();

	/// <summary>Every review comment and reply, oldest first.</summary>
	public IReadOnlyList<ReviewCommentRecord> Comments { get; init; } = Array.Empty<ReviewCommentRecord>();

	/// <summary>The structure's whole edit history (its own fields and every assessment), newest first.</summary>
	public IReadOnlyList<EditHistoryRecord> History { get; init; } = Array.Empty<EditHistoryRecord>();

	/// <summary>Everything above as one list, newest first. Field changes are marked <see cref="CaseFileEntry.IsEdit"/>.</summary>
	public IReadOnlyList<CaseFileEntry> Timeline { get; init; } = Array.Empty<CaseFileEntry>();
}

public sealed partial class SdeStore
{
	/// <summary>
	/// History events that the case file already shows from the records themselves (a letter, an appeal, a bid, a
	/// project, a document), so the timeline doesn't list them twice.
	/// </summary>
	private static readonly string[] CoveredHistoryEvents =
	{
		"Letter written:", "Letter sent:", "Appeal received", "Contractor bid added:", "Contractor bid changed:",
		"Improvement project added:", "SI determination:", "Document added:",
	};

	/// <summary>
	/// The case file of one structure: everything on record about it across every event, read in one go over one
	/// connection. Assessments in Recently deleted are listed and marked. Refuses a structure that doesn't exist or is
	/// itself in Recently deleted.
	/// </summary>
	public CaseFile GetCaseFile(Guid propertyId)
	{
		ReferenceData reference = Reference;
		return Database.Read(c =>
		{
			PropertyRecord p = TableMap<PropertyRecord>.Get(c, new { PropertyID = propertyId }) ?? throw new ArgumentException("That structure no longer exists.");
			if (!p.Active)
			{
				throw new ArgumentException("That structure is in Recently deleted. Restore it first to see its case file.");
			}

			List<AssessmentRecord> assessments = TableMap<AssessmentRecord>.Select(c, "PropertyID = @propertyId", new { propertyId },
				orderBy: "DateofInspection DESC, LastUpdate DESC").ToList();
			List<Guid> ids = assessments.Select(a => a.AssessmentID).ToList();
			Dictionary<Guid, AssessmentStatusRecord> statuses = TableMap<AssessmentStatusRecord>.Select(c, "AssessmentID IN @ids", new { ids = IdText(ids) }).ToDictionary(s => s.AssessmentID);
			Dictionary<Guid, Guid> memberships = TableMap<EventAssessmentRecord>.Select(c, "AssessmentID IN @ids", new { ids = IdText(ids) }).ToDictionary(r => r.AssessmentID, r => r.EventID);
			List<Guid> eventIds = memberships.Values.Distinct().ToList();
			Dictionary<Guid, EventRecord> events = TableMap<EventRecord>.Select(c, "EventID IN @eventIds", new { eventIds = IdText(eventIds) }).ToDictionary(e => e.EventID);
			Dictionary<Guid, int> photos = c.Query<OpenCountRow>(
				"SELECT AssessmentID, COUNT(*) AS Count FROM Attachment WHERE Active = 1 AND AssessmentID IN @ids GROUP BY AssessmentID", new { ids = IdText(ids) })
				.ToDictionary(r => r.AssessmentID, r => r.Count);
			List<LetterLogRecord> letters = GetLetters(c, ids);
			Dictionary<Guid, AppealRecord> appeals = GetAppeals(c, ids).ToDictionary(a => a.AssessmentID);
			List<ContractorBidRecord> bids = GetBids(c, ids);
			List<ReviewCommentRecord> comments = GetReviewComments(c, ids);
			List<ComplianceCaseRecord> cases = TableMap<ComplianceCaseRecord>.Select(c, "PropertyID = @propertyId", new { propertyId },
				orderBy: "CASE WHEN Status = 'Closed' THEN 1 ELSE 0 END, COALESCE(Deadline, '9999-12-31'), CreatedOn").ToList();
			List<ComplianceEntryRecord> entries = GetComplianceEntries(c, cases.Select(x => x.CaseID).ToList());
			List<ImprovementProjectRecord> projects = GetImprovementProjects(c, propertyId);
			List<SIDeterminationRecord> determinations = GetSiDeterminations(c, propertyId);
			List<DocumentRecord> documents = GetDocuments(c, propertyId);
			List<EditHistoryRecord> history = GetPropertyHistory(c, propertyId);
			CumulativeSettings cumulativeSettings = GetCumulativeSettings(c);

			List<CaseFileAssessment> list = assessments.Select(a => new CaseFileAssessment
			{
				Assessment = a,
				Status = statuses.TryGetValue(a.AssessmentID, out AssessmentStatusRecord s) ? s : new AssessmentStatusRecord { AssessmentID = a.AssessmentID },
				Event = memberships.TryGetValue(a.AssessmentID, out Guid eid) && events.TryGetValue(eid, out EventRecord e) ? e : null,
				IsTrashed = !a.Active,
				PhotoCount = photos.TryGetValue(a.AssessmentID, out int n) ? n : 0,
				Letters = letters.Where(l => l.AssessmentID == a.AssessmentID).ToList(),
				Appeal = appeals.TryGetValue(a.AssessmentID, out AppealRecord ap) ? ap : null,
				Bids = bids.Where(b => b.AssessmentID == a.AssessmentID).ToList(),
				Comments = comments.Where(x => x.AssessmentID == a.AssessmentID).ToList(),
			}).ToList();

			CumulativeResult cumulative = null;
			if (cumulativeSettings.Enabled)
			{
				try
				{
					cumulative = CumulativeDamage.Evaluate(assessments.Where(a => a.Active).ToList(), cumulativeSettings);
				}
				catch (ArgumentException)
				{
					cumulative = null;
				}
			}

			return new CaseFile
			{
				Property = p,
				Address = AddressLabel(p),
				CityStateZip = JoinParts(" ", JoinParts(", ", p.City, reference.StateName(p.StateID)), p.ZipCode),
				Owner = JoinParts(" ", p.OwnerFirstName, p.OwnerLastName),
				ParcelNumber = p.ParcelNumber?.Trim(),
				CommunityId = p.NFIPCID?.Trim(),
				CommunityName = p.NFIPCommunityName?.Trim(),
				Latest = list.FirstOrDefault(a => !a.IsTrashed),
				Cumulative = cumulative,
				Assessments = list,
				ComplianceCases = cases.Select(x => new CaseFileCompliance { Case = x, Entries = entries.Where(en => en.CaseID == x.CaseID).ToList() }).ToList(),
				Letters = letters,
				Appeals = list.Where(a => a.Appeal != null || a.Bids.Count > 0)
					.Select(a => new CaseFileAppeal { AssessmentId = a.Assessment.AssessmentID, Appeal = a.Appeal, Bids = a.Bids }).ToList(),
				Improvements = projects,
				SiDeterminations = determinations,
				Documents = documents,
				PhotoCounts = photos,
				Comments = comments,
				History = history,
				Timeline = BuildTimeline(list, cases, entries, projects, determinations, documents, history, memberships),
			};
		});
	}

	/// <summary>Every dated thing in the case file as one list, newest first.</summary>
	private static List<CaseFileEntry> BuildTimeline(List<CaseFileAssessment> assessments, List<ComplianceCaseRecord> cases, List<ComplianceEntryRecord> entries,
		List<ImprovementProjectRecord> projects, List<SIDeterminationRecord> determinations, List<DocumentRecord> documents, List<EditHistoryRecord> history,
		Dictionary<Guid, Guid> memberships)
	{
		CultureInfo us = CultureInfo.GetCultureInfo("en-US");
		List<CaseFileEntry> timeline = new List<CaseFileEntry>();
		Guid? EventOf(Guid? assessmentId) => assessmentId is Guid id && memberships.TryGetValue(id, out Guid e) ? e : null;
		void Add(CaseFileEntryKind kind, DateTime when, bool dateOnly, string title, string detail, string who, Guid? assessmentId, Guid? caseId = null, bool isEdit = false)
		{
			timeline.Add(new CaseFileEntry
			{
				Kind = kind,
				When = dateOnly ? when.Date : when,
				IsDateOnly = dateOnly,
				Title = title,
				Detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim(),
				Who = string.IsNullOrWhiteSpace(who) ? null : who.Trim(),
				AssessmentId = assessmentId,
				EventId = EventOf(assessmentId),
				CaseId = caseId,
				IsEdit = isEdit,
			});
		}

		HashSet<string> loggedLetters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (CaseFileAssessment ca in assessments)
		{
			AssessmentRecord a = ca.Assessment;
			Guid id = a.AssessmentID;
			if (a.DateofInspection is DateTime inspected)
			{
				string pct = a.PercentDamage is decimal p ? p.ToString("0.0", us) + "% damaged" : null;
				string result = JoinParts(". ", pct, a.DamageMessage);
				string eventName = ca.Event == null ? null : "Event: " + ca.Event.Label;
				Add(CaseFileEntryKind.Inspection, inspected, true, ca.IsTrashed ? "Inspection (in Recently deleted)" : "Inspection",
					JoinParts(". ", result, eventName), a.InspectorName, id);
			}
			foreach (LetterLogRecord l in ca.Letters)
			{
				loggedLetters.Add(l.LetterID.ToString("D"));
				string kind = string.IsNullOrWhiteSpace(l.Kind) ? "letter" : l.Kind.Trim();
				if (l.GeneratedOn is DateTime written)
				{
					Add(CaseFileEntryKind.LetterWritten, written, false, "Letter written: " + kind, null, null, id);
				}
				if (l.SentOn is DateTime sent)
				{
					Add(CaseFileEntryKind.LetterSent, sent, true, "Letter sent: " + kind, null, null, id);
				}
			}
			if (ca.Appeal is AppealRecord ap)
			{
				if (ap.ReceivedOn is DateTime received)
				{
					Add(CaseFileEntryKind.AppealReceived, received, true, "Appeal received", ap.Basis, null, id);
				}
				if (ap.DecidedOn is DateTime decided && ap.Outcome != AppealOutcomes.Pending)
				{
					Add(CaseFileEntryKind.AppealDecided, decided, false, "Appeal decided: " + ap.Outcome, ap.Notes, ap.DecidedBy, id);
				}
			}
			foreach (ContractorBidRecord b in ca.Bids.Where(b => b.BidDate != null))
			{
				Add(CaseFileEntryKind.ContractorBid, b.BidDate.Value, true, "Contractor bid: " + DescribeBid(b), b.Notes, null, id);
			}
			foreach (ReviewCommentRecord r in ca.Comments)
			{
				Add(CaseFileEntryKind.Comment, r.CreatedOn, false, r.ParentID == null ? "Review comment" : "Reply to a review comment", r.Body, r.Author, id);
			}
		}
		foreach (ComplianceCaseRecord cc in cases)
		{
			foreach (ComplianceEntryRecord e in entries.Where(x => x.CaseID == cc.CaseID))
			{
				if (e.LetterID != null && loggedLetters.Contains(e.LetterID))
				{
					continue;   // the letter itself is already on the timeline
				}
				Add(CaseFileEntryKind.Compliance, e.EntryOn, false, $"Compliance ({cc.Kind}): {e.Kind}", e.Text, e.CreatedBy, cc.AssessmentID, cc.CaseID);
			}
		}
		foreach (ImprovementProjectRecord p in projects)
		{
			DateTime? when = p.ProjectDate ?? p.CreatedOn;
			if (when is DateTime w)
			{
				string cost = p.Cost is decimal money ? money.ToString("C2", us) : null;
				Add(CaseFileEntryKind.ImprovementProject, w, p.ProjectDate != null, "Improvement project: " + Describe(p),
					JoinParts(". ", cost, p.Included ? null : "Not counted" + (string.IsNullOrWhiteSpace(p.ExclusionReason) ? "" : ": " + p.ExclusionReason.Trim())), null, null);
			}
		}
		foreach (SIDeterminationRecord d in determinations)
		{
			string pct = d.Percent is decimal x ? x.ToString("0.0", us) + "%" : null;
			Add(CaseFileEntryKind.SiDetermination, d.DeterminedOn, false, "SI determination: " + (d.Result ?? "recorded"), JoinParts(". ", pct, d.Notes), d.DeterminedBy, null);
		}
		foreach (DocumentRecord d in documents.Where(d => d.AddedOn != null))
		{
			Add(CaseFileEntryKind.Document, d.AddedOn.Value, false, "Document added: " + (d.Title ?? d.OriginalName ?? "document"), null, d.AddedBy, d.AssessmentID);
		}
		foreach (EditHistoryRecord h in history)
		{
			string label = h.Label ?? h.Field ?? "";
			if (h.Field == null)
			{
				if (CoveredHistoryEvents.Any(prefix => label.StartsWith(prefix, StringComparison.Ordinal)))
				{
					continue;
				}
				CaseFileEntryKind kind = label.StartsWith("Merge", StringComparison.Ordinal) || label.StartsWith("Moved here from", StringComparison.Ordinal)
					|| label.StartsWith("Moved back from", StringComparison.Ordinal)
					? CaseFileEntryKind.Merge
					: label.Contains("Recently deleted", StringComparison.Ordinal) || label == "Photo restored"
						? CaseFileEntryKind.RecentlyDeleted
						: CaseFileEntryKind.HistoryEvent;
				Add(kind, h.ChangedOn, false, label, null, h.ChangedBy, h.AssessmentID);
				continue;
			}
			switch (h.Field)
			{
				case "Status":
					Add(CaseFileEntryKind.Status, h.ChangedOn, false, label, null, h.ChangedBy, h.AssessmentID);
					break;
				case "Event":
					Add(CaseFileEntryKind.EventMembership, h.ChangedOn, false, label, null, h.ChangedBy, h.AssessmentID);
					break;
				case "Appeal.Outcome":
					break;   // the appeal's decision is already on the timeline
				default:
					Add(CaseFileEntryKind.Edit, h.ChangedOn, false, label, $"From {ShowValue(h.OldValue)} to {ShowValue(h.NewValue)}" + (string.IsNullOrEmpty(h.Via) || h.Via == SaveVia.App ? "" : $" ({h.Via})"),
						h.ChangedBy, h.AssessmentID, isEdit: true);
					break;
			}
		}
		// Newest first; on the same moment, keep the order they were gathered in.
		return timeline.Select((e, i) => (e, i)).OrderByDescending(x => x.e.When).ThenBy(x => x.i).Select(x => x.e).ToList();
	}

	/// <summary>IDs as text for an "IN @ids" list: Dapper's list expansion doesn't use the GUID type handler.</summary>
	internal static List<string> IdText(IEnumerable<Guid> ids) => ids.Distinct().Select(g => g.ToString("D")).ToList();

	private static string ShowValue(string v) => string.IsNullOrWhiteSpace(v) ? "(blank)" : v.Trim();

	/// <summary>Joins the parts that aren't blank; null when none is.</summary>
	private static string JoinParts(string separator, params string[] parts)
	{
		string text = string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()));
		return text.Length == 0 ? null : text;
	}
}
