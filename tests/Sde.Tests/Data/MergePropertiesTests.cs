using Dapper;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using Sde.Core.Assessments;
using Sde.Core.Improvements;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Tests.Data;

/// <summary>Merging a duplicate structure into the one being kept, and undoing it.</summary>
public class MergePropertiesTests
{
	/// <summary>
	/// Two records of one building: <see cref="Merge"/> (the ranch-flood case, with an assessment and something in every
	/// table that hangs off a structure) and <see cref="Keep"/> (the same building entered again, with a few fields of its
	/// own and a few left blank).
	/// </summary>
	private sealed class Duplicates : IDisposable
	{
		public Duplicates(bool rich = true)
		{
			T = new TempDatabase();
			Visit = SampleCases.RanchFlood(Store);
			Merge = Visit.Property;
			Merge.ParcelNumber = "P-100";
			Merge.LotNumber = "7";
			Merge.Latitude = 39.961234m;
			Merge.Longitude = -82.998765m;
			Merge.MailingStreetNumber = "9";
			Merge.MailingStreetName = "Box Road";
			Store.SaveProperty(Merge);
			Keep = Store.SaveProperty(new PropertyRecord
			{
				OwnerFirstName = "Pat", OwnerLastName = "Sample",
				StreetName = "River", City = "Exampleville", StateID = 36,
				NFIPCID = "399990", ParcelNumber = "P-100",
				StructureTypeID = 1, ResidenceTypeID = 92, FoundationTypeID = 45, SuperStructureTypeID = 130,
				RoofCoveringTypeID = 95, ExteriorFinishTypeID = 25, HVACTypeID = 49, StoryTypeID = 104,
				YearOfConstruction = 1962, SquareFootage = 1450m,
			});
			if (!rich)
			{
				return;
			}

			Guid aid = Visit.Assessment.AssessmentID;
			Directory.CreateDirectory(Store.AttachmentsFolder);
			File.WriteAllText(Path.Combine(Store.AttachmentsFolder, "front.jpg"), "photo");
			Visit.Attachments.Add(new AttachmentRecord { FileName = "front.jpg", Active = true, AttachmentType = "Photo", DefaultImage = true });
			Store.SaveAssessment(Visit);
			Store.SetStatus(aid, AssessmentStatus.Reviewed);
			Store.AddReviewComment(aid, "Check the basement");
			EventRecord flood = Store.SaveEvent(new EventRecord { Name = "September flood" });
			Store.SetEvent(new[] { aid }, flood.EventID);
			Gone = Store.CreateAssessment(Merge.PropertyID, applyDefaults: false).Assessment.AssessmentID;
			Store.TrashAssessment(Gone);
			Shared = Store.CreateTag("Revisit");
			Unique = Store.CreateTag("Owner called");
			KeptOnly = Store.CreateTag("Assessor record");
			Store.SetTags(Merge.PropertyID, new[] { Shared.TagID, Unique.TagID });
			Store.SetTags(Keep.PropertyID, new[] { Shared.TagID, KeptOnly.TagID });
			Store.SaveImprovementProject(new ImprovementProjectRecord { PropertyID = Merge.PropertyID, PermitNumber = "B-1", Cost = 5000m, Included = true });
			Store.SaveImprovementProject(new ImprovementProjectRecord { PropertyID = Keep.PropertyID, PermitNumber = "B-2", Cost = 7000m, Included = true });
			Store.SaveShapes(Merge.PropertyID, new[] { new ShapeRecord { ShapeType = "c", TotalSqFt = 1450m } });
			CustomFieldRecord field = Store.Reference.CustomFields[0];
			Store.SaveCustomFieldResults(Merge.PropertyID, "property", new[] { new CustomFieldResultRecord { CustomFieldID = field.CustomFieldID, Result = "Flood vent installed", zOrder = 1 } });
			using SqliteConnection c = T.Database.Connect();
			c.Execute("""
				INSERT INTO LetterLog (LetterID, AssessmentID, Kind) VALUES ('l1', @aid, 'Notice');
				INSERT INTO ContractorBid (BidID, AssessmentID, Amount) VALUES ('b1', @aid, 1000);
				INSERT INTO Appeal (AssessmentID, Outcome) VALUES (@aid, 'Pending');
				INSERT INTO ExternalLink (AssessmentID, Source, RecordId) VALUES (@aid, 'Permits', 'INS-1');
				INSERT INTO Document (DocumentID, PropertyID, AssessmentID, Kind, FileName) VALUES ('dm', @mid, @aid, 'ElevationCertificate', 'ec.pdf');
				INSERT INTO Document (DocumentID, PropertyID, Kind, FileName) VALUES ('dk', @kid, 'Permit', 'permit.pdf');
				INSERT INTO ComplianceCase (CaseID, PropertyID, AssessmentID, Kind) VALUES ('cc1', @mid, @aid, 'SD');
				INSERT INTO ComplianceEntry (EntryID, CaseID, EntryOn, Text) VALUES ('ce1', 'cc1', '2026-10-01', 'Called the owner');
				INSERT INTO SIDetermination (DeterminationID, PropertyID, DeterminedOn, ThresholdPercent, CumulativeCost) VALUES ('si1', @mid, '2026-10-01', 50, 5000);
				INSERT INTO StructureLink (Source, RecordId, PropertyID) VALUES ('Assessor', 'R-1', @mid);
				INSERT INTO StructureLink (Source, RecordId, PropertyID) VALUES ('Assessor', 'R-2', @kid);
				INSERT INTO Team (TeamID, Name) VALUES ('t1', 'Team 1');
				INSERT INTO Assignment (AssignmentID, PropertyID, TeamID, ShortCode) VALUES ('as1', @mid, 't1', 'ABC123');
				INSERT INTO PhotoInfo (FileName, AssessmentID, TakenOn) VALUES ('front.jpg', @aid, '2026-09-20');
				INSERT INTO PhotoMarkup (FileName, Markup) VALUES ('front.jpg', '[]');
				""", new { aid = aid.ToString("D"), mid = Merge.PropertyID.ToString("D"), kid = Keep.PropertyID.ToString("D") });
		}

		public TempDatabase T { get; }

		public SdeStore Store => T.Store;

		public AssessmentFile Visit { get; }

		public PropertyRecord Merge { get; }

		public PropertyRecord Keep { get; }

		/// <summary>An assessment of the duplicate that is in Recently deleted.</summary>
		public Guid Gone { get; }

		public TagRecord Shared { get; }

		public TagRecord Unique { get; }

		public TagRecord KeptOnly { get; }

		public MergePropertiesResult Run() => Store.MergeProperties(Keep.PropertyID, Merge.PropertyID);

		public void Dispose() => T.Dispose();
	}

	/// <summary>Every row of every table, as SQLite stores it (quote() keeps the storage class), sorted.</summary>
	private static string Dump(TempDatabase t, bool everything = false)
	{
		using SqliteConnection c = t.Database.Connect();
		long history = everything ? long.MaxValue : c.ExecuteScalar<long?>("SELECT MAX(HistoryID) FROM EditHistory") ?? 0;
		return Dump(c, everything ? long.MaxValue : history, everything);
	}

	private static string Dump(SqliteConnection c, long historyUpTo, bool everything)
	{
		List<string> lines = new List<string>();
		foreach (string table in c.Query<string>("SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name"))
		{
			if (!everything && table is "sqlite_sequence" or "RowVersion")
			{
				continue;
			}
			string row = string.Join(" || '|' || ", c.Query<string>("SELECT name FROM pragma_table_info(@table)", new { table }).Select(n => $"quote(\"{n}\")"));
			string where = table == "EditHistory" && historyUpTo != long.MaxValue ? " WHERE HistoryID <= " + historyUpTo : "";
			lines.AddRange(c.Query<string>($"SELECT '{table}: ' || {row} FROM \"{table}\"{where}").OrderBy(s => s, StringComparer.Ordinal));
		}
		return string.Join("\n", lines);
	}

	/// <summary>Every table and column that still holds the merged structure's ID, with how many rows.</summary>
	private static Dictionary<string, int> References(TempDatabase t, Guid id)
	{
		using SqliteConnection c = t.Database.Connect();
		Dictionary<string, int> found = new Dictionary<string, int>();
		foreach (string table in c.Query<string>("SELECT name FROM sqlite_master WHERE type = 'table' AND name <> 'sqlite_sequence'"))
		{
			foreach (string column in c.Query<string>("SELECT name FROM pragma_table_info(@table)", new { table }))
			{
				int n = c.ExecuteScalar<int>($"SELECT COUNT(*) FROM \"{table}\" WHERE \"{column}\" = @id", new { id = id.ToString("D") });
				if (n > 0)
				{
					found[table + "." + column] = n;
				}
			}
		}
		return found;
	}

	[Fact]
	public void Preview_SaysWhatWouldMove_WhatIsFilledIn_AndChangesNothing()
	{
		using Duplicates d = new Duplicates();
		string before = Dump(d.T, everything: true);
		MergePreview p = d.Store.PreviewMerge(d.Keep.PropertyID, d.Merge.PropertyID);
		Assert.Equal(before, Dump(d.T, everything: true));

		Assert.True(p.CanMerge, string.Join(" ", p.Conflicts));
		Dictionary<string, int> moving = p.Moving.ToDictionary(m => m.What, m => m.Count);
		Assert.Equal(1, moving["Assessments"]);
		Assert.Equal(1, moving["Assessments in Recently deleted"]);
		Assert.Equal(1, moving["Photos"]);
		Assert.Equal(1, moving["Review comments"]);
		Assert.Equal(1, moving["Letters"]);
		Assert.Equal(1, moving["Contractor bids"]);
		Assert.Equal(1, moving["Appeals"]);
		Assert.Equal(1, moving["Imported inspection links"]);
		Assert.Equal(1, moving["Documents"]);
		Assert.Equal(1, moving["Compliance cases"]);
		Assert.Equal(1, moving["Improvement projects"]);
		Assert.Equal(1, moving["Substantial improvement determinations"]);
		Assert.Equal(1, moving["Links to records in other systems"]);
		Assert.Equal(1, moving["Field team assignments"]);
		Assert.Equal(1, moving["Tags"]);
		Assert.Equal(1, moving["Square-footage shapes"]);
		Assert.Equal(1, moving["Custom field values"]);

		Assert.Contains(p.Filled, f => f.Key == "Property.ZipCode" && f.OtherValue == "43702");
		Assert.Contains(p.Filled, f => f.Key == "Property.Latitude");
		Assert.Contains(p.Filled, f => f.Key == "Property.LotNumber" && f.OtherValue == "7");
		Assert.Contains(p.Filled, f => f.Key == "Property.MailingStreetName" && f.OtherValue == "Box Road");
		Assert.Contains(p.Filled, f => f.Label == "Custom Field 1" && f.OtherValue == "Flood vent installed");
		Assert.DoesNotContain(p.Filled, f => f.Key == "Property.OwnerLastName");
		Assert.Contains(p.Differences, f => f.Key == "Property.OwnerLastName" && f.KeptValue == "Sample" && f.OtherValue == "Example");
		// The street address is partly filled in on the kept structure, so its number isn't taken from the other one.
		Assert.DoesNotContain(p.Filled, f => f.Key == "Property.StreetNumber");
		Assert.Contains(p.Differences, f => f.Key == "Property.StreetNumber" && f.KeptValue == "" && f.OtherValue == "123");
		Assert.Equal("River", p.KeepLabel);
		Assert.StartsWith("123 River", p.MergeLabel);
		Assert.Contains(p.StayingBehind, s => s.StartsWith("The history of its own fields"));
	}

	[Fact]
	public void Merge_MovesEveryRelatedRow_ToTheKeptStructure()
	{
		using Duplicates d = new Duplicates();
		Guid keep = d.Keep.PropertyID;
		Guid merge = d.Merge.PropertyID;
		Guid aid = d.Visit.Assessment.AssessmentID;
		MergePropertiesResult r = d.Run();

		Assert.Equal("S:" + merge.ToString("D"), r.DeletedItemKey);
		using SqliteConnection c = d.T.Database.Connect();
		object k = new { keep = keep.ToString("D") };
		foreach ((string table, int count) in new[]
		{
			("Assessment", 2), ("Document", 2), ("ComplianceCase", 1), ("ImprovementProject", 2), ("SIDetermination", 1),
			("StructureLink", 2), ("Assignment", 1), ("PropertyTag", 3), ("Shapes", 1),
		})
		{
			Assert.Equal(count, c.ExecuteScalar<int>($"SELECT COUNT(*) FROM {table} WHERE PropertyID = @keep", k));
		}
		Assert.Equal(new[] { "Assessor record", "Owner called", "Revisit" }, d.Store.GetTagsFor(keep).Select(x => x.Name).OrderBy(n => n));
		Assert.Equal("Flood vent installed", d.Store.GetCustomFieldResults(keep).Single().Result);
		// The assessment's own rows follow it, because they are keyed by the assessment, which moved unchanged.
		AssessmentFile moved = d.Store.LoadAssessment(aid);
		Assert.Equal(keep, moved.Property.PropertyID);
		Assert.Equal(AssessmentStatus.Reviewed, d.Store.GetStatus(aid).Value);
		Assert.Single(d.Store.GetReviewComments(aid));
		Assert.Single(moved.Attachments);
		Assert.NotNull(d.Store.GetEventOf(aid));

		// Nothing else points at the duplicate: only its own row, its own history and its place in Recently deleted.
		Dictionary<string, int> left = References(d.T, merge);
		Assert.Equal(new[] { "DeletedItem.PropertyID", "EditHistory.PropertyID", "Property.PropertyID", "RowVersion.ObjectID" }, left.Keys.OrderBy(x => x, StringComparer.Ordinal));
		Assert.Equal(1, left["DeletedItem.PropertyID"]);
		Assert.Equal(0, c.ExecuteScalar<int>("SELECT COUNT(*) FROM EditHistory WHERE PropertyID = @merge AND AssessmentID IS NOT NULL", new { merge = merge.ToString("D") }));
		Assert.Equal("Structure", c.ExecuteScalar<string>("SELECT Kind FROM DeletedItem WHERE PropertyID = @merge", new { merge = merge.ToString("D") }));
		Assert.False(d.Store.GetProperty(merge).Active);
		Assert.Contains(d.Store.GetDeletedItems(), i => i.ItemKey == r.DeletedItemKey && i.Label.Contains("(merged into"));
		Assert.Single(d.Store.ListStructures().Where(s => s.PropertyID == keep || s.PropertyID == merge).Select(s => s.PropertyID).Distinct());

		// The duplicate's assessment that was in Recently deleted comes back to the kept structure.
		d.Store.Restore("A:" + d.Gone.ToString("D"));
		Assert.Equal(keep, d.Store.LoadAssessment(d.Gone).Property.PropertyID);
		Assert.False(d.Store.GetProperty(merge).Active);
	}

	[Fact]
	public void Merge_MovesAssessmentsUnchanged()
	{
		using Duplicates d = new Duplicates();
		Guid aid = d.Visit.Assessment.AssessmentID;
		string Rows(string sql) => string.Join("\n", d.T.Database.Read(c => c.Query<string>(sql, new { aid = aid.ToString("D") })));
		const string assessment = "SELECT quote(AssessmentID) || quote(DateofInspection) || quote(PercentDamage) || quote(DamageMessage) || quote(TotalEstimatedDamages) || quote(ComputedActualCashValue) || quote(LastUpdate) || quote(Active) FROM Assessment WHERE AssessmentID = @aid";
		const string children = "SELECT ElementName || quote(UserDamage) || quote(ItemCost) || quote(DamageValues) FROM ElementPercentage WHERE AssessmentID = @aid ORDER BY ElementName";
		string a = Rows(assessment);
		string e = Rows(children);
		d.Run();
		Assert.Equal(a, Rows(assessment));
		Assert.Equal(e, Rows(children));
		Assert.Equal(75.0m, d.Store.LoadAssessment(aid).Assessment.PercentDamage);
	}

	[Fact]
	public void Merge_FillsOnlyBlankFields_AndNeverOverwritesAValue()
	{
		using Duplicates d = new Duplicates();
		MergePropertiesResult r = d.Run();
		PropertyRecord kept = d.Store.GetProperty(d.Keep.PropertyID);
		Assert.Equal("43702", kept.ZipCode);
		Assert.Equal(39.961234m, kept.Latitude);
		Assert.Equal(-82.998765m, kept.Longitude);
		Assert.Equal("7", kept.LotNumber);
		Assert.Equal("Box Road", kept.MailingStreetName);
		Assert.Equal("9", kept.MailingStreetNumber);
		Assert.Equal("Sample", kept.OwnerLastName);
		Assert.Equal("Pat", kept.OwnerFirstName);
		Assert.Null(kept.StreetNumber);
		Assert.Equal("River", kept.StreetName);
		Assert.Equal(1450m, kept.SquareFootage);
		Assert.Contains(r.Filled, f => f.Key == "Property.ZipCode");
		Assert.DoesNotContain(r.Filled, f => f.Key == "Property.OwnerLastName" || f.Key == "Property.StreetNumber");
		// What was filled is in the kept structure's history, in the merge's batch.
		List<EditHistoryRecord> batch = d.Store.GetBatch(r.BatchId);
		Assert.Contains(batch, h => h.Field == "Property.ZipCode" && h.OldValue == null && h.NewValue == "43702" && h.PropertyID == d.Keep.PropertyID);
		Assert.DoesNotContain(batch, h => h.Field == "Property.OwnerLastName");
	}

	[Fact]
	public void ABlankCalculationField_IsFilledIn_WhenTheKeptStructureHasNoAssessments()
	{
		using Duplicates d = new Duplicates(rich: false);
		d.Keep.FoundationTypeID = null;
		d.Keep.SquareFootage = null;
		d.Store.SaveProperty(d.Keep);
		MergePreview p = d.Store.PreviewMerge(d.Keep.PropertyID, d.Merge.PropertyID);
		Assert.True(p.CanMerge, string.Join(" ", p.Conflicts));
		d.Run();
		PropertyRecord kept = d.Store.GetProperty(d.Keep.PropertyID);
		Assert.Equal(45, kept.FoundationTypeID);
		Assert.Equal(1450m, kept.SquareFootage);
		// The moved assessment is calculated from the same structure fields as before, so recalculating changes nothing.
		AssessmentFile moved = d.Store.LoadAssessment(d.Visit.Assessment.AssessmentID);
		decimal? percent = moved.Assessment.PercentDamage;
		d.Store.Calculator.Recalculate(moved);
		Assert.Equal(percent, moved.Assessment.PercentDamage);
	}

	[Fact]
	public void Merge_ShowsInEditHistory()
	{
		using Duplicates d = new Duplicates();
		Guid aid = d.Visit.Assessment.AssessmentID;
		int earlier = d.Store.GetHistory(aid).Count;
		d.Store.UserName = "Reviewer";
		MergePropertiesResult r = d.Run();

		List<EditHistoryRecord> kept = d.Store.GetPropertyHistory(d.Keep.PropertyID);
		Assert.Contains(kept, h => h.Label.StartsWith("Merged with the duplicate structure at") && h.Via == SaveVia.Merge && h.ChangedBy == "Reviewer" && h.BatchId == r.BatchId);
		// The moved assessment's earlier history comes with it (it would otherwise go when the duplicate is removed for good).
		Assert.Contains(kept, h => h.AssessmentID == aid && h.Label == SdeStore.CreatedEvent);
		Assert.Contains(d.Store.GetHistory(aid), h => h.Label.StartsWith("Moved here from the duplicate structure at"));
		Assert.True(d.Store.GetHistory(aid).Count > earlier);
		List<EditHistoryRecord> merged = d.Store.GetPropertyHistory(d.Merge.PropertyID);
		Assert.Contains(merged, h => h.Label.StartsWith("Merged into") && h.BatchId == r.BatchId);
		Assert.Contains(merged, h => h.Label == "Moved to Recently deleted" && h.BatchId == r.BatchId);
		Assert.True(d.Store.IsMergeBatch(r.BatchId));
	}

	[Fact]
	public void Undo_PutsBothStructuresAndEveryRowBackExactly()
	{
		using Duplicates d = new Duplicates();
		string before = Dump(d.T);
		long lastHistory = d.T.Database.Read(c => c.ExecuteScalar<long>("SELECT MAX(HistoryID) FROM EditHistory"));
		MergePropertiesResult r = d.Run();
		Assert.NotEqual(before, Dump(d.T));

		MergeUndoResult u = d.Store.UndoMerge(r.BatchId);
		Assert.Equal(2, u.AssessmentsMovedBack);
		using (SqliteConnection c = d.T.Database.Connect())
		{
			Assert.Equal(before, Dump(c, lastHistory, everything: false));
		}
		Assert.True(d.Store.GetProperty(d.Merge.PropertyID).Active);
		Assert.Equal(d.Merge.PropertyID, d.Store.LoadAssessment(d.Visit.Assessment.AssessmentID).Property.PropertyID);
		Assert.Equal(new[] { "Owner called", "Revisit" }, d.Store.GetTagsFor(d.Merge.PropertyID).Select(x => x.Name).OrderBy(n => n));
		Assert.Contains(d.Store.GetPropertyHistory(d.Merge.PropertyID), h => h.Label.StartsWith("Merge undone") && h.BatchId == u.BatchId);
		Assert.Contains(d.Store.GetPropertyHistory(d.Keep.PropertyID), h => h.Label.StartsWith("Merge undone") && h.BatchId == u.BatchId);
		// The merge's notes on the assessment went back with it.
		Assert.All(d.Store.GetBatch(r.BatchId).Where(h => h.AssessmentID != null), h => Assert.Equal(d.Merge.PropertyID, h.PropertyID));
		Assert.False(d.Store.IsMergeBatch(r.BatchId));
		Assert.Throws<ArgumentException>(() => d.Store.UndoMerge(r.BatchId));
		// And the two can be merged again.
		Assert.True(d.Store.PreviewMerge(d.Keep.PropertyID, d.Merge.PropertyID).CanMerge);
	}

	[Fact]
	public void RevertingTheMergesBatch_UndoesTheWholeMerge()
	{
		using Duplicates d = new Duplicates();
		string before = Dump(d.T);
		long lastHistory = d.T.Database.Read(c => c.ExecuteScalar<long>("SELECT MAX(HistoryID) FROM EditHistory"));
		MergePropertiesResult r = d.Run();
		BulkResult reverted = d.Store.RevertBatch(r.BatchId);
		Assert.NotNull(reverted.BatchId);
		using SqliteConnection c = d.T.Database.Connect();
		Assert.Equal(before, Dump(c, lastHistory, everything: false));
	}

	[Fact]
	public void Undo_IsRefused_WhenSomethingTheMergeTouchedChangedSince()
	{
		using Duplicates d = new Duplicates();
		MergePropertiesResult r = d.Run();
		d.Store.Apply(d.Visit.Assessment.AssessmentID, f => f.Assessment.InspectorName = "Changed after the merge");
		string before = Dump(d.T, everything: true);
		MergeRefusedException e = Assert.Throws<MergeRefusedException>(() => d.Store.UndoMerge(r.BatchId));
		Assert.Contains("changed since", e.Message);
		Assert.Equal(before, Dump(d.T, everything: true));
		Assert.True(d.Store.IsMergeBatch(r.BatchId));
	}

	[Fact]
	public void Undo_IsRefused_WhenAFilledFieldWasChangedSince_OrTheDuplicateWasRemovedForGood()
	{
		using Duplicates d = new Duplicates();
		MergePropertiesResult r = d.Run();
		using (SqliteConnection c = d.T.Database.Connect())
		{
			// As an older version of SDE would write it: no version change.
			c.Execute("UPDATE Property SET ZipCode = '99999' WHERE PropertyID = @k", new { k = d.Keep.PropertyID.ToString("D") });
		}
		string before = Dump(d.T, everything: true);
		Assert.Throws<MergeRefusedException>(() => d.Store.UndoMerge(r.BatchId));
		Assert.Equal(before, Dump(d.T, everything: true));

		d.Store.Purge(r.DeletedItemKey);
		Assert.Throws<MergeRefusedException>(() => d.Store.UndoMerge(r.BatchId));
		// The moved assessment and its history stay with the kept structure; what was kept for the undo goes on opening.
		Assert.Equal(d.Keep.PropertyID, d.Store.LoadAssessment(d.Visit.Assessment.AssessmentID).Property.PropertyID);
		Assert.Contains(d.Store.GetPropertyHistory(d.Keep.PropertyID), h => h.AssessmentID == d.Visit.Assessment.AssessmentID && h.Label == SdeStore.CreatedEvent);
		d.Store.PurgeExpired();
		Assert.False(d.Store.IsMergeBatch(r.BatchId));
	}

	[Fact]
	public void Refusals_ExplainWhy_AndChangeNothing()
	{
		void Refused(Action<Duplicates> setUp, string expected, Func<Duplicates, (Guid Keep, Guid Merge)> pair = null)
		{
			using Duplicates d = new Duplicates();
			setUp(d);
			(Guid keep, Guid merge) = pair?.Invoke(d) ?? (d.Keep.PropertyID, d.Merge.PropertyID);
			string before = Dump(d.T, everything: true);
			MergePreview p = d.Store.PreviewMerge(keep, merge);
			Assert.False(p.CanMerge);
			Assert.Contains(p.Conflicts, m => m.Contains(expected, StringComparison.Ordinal));
			MergeRefusedException e = Assert.Throws<MergeRefusedException>(() => d.Store.MergeProperties(keep, merge));
			Assert.Contains(expected, e.Message);
			Assert.Equal(before, Dump(d.T, everything: true));
		}

		Refused(_ => { }, "can't be merged with itself", d => (d.Keep.PropertyID, d.Keep.PropertyID));
		Refused(_ => { }, "no longer exists", d => (d.Keep.PropertyID, Guid.NewGuid()));
		Refused(_ => { }, "Choose the two structures", d => (Guid.Empty, d.Merge.PropertyID));
		Refused(d => d.Store.TrashProperty(d.Keep.PropertyID), "is in Recently deleted");
		Refused(d => d.Store.SetStatus(d.Visit.Assessment.AssessmentID, AssessmentStatus.Final), "is Final");
		Refused(d =>
		{
			AssessmentFile k = d.Store.CreateAssessment(d.Keep.PropertyID, applyDefaults: false);
			d.Store.SetStatus(k.Assessment.AssessmentID, AssessmentStatus.Final);
			d.Store.SetStatus(k.Assessment.AssessmentID, AssessmentStatus.LetterSent);
		}, "is Letter sent");
		Refused(d => d.T.Database.InTransaction((c, tx) => c.Execute("INSERT INTO CheckOut (AssessmentID, PackageID, CheckedOutOn, CheckedOutTo) VALUES (@a, @p, '2026-10-01', 'Team 2')",
			new { a = d.Visit.Assessment.AssessmentID.ToString("D"), p = Guid.NewGuid().ToString("D") }, tx)), "checked out to Team 2");
		Refused(d =>
		{
			d.Keep.SquareFootage = 1500m;
			d.Store.SaveProperty(d.Keep);
		}, "Square footage is 1500");
		Refused(d =>
		{
			d.Keep.FoundationTypeID = null;
			d.Store.SaveProperty(d.Keep);
			d.Store.CreateAssessment(d.Keep.PropertyID, applyDefaults: false);
		}, "calculated without it");
		Refused(d => d.T.Database.InTransaction((c, tx) => c.Execute("INSERT INTO Assignment (AssignmentID, PropertyID, TeamID) VALUES ('as2', @k, 't1')",
			new { k = d.Keep.PropertyID.ToString("D") }, tx)), "Both structures are assigned to a field team");
		Refused(d => d.T.Database.InTransaction((c, tx) => c.Execute("INSERT INTO ComplianceCase (CaseID, PropertyID, Kind) VALUES ('cc2', @k, 'SI')",
			new { k = d.Keep.PropertyID.ToString("D") }, tx)), "Both structures have an open compliance case");
		Refused(d => d.Store.SaveImprovementProject(new ImprovementProjectRecord { PropertyID = d.Keep.PropertyID, PermitNumber = " b-1 ", Cost = 5000m, Included = true }),
			"permit b-1");
		Refused(d => d.Store.SetSetting(WorkPackages.ManifestKey, new WorkPackageManifest()), "This is a work package");
	}

	[Fact]
	public void Merge_IsRefused_WhenAStructureChangedAfterThePreview()
	{
		using Duplicates d = new Duplicates();
		MergePreview p = d.Store.PreviewMerge(d.Keep.PropertyID, d.Merge.PropertyID);
		d.Keep.OwnerLastName = "Changed";
		d.Store.SaveProperty(d.Keep);
		string before = Dump(d.T, everything: true);
		Assert.Throws<MergeRefusedException>(() => d.Store.MergeProperties(d.Keep.PropertyID, d.Merge.PropertyID, p));
		Assert.Equal(before, Dump(d.T, everything: true));
		Assert.NotNull(d.Store.MergeProperties(d.Keep.PropertyID, d.Merge.PropertyID, d.Store.PreviewMerge(d.Keep.PropertyID, d.Merge.PropertyID)).BatchId);
	}

	[Fact]
	public void TheirOwnShapesAndValues_StayWithTheDuplicate_WhenTheKeptStructureHasItsOwn()
	{
		using Duplicates d = new Duplicates();
		d.Store.SaveShapes(d.Keep.PropertyID, new[] { new ShapeRecord { ShapeType = "c", TotalSqFt = 1450m } });
		CustomFieldRecord field = d.Store.Reference.CustomFields[0];
		d.Store.SaveCustomFieldResults(d.Keep.PropertyID, "property", new[] { new CustomFieldResultRecord { CustomFieldID = field.CustomFieldID, Result = "Kept value", zOrder = 1 } });
		MergePreview p = d.Store.PreviewMerge(d.Keep.PropertyID, d.Merge.PropertyID);
		Assert.True(p.CanMerge, string.Join(" ", p.Conflicts));
		Assert.Contains(p.StayingBehind, s => s.Contains("square-footage shapes"));
		Assert.Contains(p.StayingBehind, s => s.Contains("Flood vent installed"));
		d.Run();
		Assert.Single(d.Store.GetShapes(d.Keep.PropertyID));
		Assert.Single(d.Store.GetShapes(d.Merge.PropertyID));
		Assert.Equal("Kept value", d.Store.GetCustomFieldResults(d.Keep.PropertyID).Single().Result);
	}

	[Fact]
	public void FemaExport_AfterAMerge_DiffersOnlyByTheRemovedStructure()
	{
		using Duplicates d = new Duplicates(rich: false);
		// The kept record has every field the duplicate has, so nothing is filled in.
		PropertyRecord copy = d.Store.GetProperty(d.Merge.PropertyID);
		copy.PropertyID = d.Keep.PropertyID;
		copy.OwnerLastName = "Sample";
		d.Store.SaveProperty(copy);
		ExchangeService x = new ExchangeService(d.Store);
		(string beforeFolder, _) = x.Export(x.ReadDataset(), Path.Combine(d.T.Folder, "before"));
		MergePropertiesResult r = d.Run();
		Assert.Empty(r.Filled);
		(string afterFolder, _) = x.Export(x.ReadDataset(), Path.Combine(d.T.Folder, "after"));

		JObject before = JObject.Parse(File.ReadAllText(Path.Combine(beforeFolder, "Export.json")));
		JObject after = JObject.Parse(File.ReadAllText(Path.Combine(afterFolder, "Export.json")));
		string merged = d.Merge.PropertyID.ToString();
		string kept = d.Keep.PropertyID.ToString();
		JArray properties = (JArray)before["Properties"];
		JToken removed = properties.Single(p => string.Equals((string)p["PropertyID"], merged, StringComparison.OrdinalIgnoreCase));
		removed.Remove();
		Assert.True(JToken.DeepEquals(properties, after["Properties"]));
		// FEMA's model also writes its record count: one structure fewer.
		before["Count"] = (int)before["Count"] - 1;
		// Its assessment is the same record, now under the kept structure.
		foreach (JToken a in (JArray)before["Assessments"])
		{
			if (string.Equals((string)a["PropertyID"], merged, StringComparison.OrdinalIgnoreCase))
			{
				a["PropertyID"] = after["Assessments"].Single(b => (string)b["AssessmentID"] == (string)a["AssessmentID"])["PropertyID"];
				Assert.Equal(kept, (string)a["PropertyID"], ignoreCase: true);
			}
		}
		foreach (JProperty part in before.Properties())
		{
			Assert.Equal(part.Value.ToString(), after[part.Name].ToString());
		}
		Assert.True(JToken.DeepEquals(before, after));
	}
}
