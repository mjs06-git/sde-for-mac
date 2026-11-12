using System.Data;
using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data.Infrastructure;

namespace Sde.Data;

/// <summary>How many of one kind of thing a merge moves, e.g. ("Assessments", 2).</summary>
public sealed record MergeCount(string What, int Count);

/// <summary>
/// A structure field a merge looks at: one it fills in on the kept structure (KeptValue is then blank), or one where the
/// two structures differ and the kept structure's value stays.
/// </summary>
public sealed record MergeField(string Key, string Label, string KeptValue, string OtherValue);

/// <summary>
/// What merging one structure into another would do, for the person to read before agreeing: what moves, which blank
/// fields are filled in, where the two differ, what stays with the removed structure, and why the merge can't be done
/// (when <see cref="Conflicts"/> isn't empty). Nothing is changed by making a preview.
/// </summary>
public sealed class MergePreview
{
	public Guid KeepPropertyID { get; init; }

	public Guid MergePropertyID { get; init; }

	/// <summary>The kept structure's address, as Recently deleted and history show it.</summary>
	public string KeepLabel { get; set; }

	/// <summary>The address of the structure that goes to Recently deleted.</summary>
	public string MergeLabel { get; set; }

	/// <summary>The structures' versions when the preview was made: the merge is refused if either has changed since.</summary>
	public long KeepVersion { get; set; }

	public long MergeVersion { get; set; }

	/// <summary>What moves to the kept structure (only kinds with at least one row).</summary>
	public List<MergeCount> Moving { get; } = new();

	/// <summary>Blank fields of the kept structure that get the other structure's value.</summary>
	public List<MergeField> Filled { get; } = new();

	/// <summary>Fields both structures have with different values. The kept structure's value stays.</summary>
	public List<MergeField> Differences { get; } = new();

	/// <summary>What stays with the removed structure (and so goes to Recently deleted with it).</summary>
	public List<string> StayingBehind { get; } = new();

	/// <summary>Things worth checking that don't stop the merge.</summary>
	public List<string> Warnings { get; } = new();

	/// <summary>Why the merge can't be done. Empty when it can.</summary>
	public List<string> Conflicts { get; } = new();

	public bool CanMerge => Conflicts.Count == 0;
}

/// <summary>What a merge did. <see cref="BatchId"/> is the edit history batch, which is also what undoes it.</summary>
public sealed class MergePropertiesResult
{
	public string BatchId { get; init; }

	public Guid KeepPropertyID { get; init; }

	public Guid MergePropertyID { get; init; }

	/// <summary>The removed structure's key in Recently deleted.</summary>
	public string DeletedItemKey { get; init; }

	public IReadOnlyList<MergeCount> Moved { get; init; }

	public IReadOnlyList<MergeField> Filled { get; init; }
}

/// <summary>What undoing a merge did.</summary>
public sealed record MergeUndoResult(string BatchId, Guid KeepPropertyID, Guid RestoredPropertyID, int AssessmentsMovedBack);

/// <summary>Thrown when a merge, or undoing one, is refused. Nothing has been changed.</summary>
public sealed class MergeRefusedException : InvalidOperationException
{
	public MergeRefusedException(IReadOnlyList<string> reasons)
		: base(string.Join(" ", reasons))
	{
		Reasons = reasons;
	}

	public MergeRefusedException(string reason)
		: this(new[] { reason })
	{
	}

	public IReadOnlyList<string> Reasons { get; }
}

/// <summary>
/// Merging duplicate structures (the Review page's "same parcel / address / location" findings). Everything that belongs
/// to the duplicate moves to the structure being kept in one transaction: its assessments (unchanged, results and all),
/// documents, compliance cases, improvement projects and SI determinations, links to other systems, field assignments,
/// tags, its stored flood lookup when the kept structure has none, and with the assessments their photos, comments,
/// letters, statuses and history. Blank fields of the kept
/// structure are filled in from the duplicate; no value is ever overwritten. The empty duplicate then goes to Recently
/// deleted. Every row the merge changes is recorded (MacSetting "Merge.&lt;batch&gt;"), so the merge can be undone
/// exactly, putting every row back on the structure it came from.
/// </summary>
public sealed partial class SdeStore
{
	/// <summary>Structure fields the damage calculation reads (AssessmentCalculator.BuildInput); a blank one counts as 0 there.</summary>
	private static readonly HashSet<string> CalculationFields = new(StringComparer.Ordinal)
	{
		nameof(PropertyRecord.StructureTypeID), nameof(PropertyRecord.ResidenceTypeID), nameof(PropertyRecord.FoundationTypeID),
		nameof(PropertyRecord.SuperStructureTypeID), nameof(PropertyRecord.RoofCoveringTypeID), nameof(PropertyRecord.ExteriorFinishTypeID),
		nameof(PropertyRecord.HVACTypeID), nameof(PropertyRecord.StoryTypeID), nameof(PropertyRecord.StructureUseID),
		nameof(PropertyRecord.SprinklerSystem), nameof(PropertyRecord.Conveyance), nameof(PropertyRecord.SquareFootage),
	};

	/// <summary>
	/// Fields that are filled in together or not at all: half of one structure's address with half of the other's would
	/// describe neither building.
	/// </summary>
	private static readonly string[][] FillGroups =
	{
		new[] { nameof(PropertyRecord.StreetNumber), nameof(PropertyRecord.StreetName), nameof(PropertyRecord.StreetSuffixID) },
		new[] { nameof(PropertyRecord.OwnerFirstName), nameof(PropertyRecord.OwnerLastName) },
		new[] { nameof(PropertyRecord.MailingOwnerFirstName), nameof(PropertyRecord.MailingOwnerLastName) },
		new[]
		{
			nameof(PropertyRecord.MailingCareOf), nameof(PropertyRecord.MailingStreetNumber), nameof(PropertyRecord.MailingStreetName),
			nameof(PropertyRecord.MailingStreetSuffixID), nameof(PropertyRecord.MailingCity), nameof(PropertyRecord.MailingStateID),
			nameof(PropertyRecord.MailingCountyID), nameof(PropertyRecord.MailingZipCode),
		},
		new[] { nameof(PropertyRecord.Latitude), nameof(PropertyRecord.Longitude) },
		new[] { nameof(PropertyRecord.FIRMPanelNumber), nameof(PropertyRecord.PanelSuffix), nameof(PropertyRecord.FIRMPanelDate) },
	};

	/// <summary>Structure columns a merge never fills: the key, Recently deleted's flag, and the record's own dates.</summary>
	private static readonly HashSet<string> NeverFilled = new(StringComparer.Ordinal)
	{
		nameof(PropertyRecord.PropertyID), nameof(PropertyRecord.Active), nameof(PropertyRecord.ImportDate), nameof(PropertyRecord.LastUpdate),
	};

	/// <summary>
	/// Rows that point at a structure by its PropertyID and simply follow it to the kept structure (table, key columns,
	/// extra condition). Rows of the duplicate's assessments follow their assessment, which keeps its ID.
	/// </summary>
	private static readonly (string Table, string[] Key, string Where)[] FollowingTables =
	{
		// Assessment rows of edit history: first, before the merge writes rows of its own. The structure's own rows stay.
		("EditHistory", new[] { "HistoryID" }, "AssessmentID IS NOT NULL"),
		("Assessment", new[] { "AssessmentID" }, null),
		("LastFiveAssesments", new[] { "AssessmentID" }, null),
		("Document", new[] { "DocumentID" }, null),
		("ComplianceCase", new[] { "CaseID" }, null),
		("ImprovementProject", new[] { "ProjectID" }, null),
		("SIDetermination", new[] { "DeterminationID" }, null),
		("StructureLink", new[] { "Source", "RecordId" }, null),
		("Assignment", new[] { "AssignmentID" }, null),
		// The duplicate's own assessments and photos in Recently deleted come back to the kept structure.
		("DeletedItem", new[] { "ItemKey" }, "Kind <> 'Structure'"),
	};

	private static string MergeKey(string batchId) => "Merge." + batchId;

	/// <summary>
	/// What merging <paramref name="mergePropertyId"/> into <paramref name="keepPropertyId"/> would do, and why it can't be
	/// done if it can't. Changes nothing.
	/// </summary>
	public MergePreview PreviewMerge(Guid keepPropertyId, Guid mergePropertyId) =>
		Database.Read(c =>
		{
			using IDbTransaction tx = c.BeginTransaction(deferred: true);
			return PlanMerge(c, tx, keepPropertyId, mergePropertyId).Preview;
		});

	/// <summary>
	/// Merges a duplicate structure into the one being kept, in one transaction and one edit history batch (see the class
	/// summary). Refused, changing nothing, for any reason <see cref="PreviewMerge"/> lists, and when
	/// <paramref name="preview"/> is given and either structure has changed since it was made. Undo with
	/// <see cref="UndoMerge"/> (or <see cref="RevertBatch"/>) and the result's BatchId.
	/// </summary>
	public MergePropertiesResult MergeProperties(Guid keepPropertyId, Guid mergePropertyId, MergePreview preview = null, SaveContext context = null)
	{
		SaveContext ctx = (context ?? SaveContext.Batch(SaveVia.Merge)).AsBatch();
		return Database.InTransaction((c, tx) =>
		{
			MergePlan plan = PlanMerge(c, tx, keepPropertyId, mergePropertyId);
			MergePreview pv = plan.Preview;
			if (!pv.CanMerge)
			{
				throw new MergeRefusedException(pv.Conflicts);
			}
			if (preview != null && (preview.KeepVersion != pv.KeepVersion || preview.MergeVersion != pv.MergeVersion))
			{
				throw new MergeRefusedException("One of the structures has been changed since the merge was previewed. Look at what the merge would do again.");
			}

			List<MergeChange> log = new List<MergeChange>();
			object keep = plan.KeepRow[nameof(PropertyRecord.PropertyID)];
			object merge = plan.MergeRow[nameof(PropertyRecord.PropertyID)];
			List<Guid> moved = c.Query<Guid>("SELECT AssessmentID FROM Assessment WHERE PropertyID = @mergePropertyId ORDER BY AssessmentID", new { mergePropertyId }, tx).ToList();

			foreach ((string table, string[] key, string where) in FollowingTables)
			{
				RepointRows(c, tx, log, table, key, "PropertyID", "PropertyID = @from" + (where == null ? "" : " AND " + where), merge, keep);
			}
			foreach (long tagId in plan.TagsToMove)
			{
				LoggedUpdate(c, tx, log, "PropertyTag", new Dictionary<string, object> { ["PropertyID"] = merge, ["TagID"] = tagId }, "PropertyID", keep);
			}
			foreach (long tagId in plan.TagsBothHave)
			{
				LoggedDelete(c, tx, log, "PropertyTag", new Dictionary<string, object> { ["PropertyID"] = merge, ["TagID"] = tagId });
			}
			if (plan.MoveFloodLookup)
			{
				LoggedUpdate(c, tx, log, "FloodLookup", new Dictionary<string, object> { ["PropertyID"] = merge }, "PropertyID", keep);
			}
			foreach (string fieldId in plan.CustomFieldsToMove)
			{
				LoggedUpdate(c, tx, log, "CustomField_Results", new Dictionary<string, object> { ["ObjectIdentifier"] = merge, ["CustomFieldID"] = fieldId }, "ObjectIdentifier", keep);
			}
			foreach ((string fieldId, object value) in plan.CustomFieldsToFill)
			{
				LoggedUpdate(c, tx, log, "CustomField_Results", new Dictionary<string, object> { ["ObjectIdentifier"] = keep, ["CustomFieldID"] = fieldId }, "Result", value);
			}
			if (plan.MoveShapes)
			{
				RepointRows(c, tx, log, "Shapes", new[] { "ShapeID" }, "PropertyID", "PropertyID = @from", merge, keep);
			}
			Dictionary<string, object> keepKey = new Dictionary<string, object> { ["PropertyID"] = keep };
			foreach (string column in plan.FillColumns)
			{
				LoggedUpdate(c, tx, log, "Property", keepKey, column, plan.MergeRow[column]);
			}
			if (plan.FillColumns.Count > 0)
			{
				LoggedUpdate(c, tx, log, "Property", keepKey, nameof(PropertyRecord.LastUpdate), Now());
			}

			// The duplicate, now empty, goes to Recently deleted the usual way.
			Dictionary<string, object> mergeKey = new Dictionary<string, object> { ["PropertyID"] = merge };
			object wasActive = RawValue(c, tx, "Property", mergeKey, nameof(PropertyRecord.Active));
			TrashProperty(c, tx, mergePropertyId, ctx, "(merged into " + pv.KeepLabel + ")");
			log.Add(new MergeChange { Table = "Property", Action = MergeChange.Update, Key = Encode(mergeKey), Column = nameof(PropertyRecord.Active),
				Old = EncodeValue(wasActive), New = EncodeValue(RawValue(c, tx, "Property", mergeKey, nameof(PropertyRecord.Active))) });
			log.Add(new MergeChange { Table = "DeletedItem", Action = MergeChange.Insert, Key = Encode(new Dictionary<string, object> { ["ItemKey"] = StructureKey(mergePropertyId) }) });

			BumpVersion(c, tx, keepPropertyId, VersionKind.Property);
			foreach (Guid aid in moved)
			{
				BumpVersion(c, tx, aid, VersionKind.Assessment);
			}
			WriteEvent(c, tx, keepPropertyId, null, "Merged with the duplicate structure at " + pv.MergeLabel, ctx);
			WriteEvent(c, tx, mergePropertyId, null, "Merged into " + pv.KeepLabel, ctx);
			foreach (Guid aid in moved)
			{
				WriteEvent(c, tx, keepPropertyId, aid, "Moved here from the duplicate structure at " + pv.MergeLabel, ctx);
			}
			WriteDiffs(c, tx, keepPropertyId, null, plan.FillDiffs, ctx);

			MergeUndoRecord undo = new MergeUndoRecord
			{
				BatchId = ctx.BatchId,
				KeepPropertyID = keepPropertyId,
				MergePropertyID = mergePropertyId,
				KeepLabel = pv.KeepLabel,
				MergeLabel = pv.MergeLabel,
				MergedOn = Clock(),
				MergedBy = ctx.User ?? UserName,
				MovedAssessments = moved,
				Changes = log,
			};
			undo.Versions[keepPropertyId] = GetVersion(c, tx, keepPropertyId);
			undo.Versions[mergePropertyId] = GetVersion(c, tx, mergePropertyId);
			foreach (Guid aid in moved)
			{
				undo.Versions[aid] = GetVersion(c, tx, aid);
			}
			TableMap<MacSettingRecord>.Upsert(c, new MacSettingRecord { Key = MergeKey(ctx.BatchId), Value = JsonConvert.SerializeObject(undo) }, tx);
			return new MergePropertiesResult
			{
				BatchId = ctx.BatchId,
				KeepPropertyID = keepPropertyId,
				MergePropertyID = mergePropertyId,
				DeletedItemKey = StructureKey(mergePropertyId),
				Moved = pv.Moving,
				Filled = pv.Filled,
			};
		});
	}

	/// <summary>True when this edit history batch is a merge that can still be undone.</summary>
	public bool IsMergeBatch(string batchId) =>
		!string.IsNullOrEmpty(batchId) && Database.Read(c => TableMap<MacSettingRecord>.Get(c, new { Key = MergeKey(batchId) })) != null;

	/// <summary>
	/// Undoes a merge exactly: every row it changed goes back to what it was, so both structures are as they were before,
	/// with their own assessments, documents, cases, tags and the rest, and the removed structure comes back from
	/// Recently deleted. Recorded in both structures' history as a new batch. Refused, changing nothing, when anything
	/// the merge touched has been changed since (an assessment edited, a filled field changed, the removed structure
	/// restored or removed for good): then the structures have to be put right by hand.
	/// </summary>
	public MergeUndoResult UndoMerge(string batchId, SaveContext context = null)
	{
		SaveContext ctx = (context ?? SaveContext.Batch(SaveVia.Undo)).AsBatch();
		return Database.InTransaction((c, tx) =>
		{
			MacSettingRecord stored = string.IsNullOrEmpty(batchId) ? null : TableMap<MacSettingRecord>.Get(c, new { Key = MergeKey(batchId) }, tx);
			if (stored == null)
			{
				throw new ArgumentException("There's no merge to undo there. It may have been undone already.");
			}
			MergeUndoRecord undo = JsonConvert.DeserializeObject<MergeUndoRecord>(stored.Value);
			if (undo.Format > MergeUndoRecord.CurrentFormat)
			{
				throw new MergeRefusedException("This merge was made by a newer version of SDE. Undo it there.");
			}
			Guid keep = undo.KeepPropertyID;
			Guid merge = undo.MergePropertyID;
			if (TableMap<PropertyRecord>.Get(c, new { PropertyID = merge }, tx) == null || TableMap<PropertyRecord>.Get(c, new { PropertyID = keep }, tx) == null)
			{
				throw new MergeRefusedException("One of the merged structures has been removed for good, so the merge can't be undone.");
			}
			foreach (KeyValuePair<Guid, long> v in undo.Versions)
			{
				if (GetVersion(c, tx, v.Key) != v.Value)
				{
					string what = v.Key == keep ? undo.KeepLabel : v.Key == merge ? undo.MergeLabel + " (in Recently deleted)" : "an assessment that was moved";
					throw new MergeRefusedException($"The merge can't be undone because {what} has been changed since. Nothing was changed.");
				}
			}
			for (int i = undo.Changes.Count - 1; i >= 0; i--)
			{
				UndoChange(c, tx, undo.Changes[i]);
			}
			BumpVersion(c, tx, keep, VersionKind.Property);
			BumpVersion(c, tx, merge, VersionKind.Property);
			foreach (Guid aid in undo.MovedAssessments)
			{
				BumpVersion(c, tx, aid, VersionKind.Assessment);
			}
			// The merge's own notes on the moved assessments go back with them.
			c.Execute("UPDATE EditHistory SET PropertyID = @merge WHERE BatchId = @batchId AND AssessmentID IS NOT NULL", new { merge, batchId }, tx);
			WriteEvent(c, tx, keep, null, $"Merge undone: {undo.MergeLabel} is a separate structure again", ctx);
			WriteEvent(c, tx, merge, null, $"Merge undone: back from {undo.KeepLabel}", ctx);
			foreach (Guid aid in undo.MovedAssessments)
			{
				WriteEvent(c, tx, merge, aid, "Moved back from " + undo.KeepLabel + " (merge undone)", ctx);
			}
			TableMap<MacSettingRecord>.Delete(c, "Key = @Key", new { stored.Key }, tx);
			return new MergeUndoResult(ctx.BatchId, keep, merge, undo.MovedAssessments.Count);
		});
	}

	// ───────────── Planning ─────────────

	/// <summary>The preview and what the merge would write.</summary>
	private sealed class MergePlan
	{
		public MergePreview Preview { get; init; }

		public IDictionary<string, object> KeepRow { get; set; }

		public IDictionary<string, object> MergeRow { get; set; }

		public List<string> FillColumns { get; } = new();

		public List<FieldDiff> FillDiffs { get; } = new();

		public bool MoveShapes { get; set; }

		public List<string> CustomFieldsToMove { get; } = new();

		public List<(string FieldId, object Value)> CustomFieldsToFill { get; } = new();

		public List<long> TagsToMove { get; } = new();

		public List<long> TagsBothHave { get; } = new();

		/// <summary>The duplicate's stored flood lookup moves (the kept structure has none of its own).</summary>
		public bool MoveFloodLookup { get; set; }
	}

	private MergePlan PlanMerge(IDbConnection c, IDbTransaction tx, Guid keepId, Guid mergeId)
	{
		MergePreview pv = new MergePreview { KeepPropertyID = keepId, MergePropertyID = mergeId };
		MergePlan plan = new MergePlan { Preview = pv };
		if (keepId == Guid.Empty || mergeId == Guid.Empty)
		{
			pv.Conflicts.Add("Choose the two structures to merge.");
			return plan;
		}
		if (keepId == mergeId)
		{
			pv.Conflicts.Add("A structure can't be merged with itself.");
			return plan;
		}
		PropertyRecord kp = TableMap<PropertyRecord>.Get(c, new { PropertyID = keepId }, tx);
		PropertyRecord mp = TableMap<PropertyRecord>.Get(c, new { PropertyID = mergeId }, tx);
		if (kp == null || mp == null)
		{
			pv.Conflicts.Add("One of the structures no longer exists.");
			return plan;
		}
		plan.KeepRow = RawRow(c, tx, "Property", new Dictionary<string, object> { ["PropertyID"] = Raw(c, tx, keepId) });
		plan.MergeRow = RawRow(c, tx, "Property", new Dictionary<string, object> { ["PropertyID"] = Raw(c, tx, mergeId) });
		pv.KeepLabel = AddressLabel(kp);
		pv.MergeLabel = AddressLabel(mp);
		pv.KeepVersion = GetVersion(c, tx, keepId);
		pv.MergeVersion = GetVersion(c, tx, mergeId);
		if (!kp.Active)
		{
			pv.Conflicts.Add($"The structure being kept ({pv.KeepLabel}) is in Recently deleted. Restore it first.");
		}
		if (!mp.Active)
		{
			pv.Conflicts.Add($"The structure being merged ({pv.MergeLabel}) is in Recently deleted. Restore it first.");
		}
		if (TableMap<MacSettingRecord>.Get(c, new { Key = WorkPackages.ManifestKey }, tx) != null)
		{
			pv.Conflicts.Add("This is a work package. Merge structures at the office, after the package is checked in.");
		}

		List<AssessmentRecord> keptAssessments = TableMap<AssessmentRecord>.Select(c, "PropertyID = @keepId", new { keepId }, tx).ToList();
		List<AssessmentRecord> mergedAssessments = TableMap<AssessmentRecord>.Select(c, "PropertyID = @mergeId", new { mergeId }, tx).ToList();
		CheckAssessmentsFree(c, tx, pv, keptAssessments, pv.KeepLabel);
		CheckAssessmentsFree(c, tx, pv, mergedAssessments, pv.MergeLabel);

		PlanFields(plan, kp, mp, keptAssessments.Count > 0, mergedAssessments.Count > 0);
		PlanShapes(c, tx, plan, kp, mp, keepId, mergeId);
		PlanCustomFields(c, tx, plan, keepId, mergeId);
		PlanRelatedRows(c, tx, plan, keepId, mergeId);
		return plan;
	}

	/// <summary>Refuses a merge that would move or change a locked or checked-out assessment's structure.</summary>
	private static void CheckAssessmentsFree(IDbConnection c, IDbTransaction tx, MergePreview pv, List<AssessmentRecord> assessments, string label)
	{
		foreach (AssessmentRecord a in assessments)
		{
			AssessmentStatusRecord status = GetStatus(c, tx, a.AssessmentID);
			string when = a.DateofInspection is DateTime d ? " inspected " + d.ToString("MMM d, yyyy", CultureInfo.GetCultureInfo("en-US")) : "";
			if (status.IsLocked)
			{
				pv.Conflicts.Add($"The assessment of {label}{when} is {AssessmentStatuses.Label(status.Value)}, and merging would change the structure it was based on. Reopen it first.");
			}
			if (TableMap<CheckOutRecord>.Get(c, new { a.AssessmentID }, tx) is CheckOutRecord r)
			{
				pv.Conflicts.Add($"The assessment of {label}{when} is checked out to {AssessmentCheckedOutException.CheckedOutTo(r)} in a work package. Check it back in first.");
			}
		}
	}

	/// <summary>Which blank fields get filled, where the two differ, and differences the damage calculation would notice.</summary>
	private void PlanFields(MergePlan plan, PropertyRecord kp, PropertyRecord mp, bool keptHasAssessments, bool mergedHasAssessments)
	{
		MergePreview pv = plan.Preview;
		List<string> columns = TableMap<PropertyRecord>.Columns.Select(p => p.Name).Where(n => !NeverFilled.Contains(n)).ToList();
		HashSet<string> grouped = FillGroups.SelectMany(g => g).ToHashSet(StringComparer.Ordinal);
		IEnumerable<string[]> groups = FillGroups.Concat(columns.Where(n => !grouped.Contains(n)).Select(n => new[] { n }));
		foreach (string[] group in groups)
		{
			bool keptBlank = group.All(n => IsBlankField(kp, n));
			foreach (string n in group)
			{
				bool kb = IsBlankField(kp, n);
				bool mb = IsBlankField(mp, n);
				if (mb)
				{
					continue;
				}
				if (keptBlank)
				{
					plan.FillColumns.Add(n);
					plan.FillDiffs.Add(new FieldDiff("Property." + n, FieldLabel(n), FieldCatalog.Canonical(FieldValue(kp, n)), FieldCatalog.Canonical(FieldValue(mp, n))));
					pv.Filled.Add(new MergeField("Property." + n, FieldLabel(n), "", ShowField(mp, n)));
				}
				else if (kb || FieldCatalog.Canonical(FieldValue(kp, n)) != FieldCatalog.Canonical(FieldValue(mp, n)))
				{
					pv.Differences.Add(new MergeField("Property." + n, FieldLabel(n), ShowField(kp, n), ShowField(mp, n)));
				}
			}
		}

		// Assessments move with their results unchanged, so the structure fields they were calculated from must be the
		// same afterwards, on both sides. Otherwise the next save would recalculate them to something else.
		foreach (string n in CalculationFields)
		{
			string k = IsBlankField(kp, n) ? "0" : FieldCatalog.Canonical(FieldValue(kp, n));
			string m = IsBlankField(mp, n) ? "0" : FieldCatalog.Canonical(FieldValue(mp, n));
			if (k == m)
			{
				continue;
			}
			string label = FieldLabel(n);
			if (IsBlankField(kp, n) && keptHasAssessments)
			{
				pv.Conflicts.Add($"{label} is blank on {pv.KeepLabel}, whose assessments were calculated without it. Filling it in from {pv.MergeLabel} ({ShowField(mp, n)}) would change what they are based on. Enter it on {pv.KeepLabel} first.");
			}
			else if (!IsBlankField(kp, n) && mergedHasAssessments)
			{
				string other = IsBlankField(mp, n) ? "blank" : ShowField(mp, n);
				pv.Conflicts.Add($"{label} is {ShowField(kp, n)} on {pv.KeepLabel} but {other} on {pv.MergeLabel}, whose assessments were calculated with it. Make it the same on both structures first, so the moved assessments' results stay right.");
			}
		}
	}

	/// <summary>Square-footage shapes move only when the kept structure has none and they add up to its square footage.</summary>
	private static void PlanShapes(IDbConnection c, IDbTransaction tx, MergePlan plan, PropertyRecord kp, PropertyRecord mp, Guid keepId, Guid mergeId)
	{
		int keptShapes = c.ExecuteScalar<int>("SELECT COUNT(*) FROM Shapes WHERE PropertyID = @keepId", new { keepId }, tx);
		int mergedShapes = c.ExecuteScalar<int>("SELECT COUNT(*) FROM Shapes WHERE PropertyID = @mergeId", new { mergeId }, tx);
		if (mergedShapes == 0)
		{
			return;
		}
		decimal? finalSqFt = plan.FillColumns.Contains(nameof(PropertyRecord.SquareFootage)) ? mp.SquareFootage : kp.SquareFootage;
		if (keptShapes == 0 && FieldCatalog.Canonical(finalSqFt) == FieldCatalog.Canonical(mp.SquareFootage))
		{
			plan.MoveShapes = true;
			plan.Preview.Moving.Add(new MergeCount("Square-footage shapes", mergedShapes));
			return;
		}
		plan.Preview.StayingBehind.Add(keptShapes > 0
			? $"Its square-footage shapes ({mergedShapes}), because {plan.Preview.KeepLabel} has its own."
			: $"Its square-footage shapes ({mergedShapes}), because they don't add up to {plan.Preview.KeepLabel}'s square footage.");
	}

	/// <summary>The structure's own custom field values: moved or filled in where the kept structure has none.</summary>
	private static void PlanCustomFields(IDbConnection c, IDbTransaction tx, MergePlan plan, Guid keepId, Guid mergeId)
	{
		// Matched on the stored text of CustomFieldID (written by the same code for both structures).
		List<IDictionary<string, object>> merged = c.Query("SELECT CustomFieldID, Result FROM CustomField_Results WHERE ObjectIdentifier = @mergeId ORDER BY CustomFieldID", new { mergeId }, tx)
			.Cast<IDictionary<string, object>>().ToList();
		int moved = 0;
		foreach (IDictionary<string, object> row in merged)
		{
			string result = row["Result"] as string;
			if (string.IsNullOrWhiteSpace(result))
			{
				continue;
			}
			string rawId = (string)row["CustomFieldID"];
			string name = c.ExecuteScalar<string>("SELECT CustomField FROM CustomField WHERE CustomFieldID = @rawId", new { rawId }, tx) ?? "Custom field";
			IDictionary<string, object> mine = c.Query("SELECT Result FROM CustomField_Results WHERE ObjectIdentifier = @keepId AND CustomFieldID = @rawId", new { keepId, rawId }, tx)
				.Cast<IDictionary<string, object>>().FirstOrDefault();
			string mineResult = mine?["Result"] as string;
			if (mine == null)
			{
				plan.CustomFieldsToMove.Add(rawId);
				plan.Preview.Filled.Add(new MergeField("CustomField." + rawId, name, "", result));
				moved++;
			}
			else if (string.IsNullOrWhiteSpace(mineResult))
			{
				plan.CustomFieldsToFill.Add((rawId, row["Result"]));
				plan.FillDiffs.Add(new FieldDiff("CustomField." + rawId, name, FieldCatalog.Canonical(mineResult), result));
				plan.Preview.Filled.Add(new MergeField("CustomField." + rawId, name, "", result));
			}
			else if (mineResult != result)
			{
				plan.Preview.Differences.Add(new MergeField("CustomField." + rawId, name, mineResult, result));
				plan.Preview.StayingBehind.Add($"Its \"{name}\" value ({result}), because {plan.Preview.KeepLabel} has its own.");
			}
		}
		if (moved > 0)
		{
			plan.Preview.Moving.Add(new MergeCount("Custom field values", moved));
		}
	}

	/// <summary>Counts what moves, and the rows that can't simply be put together.</summary>
	private void PlanRelatedRows(IDbConnection c, IDbTransaction tx, MergePlan plan, Guid keepId, Guid mergeId)
	{
		MergePreview pv = plan.Preview;
		object args = new { keepId, mergeId };
		const string mergedAssessments = "SELECT AssessmentID FROM Assessment WHERE PropertyID = @mergeId";
		void Count(string what, string sql)
		{
			int n = c.ExecuteScalar<int>(sql, args, tx);
			if (n > 0)
			{
				pv.Moving.Add(new MergeCount(what, n));
			}
		}
		Count("Assessments", "SELECT COUNT(*) FROM Assessment WHERE PropertyID = @mergeId AND Active = 1");
		Count("Assessments in Recently deleted", "SELECT COUNT(*) FROM Assessment WHERE PropertyID = @mergeId AND Active = 0");
		Count("Photos", $"SELECT COUNT(*) FROM Attachment WHERE Active = 1 AND AssessmentID IN ({mergedAssessments})");
		Count("Review comments", $"SELECT COUNT(*) FROM ReviewComment WHERE AssessmentID IN ({mergedAssessments})");
		Count("Letters", $"SELECT COUNT(*) FROM LetterLog WHERE AssessmentID IN ({mergedAssessments})");
		Count("Contractor bids", $"SELECT COUNT(*) FROM ContractorBid WHERE AssessmentID IN ({mergedAssessments})");
		Count("Appeals", $"SELECT COUNT(*) FROM Appeal WHERE AssessmentID IN ({mergedAssessments})");
		Count("Imported inspection links", $"SELECT COUNT(*) FROM ExternalLink WHERE AssessmentID IN ({mergedAssessments})");
		Count("Documents", "SELECT COUNT(*) FROM Document WHERE PropertyID = @mergeId");
		Count("Compliance cases", "SELECT COUNT(*) FROM ComplianceCase WHERE PropertyID = @mergeId");
		Count("Improvement projects", "SELECT COUNT(*) FROM ImprovementProject WHERE PropertyID = @mergeId");
		Count("Substantial improvement determinations", "SELECT COUNT(*) FROM SIDetermination WHERE PropertyID = @mergeId");
		Count("Links to records in other systems", "SELECT COUNT(*) FROM StructureLink WHERE PropertyID = @mergeId");
		Count("Field team assignments", "SELECT COUNT(*) FROM Assignment WHERE PropertyID = @mergeId");

		// Tags: one row per structure and tag, so a tag both have is kept once.
		HashSet<long> keptTags = c.Query<long>("SELECT TagID FROM PropertyTag WHERE PropertyID = @keepId", args, tx).ToHashSet();
		foreach (long tagId in c.Query<long>("SELECT TagID FROM PropertyTag WHERE PropertyID = @mergeId ORDER BY TagID", args, tx))
		{
			(keptTags.Contains(tagId) ? plan.TagsBothHave : plan.TagsToMove).Add(tagId);
		}
		if (plan.TagsToMove.Count > 0)
		{
			pv.Moving.Add(new MergeCount("Tags", plan.TagsToMove.Count));
		}

		// The stored flood lookup: one per structure. The kept structure's own stays; the duplicate's moves only when the
		// kept structure has none. A lookup made somewhere else than the kept structure ends up is stale to the Review
		// checks, as for any structure that moved.
		if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM FloodLookup WHERE PropertyID = @mergeId", args, tx) > 0)
		{
			if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM FloodLookup WHERE PropertyID = @keepId", args, tx) == 0)
			{
				plan.MoveFloodLookup = true;
				pv.Moving.Add(new MergeCount("Flood map lookup", 1));
			}
			else
			{
				pv.StayingBehind.Add($"Its stored flood map lookup, because {pv.KeepLabel} has its own.");
			}
		}

		// A structure has one field assignment per event (AssignToTeam): two can't be put together.
		foreach (string eventName in c.Query<string>("""
			SELECT COALESCE(e.Name, 'work with no event') FROM Assignment k
			JOIN Assignment m ON m.PropertyID = @mergeId AND m.EventID IS k.EventID
			LEFT JOIN Event e ON e.EventID = k.EventID
			WHERE k.PropertyID = @keepId
			""", args, tx).Distinct())
		{
			pv.Conflicts.Add($"Both structures are assigned to a field team for {eventName}. Take one of them off its team first.");
		}
		// Two open compliance cases for one building: which one tracks it is for a person to decide.
		if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM ComplianceCase WHERE PropertyID = @keepId AND Status <> 'Closed'", args, tx) > 0
			&& c.ExecuteScalar<int>("SELECT COUNT(*) FROM ComplianceCase WHERE PropertyID = @mergeId AND Status <> 'Closed'", args, tx) > 0)
		{
			pv.Conflicts.Add("Both structures have an open compliance case. Close the one that isn't needed first, so the building is tracked once.");
		}
		// The same permit on both would be counted twice in the substantial improvement total.
		foreach (string permit in c.Query<string>("""
			SELECT DISTINCT TRIM(k.PermitNumber) FROM ImprovementProject k
			JOIN ImprovementProject m ON m.PropertyID = @mergeId AND UPPER(TRIM(m.PermitNumber)) = UPPER(TRIM(k.PermitNumber))
			WHERE k.PropertyID = @keepId AND TRIM(COALESCE(k.PermitNumber, '')) <> ''
			""", args, tx))
		{
			pv.Conflicts.Add($"Both structures have an improvement project for permit {permit}, which would be counted twice in the substantial improvement total. Delete one of them first.");
		}

		foreach (string eventName in c.Query<string>($"""
			SELECT DISTINCT e.Name FROM EventAssessment k
			JOIN Assessment ka ON ka.AssessmentID = k.AssessmentID AND ka.PropertyID = @keepId AND ka.Active = 1
			JOIN EventAssessment m ON m.EventID = k.EventID
			JOIN Assessment ma ON ma.AssessmentID = m.AssessmentID AND ma.PropertyID = @mergeId AND ma.Active = 1
			JOIN Event e ON e.EventID = k.EventID
			""", args, tx))
		{
			pv.Warnings.Add($"Both structures have an assessment for {eventName}. After the merge the kept structure has both; if they are the same visit, delete one.");
		}
		if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM SIDetermination WHERE PropertyID = @keepId", args, tx) > 0
			&& c.ExecuteScalar<int>("SELECT COUNT(*) FROM SIDetermination WHERE PropertyID = @mergeId", args, tx) > 0)
		{
			pv.Warnings.Add("Both structures have substantial improvement determinations. Check the cumulative total after the merge.");
		}
		if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM EditHistory WHERE PropertyID = @mergeId AND AssessmentID IS NULL", args, tx) > 0)
		{
			pv.StayingBehind.Add("The history of its own fields (address, owner and so on). Its assessments' history moves with them.");
		}
	}

	private static bool IsBlankField(PropertyRecord p, string column)
	{
		object v = FieldValue(p, column);
		if (v == null || (v is string s && string.IsNullOrWhiteSpace(s)))
		{
			return true;
		}
		return ZeroIsBlank(column) && Convert.ToDecimal(v, CultureInfo.InvariantCulture) == 0m;
	}

	/// <summary>FEMA stored 0 for "none chosen" in its lookup columns; a square footage of 0 isn't a building's size either.</summary>
	private static bool ZeroIsBlank(string column)
	{
		FieldInfo f = FieldCatalog.Find(FieldModel.Property, column);
		if (f != null)
		{
			return f.Kind is FieldKind.Lookup or FieldKind.State or FieldKind.County or FieldKind.StructureType;
		}
		return column == nameof(PropertyRecord.SquareFootage) || column.EndsWith("ID", StringComparison.Ordinal);
	}

	private static object FieldValue(PropertyRecord p, string column) => typeof(PropertyRecord).GetProperty(column)!.GetValue(p);

	private static string FieldLabel(string column)
	{
		FieldInfo f = FieldCatalog.Find(FieldModel.Property, column);
		if (f != null)
		{
			return f.Label;
		}
		if (column == nameof(PropertyRecord.SquareFootage))
		{
			return "Square footage";
		}
		// "TeamName" → "Team name".
		System.Text.StringBuilder b = new System.Text.StringBuilder();
		for (int i = 0; i < column.Length; i++)
		{
			char ch = column[i];
			if (i > 0 && char.IsUpper(ch) && !char.IsUpper(column[i - 1]))
			{
				b.Append(' ').Append(char.ToLowerInvariant(ch));
			}
			else
			{
				b.Append(ch);
			}
		}
		return b.ToString();
	}

	private string ShowField(PropertyRecord p, string column)
	{
		object v = FieldValue(p, column);
		FieldInfo f = FieldCatalog.Find(FieldModel.Property, column);
		return f != null ? FieldCatalog.Display(f, v, Reference) : FieldCatalog.Canonical(v) ?? "";
	}

	// ───────────── Recording each change, and undoing it ─────────────

	/// <summary>
	/// One row the merge changed, with values exactly as SQLite stored them (see <see cref="EncodeValue"/>), so undoing it
	/// writes back the same bytes.
	/// </summary>
	internal sealed class MergeChange
	{
		public const string Update = "Update";
		public const string Delete = "Delete";
		public const string Insert = "Insert";

		public string Table { get; set; }

		/// <summary>Update, Delete or Insert.</summary>
		public string Action { get; set; }

		/// <summary>The row's key as it is after the merge.</summary>
		public Dictionary<string, string> Key { get; set; }

		/// <summary>For an update: the column, and its value before and after.</summary>
		public string Column { get; set; }

		public string Old { get; set; }

		public string New { get; set; }

		/// <summary>For a delete: the whole row.</summary>
		public Dictionary<string, string> Row { get; set; }
	}

	/// <summary>Everything needed to undo one merge (stored as JSON in MacSetting "Merge.&lt;batch&gt;").</summary>
	internal sealed class MergeUndoRecord
	{
		public const int CurrentFormat = 1;

		public int Format { get; set; } = CurrentFormat;

		public string BatchId { get; set; }

		public Guid KeepPropertyID { get; set; }

		public Guid MergePropertyID { get; set; }

		public string KeepLabel { get; set; }

		public string MergeLabel { get; set; }

		public DateTime MergedOn { get; set; }

		public string MergedBy { get; set; }

		public List<Guid> MovedAssessments { get; set; } = new();

		/// <summary>The versions of both structures and the moved assessments right after the merge.</summary>
		public Dictionary<Guid, long> Versions { get; set; } = new();

		public List<MergeChange> Changes { get; set; } = new();
	}

	/// <summary>Points every matching row's <paramref name="column"/> from <paramref name="from"/> to <paramref name="to"/>, recording each.</summary>
	private static void RepointRows(IDbConnection c, IDbTransaction tx, List<MergeChange> log, string table, string[] key, string column, string where, object from, object to)
	{
		string columns = string.Join(", ", key.Select(TableMap<MacSettingRecord>.Q));
		List<IDictionary<string, object>> rows = c.Query($"SELECT {columns} FROM {TableMap<MacSettingRecord>.Q(table)} WHERE {where} ORDER BY {columns}", new { from }, tx)
			.Cast<IDictionary<string, object>>().ToList();
		foreach (IDictionary<string, object> row in rows)
		{
			LoggedUpdate(c, tx, log, table, new Dictionary<string, object>(row), column, to);
		}
	}

	/// <summary>Sets one column of one row and records its value before and after.</summary>
	private static void LoggedUpdate(IDbConnection c, IDbTransaction tx, List<MergeChange> log, string table, Dictionary<string, object> key, string column, object value)
	{
		object old = RawValue(c, tx, table, key, column);
		(string where, DynamicParameters args) = KeyFilter(key);
		args.Add("value", value);
		if (c.Execute($"UPDATE {TableMap<MacSettingRecord>.Q(table)} SET {TableMap<MacSettingRecord>.Q(column)} = @value WHERE {where}", args, tx) != 1)
		{
			throw new InvalidOperationException($"A {table} row changed while the structures were being merged. Nothing was changed.");
		}
		Dictionary<string, object> after = new Dictionary<string, object>(key);
		if (after.ContainsKey(column))
		{
			after[column] = value;
		}
		log.Add(new MergeChange { Table = table, Action = MergeChange.Update, Key = Encode(after), Column = column, Old = EncodeValue(old), New = EncodeValue(RawValue(c, tx, table, after, column)) });
	}

	/// <summary>Removes one row and records all of it.</summary>
	private static void LoggedDelete(IDbConnection c, IDbTransaction tx, List<MergeChange> log, string table, Dictionary<string, object> key)
	{
		IDictionary<string, object> row = RawRow(c, tx, table, key);
		(string where, DynamicParameters args) = KeyFilter(key);
		c.Execute($"DELETE FROM {TableMap<MacSettingRecord>.Q(table)} WHERE {where}", args, tx);
		log.Add(new MergeChange { Table = table, Action = MergeChange.Delete, Key = Encode(key), Row = Encode(row) });
	}

	/// <summary>Puts back one recorded change, refusing when the row isn't as the merge left it.</summary>
	private static void UndoChange(IDbConnection c, IDbTransaction tx, MergeChange change)
	{
		string table = TableMap<MacSettingRecord>.Q(change.Table);
		Dictionary<string, object> key = Decode(change.Key);
		(string where, DynamicParameters args) = KeyFilter(key);
		string changedSince = $"The merge can't be undone because something it changed ({TableNames.GetValueOrDefault(change.Table, change.Table)}) has been changed again since. Nothing was changed.";
		switch (change.Action)
		{
			case MergeChange.Update:
				args.Add("old", DecodeValue(change.Old));
				args.Add("new", DecodeValue(change.New));
				string col = TableMap<MacSettingRecord>.Q(change.Column);
				if (c.Execute($"UPDATE {table} SET {col} = @old WHERE {where} AND {col} IS @new", args, tx) != 1)
				{
					throw new MergeRefusedException(changedSince);
				}
				break;
			case MergeChange.Insert:
				if (c.Execute($"DELETE FROM {table} WHERE {where}", args, tx) != 1)
				{
					throw new MergeRefusedException(changedSince);
				}
				break;
			case MergeChange.Delete:
				Dictionary<string, object> row = Decode(change.Row);
				DynamicParameters values = new DynamicParameters();
				List<string> names = row.Keys.ToList();
				for (int i = 0; i < names.Count; i++)
				{
					values.Add("v" + i, row[names[i]]);
				}
				try
				{
					c.Execute($"INSERT INTO {table} ({string.Join(", ", names.Select(TableMap<MacSettingRecord>.Q))}) VALUES ({string.Join(", ", names.Select((_, i) => "@v" + i))})", values, tx);
				}
				catch (SqliteException)
				{
					throw new MergeRefusedException(changedSince);
				}
				break;
			default:
				throw new MergeRefusedException("This merge was recorded by a newer version of SDE. Undo it there.");
		}
	}

	/// <summary>Plain names for the tables a merge changes, for messages.</summary>
	private static readonly Dictionary<string, string> TableNames = new()
	{
		["Property"] = "a structure", ["Assessment"] = "an assessment", ["EditHistory"] = "edit history", ["Document"] = "a document",
		["ComplianceCase"] = "a compliance case", ["ImprovementProject"] = "an improvement project", ["SIDetermination"] = "an SI determination",
		["StructureLink"] = "a link to another system", ["Assignment"] = "a field assignment", ["PropertyTag"] = "a tag", ["FloodLookup"] = "a flood map lookup",
		["CustomField_Results"] = "a custom field", ["Shapes"] = "the square-footage shapes", ["DeletedItem"] = "Recently deleted",
		["LastFiveAssesments"] = "the recent assessments list",
	};

	private static (string Where, DynamicParameters Args) KeyFilter(IReadOnlyDictionary<string, object> key)
	{
		DynamicParameters args = new DynamicParameters();
		List<string> parts = new List<string>();
		int i = 0;
		foreach (KeyValuePair<string, object> kv in key)
		{
			parts.Add($"{TableMap<MacSettingRecord>.Q(kv.Key)} IS @k{i}");
			args.Add("k" + i, kv.Value);
			i++;
		}
		return (string.Join(" AND ", parts), args);
	}

	/// <summary>A row exactly as stored: long, double, string, byte[] or null for each column.</summary>
	private static IDictionary<string, object> RawRow(IDbConnection c, IDbTransaction tx, string table, IReadOnlyDictionary<string, object> key)
	{
		(string where, DynamicParameters args) = KeyFilter(key);
		IDictionary<string, object> row = c.Query($"SELECT * FROM {TableMap<MacSettingRecord>.Q(table)} WHERE {where}", args, tx).Cast<IDictionary<string, object>>().SingleOrDefault();
		return row == null ? null : new Dictionary<string, object>(row);
	}

	private static object RawValue(IDbConnection c, IDbTransaction tx, string table, IReadOnlyDictionary<string, object> key, string column)
	{
		(string where, DynamicParameters args) = KeyFilter(key);
		object v = c.ExecuteScalar<object>($"SELECT {TableMap<MacSettingRecord>.Q(column)} FROM {TableMap<MacSettingRecord>.Q(table)} WHERE {where}", args, tx);
		return v is DBNull ? null : v;
	}

	/// <summary>A structure's PropertyID as it is stored (the text SQLite holds).</summary>
	private static object Raw(IDbConnection c, IDbTransaction tx, Guid propertyId) =>
		c.ExecuteScalar<object>("SELECT PropertyID FROM Property WHERE PropertyID = @propertyId", new { propertyId }, tx);

	/// <summary>
	/// A stored value as text that keeps its SQLite storage class: "i:" integer, "r:" real (round-trip), "t:" text,
	/// "b:" blob (base 64), null for NULL.
	/// </summary>
	private static string EncodeValue(object v) => v switch
	{
		null or DBNull => null,
		long l => "i:" + l.ToString(CultureInfo.InvariantCulture),
		int n => "i:" + n.ToString(CultureInfo.InvariantCulture),
		double d => "r:" + d.ToString("R", CultureInfo.InvariantCulture),
		string s => "t:" + s,
		byte[] b => "b:" + Convert.ToBase64String(b),
		DateTime d => "t:" + d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
		_ => throw new InvalidOperationException("A merge can't record a value of type " + v.GetType().Name + "."),
	};

	private static object DecodeValue(string s) => s == null ? null : s[0] switch
	{
		'i' => long.Parse(s[2..], CultureInfo.InvariantCulture),
		'r' => double.Parse(s[2..], CultureInfo.InvariantCulture),
		'b' => Convert.FromBase64String(s[2..]),
		_ => s[2..],
	};

	private static Dictionary<string, string> Encode(IDictionary<string, object> row) =>
		row.ToDictionary(kv => kv.Key, kv => EncodeValue(kv.Value));

	private static Dictionary<string, object> Decode(Dictionary<string, string> row) =>
		row.ToDictionary(kv => kv.Key, kv => DecodeValue(kv.Value));
}
