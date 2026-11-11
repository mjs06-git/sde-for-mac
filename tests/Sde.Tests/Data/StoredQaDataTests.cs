using Dapper;
using Microsoft.Data.Sqlite;
using Sde.Core.Assessments;
using Sde.Core.Depth;
using Sde.Core.Quality;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Tests.Data;

/// <summary>
/// Stored flood lookups and high-water marks (MacSchemaVersion 4): the upgrade, the store, Recently deleted, work
/// packages, the export, and the quality checks that compare with them without going online.
/// </summary>
public class StoredQaDataTests
{
	private const string DropNewTables = "DROP TABLE FloodLookup; DROP TABLE HighWaterMarkRow; DROP INDEX UX_HighWaterMarkSet_Event; DROP TABLE HighWaterMarkSet;";

	private static readonly string[] NewTables = { "FloodLookup", "HighWaterMarkSet", "HighWaterMarkRow" };

	private static int CountTables(SqliteConnection c) =>
		c.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN @NewTables", new { NewTables });

	private static FloodLookupRecord Lookup(Guid propertyId, string zone = "AE", double latitude = 39.7, double longitude = -84.2) => new FloodLookupRecord
	{
		PropertyID = propertyId, Zone = zone, ZoneSubtype = "FLOODWAY", InFloodway = true, Bfe = 742.5m, BfeDatum = "NAVD88",
		FirmPanel = "39113C0157", PanelSuffix = "E", PanelEffectiveDate = new DateTime(2011, 1, 6), CommunityId = "399990",
		CommunityName = "Exampleville, City of", Latitude = latitude, Longitude = longitude, RetrievedOn = new DateTime(2026, 9, 20, 14, 0, 0, DateTimeKind.Utc),
	};

	private static List<HighWaterMark> Marks(decimal elevation = 742.5m) => new()
	{
		new HighWaterMark { Id = "1", Label = "HWM 1", Latitude = 39.7005, Longitude = -84.2, ElevationFt = elevation, DatumName = "NAVD88", Quality = 2, Kind = "Debris" },
		new HighWaterMark { Id = "2", Label = "HWM 2", Latitude = 39.6995, Longitude = -84.2, ElevationFt = elevation, DatumName = "NAVD88", Quality = 2, Kind = "Seed line" },
	};

	/// <summary>The ranch case at 39.7, -84.2 in NAVD88, with a lowest floor of 740 ft and a flood elevation entered.</summary>
	private static AssessmentFile Placed(SdeStore store, decimal floodElevation = 745m)
	{
		AssessmentFile f = SampleCases.RanchFlood(store);
		return store.Apply(f.Assessment.AssessmentID, x =>
		{
			x.Property.Latitude = 39.7m;
			x.Property.Longitude = -84.2m;
			x.Property.Datum = "NAVD88";
			x.Property.FirstFloorElevation = 740m;
			x.Property.FIRMZone = 30;
			x.Assessment.DepthOfFlood = floodElevation;
		});
	}

	// ───────────── The upgrade ─────────────

	[Fact]
	public void OpeningAV3Database_AddsTheNewTables()
	{
		using TempDatabase t = new TempDatabase();
		SampleCases.RanchFlood(t.Store);
		using (SqliteConnection c = t.Database.Connect())
		{
			c.Execute(DropNewTables + """
				DROP INDEX IX_PropertyTag_Tag; DROP TABLE PropertyTag; DROP TABLE Tag;
				UPDATE SchemaInfo SET Value = '3' WHERE Key = 'MacSchemaVersion';
				UPDATE SchemaInfo SET Value = 'v3.0 schema' WHERE Key = 'MacSchemaHash';
				""");
		}
		SqliteConnection.ClearAllPools();
		Assert.True(SdeDatabase.NeedsUpgrade(t.Database.FilePath));
		SdeDatabase reopened = SdeDatabase.Open(t.Database.FilePath);
		using SqliteConnection again = reopened.Connect();
		Assert.Equal(3, CountTables(again));
		Assert.Equal("4", again.ExecuteScalar<string>("SELECT Value FROM SchemaInfo WHERE Key = 'MacSchemaVersion'"));
		Assert.False(SdeDatabase.NeedsUpgrade(t.Database.FilePath));
	}

	/// <summary>A database an earlier 3.5 build stamped 4 before these tables existed: the schema hash differs, so they are added.</summary>
	[Fact]
	public void AV4DatabaseWithoutTheTables_GetsThem_BecauseItsSchemaHashDiffers()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		TagRecord tag = t.Store.CreateTag("Revisit");
		t.Store.SetTags(f.Property.PropertyID, new[] { tag.TagID });
		using (SqliteConnection c = t.Database.Connect())
		{
			c.Execute(DropNewTables + "UPDATE SchemaInfo SET Value = 'an earlier v3.5 schema' WHERE Key = 'MacSchemaHash';");
			Assert.Equal("4", c.ExecuteScalar<string>("SELECT Value FROM SchemaInfo WHERE Key = 'MacSchemaVersion'"));
			Assert.Equal(0, CountTables(c));
		}
		SqliteConnection.ClearAllPools();
		Assert.True(SdeDatabase.NeedsUpgrade(t.Database.FilePath));
		SdeDatabase reopened = SdeDatabase.Open(t.Database.FilePath);
		using SqliteConnection again = reopened.Connect();
		Assert.Equal(3, CountTables(again));
		Assert.Equal(1, again.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'UX_HighWaterMarkSet_Event'"));
		Assert.Equal(SdeDatabase.SchemaHash, again.ExecuteScalar<string>("SELECT Value FROM SchemaInfo WHERE Key = 'MacSchemaHash'"));
		Assert.Equal(1, again.ExecuteScalar<int>("SELECT COUNT(*) FROM PropertyTag"));
		Assert.False(SdeDatabase.NeedsUpgrade(t.Database.FilePath));
		SdeStore store = new SdeStore(reopened);
		Assert.True(store.SaveFloodLookup(Lookup(f.Property.PropertyID)));
	}

	// ───────────── Flood lookups ─────────────

	[Fact]
	public void FloodLookups_AreSaved_ReplacedByTheNext_ReadAndRemoved()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile a = SampleCases.RanchFlood(t.Store);
		PropertyRecord b = t.Store.SaveProperty(new PropertyRecord { StreetNumber = "2", StreetName = "Other", StructureTypeID = 1 });
		t.Store.UserName = "Reviewer";
		Guid pid = a.Property.PropertyID;
		Assert.Null(t.Store.GetFloodLookup(pid));

		Assert.True(t.Store.SaveFloodLookup(Lookup(pid)));
		FloodLookupRecord got = t.Store.GetFloodLookup(pid);
		Assert.Equal(("AE", "FLOODWAY", true, 742.5m, "NAVD88"), (got.Zone, got.ZoneSubtype, got.InFloodway, got.Bfe, got.BfeDatum));
		Assert.Equal(("39113C0157", "E", new DateTime(2011, 1, 6), "399990"), (got.FirmPanel, got.PanelSuffix, got.PanelEffectiveDate, got.CommunityId));
		Assert.Equal((FloodLookupRecord.NfhlSource, 39.7, -84.2, "Reviewer"), (got.Source, got.Latitude, got.Longitude, got.RetrievedBy));
		Assert.Equal(new DateTime(2026, 9, 20, 14, 0, 0, DateTimeKind.Utc), got.RetrievedOn);
		Assert.Equal(DateTimeKind.Utc, got.RetrievedOn.Kind);

		FloodLookupRecord newer = Lookup(pid, "X");
		newer.RetrievedOn = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
		Assert.True(t.Store.SaveFloodLookup(newer));
		Assert.Equal("X", t.Store.GetFloodLookup(pid).Zone);
		Assert.Equal(1, t.Database.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM FloodLookup")));

		t.Store.SaveFloodLookup(Lookup(b.PropertyID, "VE"));
		Dictionary<Guid, FloodLookupRecord> many = t.Store.GetFloodLookups(new[] { pid, b.PropertyID, Guid.NewGuid() });
		Assert.Equal(2, many.Count);
		Assert.Equal("VE", many[b.PropertyID].Zone);

		// A structure not saved yet has nothing to keep a lookup with.
		Assert.False(t.Store.SaveFloodLookup(Lookup(Guid.NewGuid())));
		Assert.True(t.Store.RemoveFloodLookup(pid));
		Assert.False(t.Store.RemoveFloodLookup(pid));
		Assert.Null(t.Store.GetFloodLookup(pid));
	}

	[Fact]
	public void SavingALookup_IsNotAnEdit_AndWorksOnFinalAssessments()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		t.Store.SetStatus(f.Assessment.AssessmentID, AssessmentStatus.Final);
		int history = t.Store.GetPropertyHistory(f.Property.PropertyID).Count;
		long version = t.Store.GetVersion(f.Property.PropertyID);
		Assert.True(t.Store.SaveFloodLookup(Lookup(f.Property.PropertyID)));
		Assert.Equal(history, t.Store.GetPropertyHistory(f.Property.PropertyID).Count);
		Assert.Equal(version, t.Store.GetVersion(f.Property.PropertyID));
		Assert.Null(t.Store.LoadAssessment(f.Assessment.AssessmentID).Property.BFE);   // the structure's own fields are untouched
	}

	[Fact]
	public void RecentlyDeleted_KeepsAStructuresLookup_AndRemovingItForGoodRemovesIt()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		Guid pid = f.Property.PropertyID;
		t.Store.SaveFloodLookup(Lookup(pid));
		t.Store.TrashProperty(pid);
		Assert.NotNull(t.Store.GetFloodLookup(pid));
		t.Store.Restore("S:" + pid.ToString("D"));
		Assert.Equal("AE", t.Store.GetFloodLookup(pid).Zone);
		t.Store.TrashProperty(pid);
		t.Store.EmptyTrash();
		Assert.Null(t.Store.GetProperty(pid));
		Assert.Equal(0, t.Database.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM FloodLookup")));
	}

	[Fact]
	public void ABackup_KeepsTheLookupsAndMarks()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		EventRecord flood = t.Store.SaveEvent(new EventRecord { Name = "September flood" });
		t.Store.SaveFloodLookup(Lookup(f.Property.PropertyID));
		t.Store.SaveHighWaterMarks(flood.EventID, Marks(), "File: marks.csv");
		BackupInfo backup = new BackupService(t.Database).BackUp("test");
		SdeStore copy = new SdeStore(SdeDatabase.Open(Path.Combine(backup.Folder, "sde.sqlite")));
		Assert.Equal("AE", copy.GetFloodLookup(f.Property.PropertyID).Zone);
		Assert.Equal(2, copy.GetHighWaterMarks(flood.EventID).Marks.Count);
	}

	// ───────────── High-water marks ─────────────

	[Fact]
	public void HighWaterMarks_AreKeptPerEvent_ReplacedByTheNextSet_AndCleared()
	{
		using TempDatabase t = new TempDatabase();
		EventRecord flood = t.Store.SaveEvent(new EventRecord { Name = "September flood" });
		EventRecord storm = t.Store.SaveEvent(new EventRecord { Name = "October storm" });
		t.Store.UserName = "Reviewer";
		Assert.Null(t.Store.GetHighWaterMarks(flood.EventID));

		HighWaterMarkSetRecord set = t.Store.SaveHighWaterMarks(flood.EventID, Marks(), "File: marks.csv", "NAVD88");
		StoredHighWaterMarks got = t.Store.GetHighWaterMarks(flood.EventID);
		Assert.Equal((set.SetID, "File: marks.csv", "NAVD88", 2, "Reviewer"), (got.Set.SetID, got.Set.Source, got.Set.DatumName, got.Set.MarkCount, got.Set.LoadedBy));
		Assert.NotNull(got.Set.LoadedOn);
		Assert.Equal(Marks(), got.Marks);
		Assert.Null(t.Store.GetHighWaterMarks(storm.EventID));

		t.Store.SaveHighWaterMarks(flood.EventID, Marks(750m).Take(1).ToList(), "USGS STN event 123 (Example flood)");
		got = t.Store.GetHighWaterMarks(flood.EventID);
		Assert.Equal(("USGS STN event 123 (Example flood)", 750m), (got.Set.Source, Assert.Single(got.Marks).ElevationFt));
		Assert.Equal(1, t.Database.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM HighWaterMarkSet")));
		Assert.Equal(1, t.Database.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM HighWaterMarkRow")));

		Assert.Throws<ArgumentException>(() => t.Store.SaveHighWaterMarks(flood.EventID, new List<HighWaterMark>(), "File: empty.csv"));
		Assert.Throws<ArgumentException>(() => t.Store.SaveHighWaterMarks(Guid.NewGuid(), Marks(), "File: marks.csv"));
		Assert.True(t.Store.ClearHighWaterMarks(flood.EventID));
		Assert.False(t.Store.ClearHighWaterMarks(flood.EventID));
		Assert.Null(t.Store.GetHighWaterMarks(flood.EventID));
		Assert.Equal(0, t.Database.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM HighWaterMarkRow")));
	}

	[Fact]
	public void TheSetForEveryEvent_IsUsedByEventsWithoutTheirOwn()
	{
		using TempDatabase t = new TempDatabase();
		EventRecord flood = t.Store.SaveEvent(new EventRecord { Name = "September flood" });
		EventRecord storm = t.Store.SaveEvent(new EventRecord { Name = "October storm" });
		t.Store.SaveHighWaterMarks(null, Marks(700m), "File: county.csv");
		t.Store.SaveHighWaterMarks(flood.EventID, Marks(742.5m), "File: river.csv");
		Assert.Equal(742.5m, t.Store.HighWaterMarksFor(flood.EventID).Marks[0].ElevationFt);
		Assert.Equal(700m, t.Store.HighWaterMarksFor(storm.EventID).Marks[0].ElevationFt);
		Assert.Equal(700m, t.Store.HighWaterMarksFor(null).Marks[0].ElevationFt);
		Assert.Null(t.Store.GetHighWaterMarks(storm.EventID));
		// Only one set for every event: a second replaces it.
		t.Store.SaveHighWaterMarks(null, Marks(701m), "File: county2.csv");
		Assert.Equal(2, t.Store.GetHighWaterMarkSets().Count);
		Assert.Throws<SqliteException>(() => t.Database.InTransaction((c, tx) =>
			c.Execute("INSERT INTO HighWaterMarkSet (SetID, EventID, Source) VALUES ('x', NULL, 'Twice')", transaction: tx)));
	}

	[Fact]
	public void DeletingAnEvent_RemovesItsMarks()
	{
		using TempDatabase t = new TempDatabase();
		EventRecord flood = t.Store.SaveEvent(new EventRecord { Name = "September flood" });
		t.Store.SaveHighWaterMarks(flood.EventID, Marks(), "File: marks.csv");
		t.Store.DeleteEvent(flood.EventID);
		Assert.Equal(0, t.Database.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM HighWaterMarkSet")));
		Assert.Equal(0, t.Database.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM HighWaterMarkRow")));
	}

	// ───────────── The export and work packages ─────────────

	[Fact]
	public void LookupsAndMarks_NeverChangeTheFemaExport()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		EventRecord flood = t.Store.SaveEvent(new EventRecord { Name = "September flood" });
		t.Store.SetEvent(new[] { f.Assessment.AssessmentID }, flood.EventID);
		ExchangeService x = new ExchangeService(t.Store);
		(string before, _) = x.Export(x.ReadDataset(), Path.Combine(t.Folder, "a"));
		t.Store.SaveFloodLookup(Lookup(f.Property.PropertyID, "VE"));
		t.Store.SaveHighWaterMarks(flood.EventID, Marks(), "File: marks.csv");
		(string after, _) = x.Export(x.ReadDataset(), Path.Combine(t.Folder, "b"));
		Assert.Equal(File.ReadAllBytes(Path.Combine(before, "Export.json")), File.ReadAllBytes(Path.Combine(after, "Export.json")));
		Assert.DoesNotContain("39113C0157", File.ReadAllText(Path.Combine(after, "Export.json")));
	}

	[Fact]
	public void WorkPackages_CarryLookupsAndTheEventsMarks_OutAndBack()
	{
		using TempDatabase office = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(office.Store);
		Guid pid = f.Property.PropertyID;
		Guid aid = f.Assessment.AssessmentID;
		EventRecord flood = office.Store.SaveEvent(new EventRecord { Name = "September flood" });
		EventRecord other = office.Store.SaveEvent(new EventRecord { Name = "Other" });
		office.Store.SetEvent(new[] { aid }, flood.EventID);
		office.Store.SaveFloodLookup(Lookup(pid));
		office.Store.SaveHighWaterMarks(flood.EventID, Marks(), "File: marks.csv");
		office.Store.SaveHighWaterMarks(other.EventID, Marks(700m), "File: other.csv");
		string outPath = Path.Combine(office.Folder, "out" + WorkPackages.Extension);
		string backPath = Path.Combine(office.Folder, "back" + WorkPackages.Extension);
		WorkPackages.CheckOut(office.Store, new[] { aid }, outPath, "Team 1");

		SdeStore field = new SdeStore(SdeDatabase.Open(WorkPackages.Open(outPath, Path.Combine(office.Folder, "field"))));
		Assert.Equal("AE", field.GetFloodLookup(pid).Zone);
		Assert.Equal(Marks(), field.GetHighWaterMarks(flood.EventID).Marks);
		Assert.Empty(field.GetHighWaterMarkSets().Where(s => s.EventID == other.EventID));
		// In the field: a newer lookup, the assessment changed.
		FloodLookupRecord newer = Lookup(pid, "X");
		newer.RetrievedOn = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);
		field.SaveFloodLookup(newer);
		field.Apply(aid, x => x.Assessment.InspectorName = "Field tech");
		WorkPackages.SendBack(field, backPath);

		CheckInResult result = WorkPackages.CheckIn(office.Store, backPath);
		Assert.Equal(1, result.CheckedIn);
		Assert.Equal("X", office.Store.GetFloodLookup(pid).Zone);
		Assert.Equal(1, office.Store.GetHighWaterMarkSets().Count(s => s.EventID == flood.EventID));
		Assert.Equal(Marks(), office.Store.GetHighWaterMarks(flood.EventID).Marks);
	}

	// ───────────── The quality checks ─────────────

	[Fact]
	public void TheZoneCheck_RunsFromAStoredLookup()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = Placed(t.Store);
		AssessmentFile notLooked = SampleCases.RanchFlood(t.Store);
		Assert.Contains(QaRules.NotChecked(t.Store.LoadQaInput()), n => n.RuleId == QaRuleIds.ZoneDiffersFromNfhl && n.Count == 0);

		t.Store.SaveFloodLookup(Lookup(f.Property.PropertyID, "X"));
		QaResult result = QaRules.CheckWithStatus(t.Store.LoadQaInput());
		QaFinding zone = Assert.Single(result.Findings, x => x.RuleId == QaRuleIds.ZoneDiffersFromNfhl);
		Assert.Equal(f.Property.PropertyID, zone.PropertyId);
		Assert.Contains("FEMA's flood map shows X here", zone.Message);
		QaNotChecked partly = Assert.Single(result.NotChecked, n => n.RuleId == QaRuleIds.ZoneDiffersFromNfhl);
		Assert.Equal((QaNotCheckedReason.NoStoredFloodLookup, 1, "Not checked for 1 of 2 structures: no stored flood lookup."), (partly.Reason, partly.Count, partly.Message));

		t.Store.SaveFloodLookup(Lookup(notLooked.Property.PropertyID, t.Store.Reference.LookupName(notLooked.Property.FIRMZone) ?? "AE"));
		Assert.DoesNotContain(QaRules.NotChecked(t.Store.LoadQaInput()), n => n.RuleId == QaRuleIds.ZoneDiffersFromNfhl);
	}

	[Fact]
	public void ALookupMadeWhereTheStructureNoLongerIs_IsStale_AndSaysSo()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = Placed(t.Store);
		// 10 m away still counts as the same place; about 110 m does not.
		t.Store.SaveFloodLookup(Lookup(f.Property.PropertyID, "X", latitude: 39.70009));
		Assert.Single(QaRules.Check(t.Store.LoadQaInput()), x => x.RuleId == QaRuleIds.ZoneDiffersFromNfhl);

		t.Store.SaveFloodLookup(Lookup(f.Property.PropertyID, "X", latitude: 39.701));
		QaInput input = t.Store.LoadQaInput();
		Assert.Equal(new[] { f.Property.PropertyID }, input.StaleFloodLookups);
		QaResult result = QaRules.CheckWithStatus(input);
		Assert.DoesNotContain(result.Findings, x => x.RuleId == QaRuleIds.ZoneDiffersFromNfhl);
		QaNotChecked stale = Assert.Single(result.NotChecked, n => n.RuleId == QaRuleIds.ZoneDiffersFromNfhl);
		Assert.Equal((QaNotCheckedReason.StaleFloodLookup, "Not checked: the structure has moved since its flood lookup.", true), (stale.Reason, stale.Message, stale.CanCheckOnline));
	}

	[Fact]
	public void TheHighWaterMarkCheck_RunsFromTheEventsStoredMarks()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = Placed(t.Store, floodElevation: 745m);
		AssessmentFile elsewhere = Placed(t.Store, floodElevation: 745m);
		EventRecord flood = t.Store.SaveEvent(new EventRecord { Name = "September flood" });
		EventRecord storm = t.Store.SaveEvent(new EventRecord { Name = "October storm" });
		t.Store.SetEvent(new[] { f.Assessment.AssessmentID }, flood.EventID);
		t.Store.SetEvent(new[] { elsewhere.Assessment.AssessmentID }, storm.EventID);
		QaNotChecked none = Assert.Single(QaRules.NotChecked(t.Store.LoadQaInput()), n => n.RuleId == QaRuleIds.DepthVsHighWaterMarks);
		Assert.Equal(("Not checked: no high-water marks stored.", QaNotCheckedReason.NoHighWaterMarks, 0), (none.Message, none.Reason, none.Count));

		t.Store.SaveHighWaterMarks(flood.EventID, Marks(742.5m), "File: marks.csv");
		QaResult result = QaRules.CheckWithStatus(t.Store.LoadQaInput());
		QaFinding marks = Assert.Single(result.Findings, x => x.RuleId == QaRuleIds.DepthVsHighWaterMarks);
		Assert.Equal(f.Assessment.AssessmentID, marks.AssessmentId);
		Assert.Contains("the high-water marks suggest about 742.5 ft", marks.Message);
		QaNotChecked partly = Assert.Single(result.NotChecked, n => n.RuleId == QaRuleIds.DepthVsHighWaterMarks);
		Assert.Equal(("Not checked for 1 of 2 assessments: no high-water marks stored for their event.", 1), (partly.Message, partly.Count));

		// The set for every event covers the storm too.
		t.Store.SaveHighWaterMarks(null, Marks(745m), "File: county.csv");
		result = QaRules.CheckWithStatus(t.Store.LoadQaInput());
		Assert.Single(result.Findings, x => x.RuleId == QaRuleIds.DepthVsHighWaterMarks);
		Assert.DoesNotContain(result.NotChecked, n => n.RuleId == QaRuleIds.DepthVsHighWaterMarks);
	}

	[Fact]
	public void TheStoredDataPath_HasNoNetworkCode()
	{
		// LoadQaInput lives in Sde.Data and the rules in Sde.Core: neither can reach HTTP or the online clients.
		foreach (System.Reflection.Assembly a in new[] { typeof(SdeStore).Assembly, typeof(QaRules).Assembly })
		{
			Assert.DoesNotContain(a.GetReferencedAssemblies(), r => r.Name is "System.Net.Http" or "Sde.Interop");
		}
	}
}
