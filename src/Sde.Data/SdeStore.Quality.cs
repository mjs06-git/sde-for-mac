using System.Data;
using Dapper;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Quality;
using Sde.Core.Records;
using Sde.Data.Infrastructure;

namespace Sde.Data;

public sealed partial class SdeStore
{
	/// <summary>
	/// What the quality checks need from the database, read in one go: the assessments (null: every active assessment
	/// of an active structure), the depth-table suggestions nobody has accepted, which event each assessment is in,
	/// the events' names and the findings set aside. The flood map zones come from the structures' stored flood lookups
	/// (a lookup made where the structure no longer is, is stale and left out), and the high-water mark estimates from the
	/// marks stored with each assessment's event (see <see cref="HighWaterMarksFor"/>). Nothing goes online: what isn't
	/// stored isn't checked, and <see cref="QaRules.NotChecked"/> says so. Other marks (a file) can replace the estimates
	/// with <c>input with { HighWaterMarkEstimates = ..., AssessmentsWithoutMarks = null }</c>.
	/// </summary>
	public QaInput LoadQaInput(IReadOnlyCollection<Guid> assessmentIds = null, QaOptions options = null)
	{
		QaInput input = LoadQaRecords(assessmentIds, options ?? new QaOptions());
		Dictionary<Guid, decimal> estimates = StoredMarkEstimates(input, out List<Guid> without);
		return input with { HighWaterMarkEstimates = estimates, AssessmentsWithoutMarks = estimates == null ? null : without };
	}

	private QaInput LoadQaRecords(IReadOnlyCollection<Guid> assessmentIds, QaOptions options)
	{
		// Read first: loading the reference data takes a connection of its own.
		ReferenceData reference = Reference;
		return Database.Read(c =>
		{
			List<Guid> ids = assessmentIds?.Distinct().ToList() ?? c.Query<Guid>(
				"SELECT a.AssessmentID FROM Assessment a JOIN Property p ON p.PropertyID = a.PropertyID WHERE a.Active = 1 AND p.Active = 1 ORDER BY a.DateofInspection DESC").ToList();
			List<AssessmentFile> files = ids.Select(id => LoadAssessment(c, null, id)).Where(f => f != null).ToList();
			Dictionary<Guid, int> suggestions = c.Query<CountRow>("SELECT AssessmentID AS Id, COUNT(*) AS Count FROM DamageSuggestion GROUP BY AssessmentID")
				.ToDictionary(r => r.Id, r => r.Count);
			Dictionary<Guid, Guid> eventOf = c.Query<EventLinkRow>("SELECT AssessmentID, EventID FROM EventAssessment")
				.ToDictionary(r => r.AssessmentID, r => r.EventID);
			Dictionary<Guid, QaEvent> events = c.Query<EventNameRow>("SELECT EventID, Name FROM Event")
				.ToDictionary(r => r.EventID, r => new QaEvent(r.EventID, r.Name));
			List<QaDismissalRecord> dismissals = TableMap<QaDismissalRecord>.Select(c).ToList();
			Dictionary<Guid, PropertyRecord> structures = new Dictionary<Guid, PropertyRecord>();
			foreach (AssessmentFile f in files)
			{
				structures[f.Property.PropertyID] = f.Property;
			}
			Dictionary<Guid, string> zones = new Dictionary<Guid, string>();
			List<Guid> stale = new List<Guid>();
			foreach (FloodLookupRecord lookup in GetFloodLookups(c, null, structures.Keys).Values)
			{
				PropertyRecord p = structures[lookup.PropertyID];
				if (lookup.IsStaleFor(p.Latitude, p.Longitude, options.FloodLookupMoveMeters))
				{
					stale.Add(p.PropertyID);
				}
				else if (!string.IsNullOrWhiteSpace(lookup.Zone))
				{
					zones[p.PropertyID] = lookup.Zone;
				}
			}
			return new QaInput
			{
				Assessments = files,
				Reference = reference,
				SuggestionCounts = suggestions,
				EventOf = eventOf,
				Events = events,
				Dismissals = dismissals,
				NfhlZones = zones,
				StaleFloodLookups = stale,
				Options = options,
			};
		});
	}

	/// <summary>
	/// The high-water mark estimates from the marks stored for each assessment's event (or for every event), and the
	/// assessments with no marks to use. Null when there are no marks for any of them.
	/// </summary>
	private Dictionary<Guid, decimal> StoredMarkEstimates(QaInput input, out List<Guid> without)
	{
		without = new List<Guid>();
		if (input.Assessments.Count == 0 || GetHighWaterMarkSets().Count == 0)
		{
			return null;
		}
		Dictionary<Guid, StoredHighWaterMarks> byEvent = new Dictionary<Guid, StoredHighWaterMarks>();
		StoredHighWaterMarks forEvery = HighWaterMarksFor(null);
		Dictionary<Guid, (StoredHighWaterMarks Marks, List<Guid> Ids)> bySet = new Dictionary<Guid, (StoredHighWaterMarks, List<Guid>)>();
		foreach (AssessmentFile f in input.Assessments)
		{
			Guid id = f.Assessment.AssessmentID;
			StoredHighWaterMarks marks = forEvery;
			if (input.EventOf != null && input.EventOf.TryGetValue(id, out Guid eventId))
			{
				if (!byEvent.TryGetValue(eventId, out marks))
				{
					marks = HighWaterMarksFor(eventId);
					byEvent[eventId] = marks;
				}
			}
			if (marks == null)
			{
				without.Add(id);
				continue;
			}
			if (!bySet.TryGetValue(marks.Set.SetID, out (StoredHighWaterMarks Marks, List<Guid> Ids) group))
			{
				group = (marks, new List<Guid>());
				bySet[marks.Set.SetID] = group;
			}
			group.Ids.Add(id);
		}
		if (bySet.Count == 0)
		{
			return null;
		}
		Dictionary<Guid, decimal> estimates = new Dictionary<Guid, decimal>();
		foreach ((StoredHighWaterMarks marks, List<Guid> ids) in bySet.Values)
		{
			foreach (KeyValuePair<Guid, decimal> e in DepthFromMarks.QaEstimates(this, ids, marks.Marks))
			{
				estimates[e.Key] = e.Value;
			}
		}
		return estimates;
	}

	/// <summary>Every quality finding someone has set aside.</summary>
	public List<QaDismissalRecord> GetQaDismissals() =>
		Database.Read(c => TableMap<QaDismissalRecord>.Select(c, orderBy: "DismissedOn").ToList());

	/// <summary>Sets a finding aside with a reason (see <see cref="DismissQaFinding(string, Guid, string, string, SaveContext)"/>).</summary>
	public QaDismissalRecord DismissQaFinding(QaFinding finding, string reason, SaveContext context = null) =>
		DismissQaFinding(finding.RuleId, finding.ObjectId, finding.Detail, reason, context);

	/// <summary>
	/// Sets a quality finding aside: it stays listed as dismissed, with the reason, who and when. A reason is required,
	/// and checks that block making an assessment Final (unaccepted suggestions) can't be set aside. Dismissing the
	/// same finding again replaces the reason. A change to what was checked gives a new finding, which shows again.
	/// </summary>
	public QaDismissalRecord DismissQaFinding(string ruleId, Guid objectId, string detail, string reason, SaveContext context = null)
	{
		QaRuleInfo rule = QaRules.Rule(ruleId) ?? throw new ArgumentException($"There's no quality check called \"{ruleId}\".");
		if (!rule.CanDismiss)
		{
			throw new InvalidOperationException($"\"{rule.Title}\" can't be set aside. Fix it instead.");
		}
		if (string.IsNullOrWhiteSpace(reason))
		{
			throw new ArgumentException("Say why this check is being set aside.");
		}
		SaveContext ctx = context ?? SaveContext.App;
		QaDismissalRecord record = new QaDismissalRecord
		{
			RuleId = rule.Id,
			ObjectID = objectId,
			Detail = detail ?? "",
			Reason = reason.Trim(),
			DismissedOn = Clock(),
			DismissedBy = ctx.User ?? UserName,
		};
		Database.InTransaction((c, tx) =>
		{
			TableMap<QaDismissalRecord>.Upsert(c, record, tx);
			QaHistory(c, tx, objectId, $"Quality check set aside: {rule.Title} ({record.Reason})", ctx);
		});
		return record;
	}

	/// <summary>Brings a dismissed finding back (recorded in edit history). Returns false when it wasn't dismissed.</summary>
	public bool RemoveQaDismissal(string ruleId, Guid objectId, string detail, SaveContext context = null) => Database.InTransaction((c, tx) =>
	{
		bool removed = c.Execute("DELETE FROM QaDismissal WHERE RuleId = @ruleId AND ObjectID = @objectId AND Detail = @detail",
			new { ruleId, objectId, detail = detail ?? "" }, tx) > 0;
		if (removed)
		{
			QaHistory(c, tx, objectId, "Quality check brought back: " + (QaRules.Rule(ruleId)?.Title ?? ruleId), context ?? SaveContext.App);
		}
		return removed;
	});

	/// <summary>
	/// Records a dismissal in the edit history of what the finding was about: an assessment, or a structure. Nothing is
	/// written when it is neither (it was deleted).
	/// </summary>
	private void QaHistory(IDbConnection c, IDbTransaction tx, Guid objectId, string label, SaveContext ctx)
	{
		Guid? propertyOfAssessment = c.ExecuteScalar<Guid?>("SELECT PropertyID FROM Assessment WHERE AssessmentID = @objectId", new { objectId }, tx);
		if (propertyOfAssessment is Guid p)
		{
			WriteEvent(c, tx, p, objectId, label, ctx);
		}
		else if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM Property WHERE PropertyID = @objectId", new { objectId }, tx) > 0)
		{
			WriteEvent(c, tx, objectId, null, label, ctx);
		}
	}

	private sealed class CountRow
	{
		public Guid Id { get; set; }
		public int Count { get; set; }
	}

	private sealed class EventLinkRow
	{
		public Guid AssessmentID { get; set; }
		public Guid EventID { get; set; }
	}

	private sealed class EventNameRow
	{
		public Guid EventID { get; set; }
		public string Name { get; set; }
	}
}
