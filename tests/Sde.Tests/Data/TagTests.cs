using Dapper;
using Microsoft.Data.Sqlite;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Tests.Data;

/// <summary>Tags on structures (MacSchemaVersion 4): the upgrade, the store, Recently deleted, work packages, and the export.</summary>
public class TagTests
{
	private static PropertyRecord Structure(SdeStore store, string street) =>
		store.SaveProperty(new PropertyRecord { StreetNumber = "1", StreetName = street, StructureTypeID = 1 });

	private static string SchemaDump(SqliteConnection c) =>
		string.Join("\n", c.Query<string>("SELECT name || ':' || COALESCE(sql, '') FROM sqlite_master ORDER BY name"))
		+ "\n" + string.Join("\n", c.Query<string>("SELECT Key || '=' || Value FROM SchemaInfo ORDER BY Key"));

	[Fact]
	public void OpeningAV3Database_AddsTheTagTables_AndKeepsItsData()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		t.Store.AddReviewComment(f.Assessment.AssessmentID, "Check the depth");
		string data;
		using (SqliteConnection c = t.Database.Connect())
		{
			// What v3.0 left: MacSchemaVersion 3, its own Schema.sql, no tag tables.
			c.Execute("""
				DROP INDEX IX_PropertyTag_Tag; DROP TABLE PropertyTag; DROP TABLE Tag;
				UPDATE SchemaInfo SET Value = '3' WHERE Key = 'MacSchemaVersion';
				UPDATE SchemaInfo SET Value = 'v3.0 schema' WHERE Key = 'MacSchemaHash';
				""");
			data = string.Join("|", c.Query<string>("SELECT PropertyID || OwnerLastName FROM Property")) + string.Join("|", c.Query<string>("SELECT Body FROM ReviewComment"));
		}
		SqliteConnection.ClearAllPools();
		Assert.True(SdeDatabase.NeedsUpgrade(t.Database.FilePath));

		SdeDatabase reopened = SdeDatabase.Open(t.Database.FilePath);
		using SqliteConnection again = reopened.Connect();
		Assert.Equal(2, again.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('Tag', 'PropertyTag')"));
		Assert.Equal(1, again.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_PropertyTag_Tag'"));
		Assert.Equal("4", again.ExecuteScalar<string>("SELECT Value FROM SchemaInfo WHERE Key = 'MacSchemaVersion'"));
		Assert.Equal(SdeDatabase.SchemaHash, again.ExecuteScalar<string>("SELECT Value FROM SchemaInfo WHERE Key = 'MacSchemaHash'"));
		Assert.Equal(data, string.Join("|", again.Query<string>("SELECT PropertyID || OwnerLastName FROM Property")) + string.Join("|", again.Query<string>("SELECT Body FROM ReviewComment")));
		Assert.False(SdeDatabase.NeedsUpgrade(t.Database.FilePath));
		SdeStore store = new SdeStore(reopened);
		TagRecord tag = store.CreateTag("Revisit", TagColours.Amber);
		store.SetTags(f.Property.PropertyID, new[] { tag.TagID });
		Assert.Equal("Revisit", Assert.Single(store.GetTagsFor(f.Property.PropertyID)).Name);
	}

	[Fact]
	public void ADatabaseFromANewerVersion_IsLeftAlone()
	{
		using TempDatabase t = new TempDatabase();
		SampleCases.RanchFlood(t.Store);
		string before;
		using (SqliteConnection c = t.Database.Connect())
		{
			c.Execute("UPDATE SchemaInfo SET Value = '5' WHERE Key = 'MacSchemaVersion'; UPDATE SchemaInfo SET Value = 'v4.0 schema' WHERE Key = 'MacSchemaHash';");
			before = SchemaDump(c);
		}
		SqliteConnection.ClearAllPools();
		Assert.False(SdeDatabase.NeedsUpgrade(t.Database.FilePath));

		SdeDatabase reopened = SdeDatabase.Open(t.Database.FilePath);
		using SqliteConnection again = reopened.Connect();
		Assert.Equal(before, SchemaDump(again));
	}

	/// <summary>What v3.0 checks when it opens a v3.5 database: it runs its Schema.sql only when MacSchemaVersion is below 3.</summary>
	[Fact]
	public void AV30App_LeavesAV35DatabaseAlone_AndCanStillDeleteATaggedStructure()
	{
		using TempDatabase t = new TempDatabase();
		PropertyRecord p = Structure(t.Store, "River");
		TagRecord tag = t.Store.CreateTag("Revisit");
		t.Store.SetTags(p.PropertyID, new[] { tag.TagID });
		using SqliteConnection c = t.Database.Connect();
		Assert.True(c.ExecuteScalar<int>("SELECT CAST(Value AS INTEGER) FROM SchemaInfo WHERE Key = 'MacSchemaVersion'") >= 3);
		Assert.Equal(1, c.ExecuteScalar<int>("SELECT CAST(Value AS INTEGER) FROM SchemaInfo WHERE Key = 'SchemaVersion'"));
		// v3.0 removes a structure from its own tables only; the tag rows must go with it, not block it.
		c.Execute("DELETE FROM CustomField_Results WHERE ObjectIdentifier = @pid; DELETE FROM Shapes WHERE PropertyID = @pid; DELETE FROM Property WHERE PropertyID = @pid;",
			new { pid = p.PropertyID.ToString("D") });
		Assert.Equal(0, c.ExecuteScalar<int>("SELECT COUNT(*) FROM PropertyTag"));
		Assert.Equal(1, c.ExecuteScalar<int>("SELECT COUNT(*) FROM Tag"));
	}

	[Fact]
	public void Tags_CanBeMadeRenamedRecolouredReorderedAndDeleted()
	{
		using TempDatabase t = new TempDatabase();
		PropertyRecord p = Structure(t.Store, "River");
		TagRecord revisit = t.Store.CreateTag("  Revisit   soon ", "amber");
		TagRecord called = t.Store.CreateTag("Owner called", TagColours.Green);
		Assert.Equal("Revisit soon", revisit.Name);
		Assert.Equal(TagColours.Amber, revisit.Colour);
		Assert.Equal(new[] { "Revisit soon", "Owner called" }, t.Store.GetTags().Select(x => x.Name));

		t.Store.RenameTag(revisit.TagID, "Revisit");
		t.Store.SetTagColour(revisit.TagID, TagColours.Red);
		t.Store.ReorderTags(new[] { called.TagID });
		Assert.Equal(new[] { "Owner called", "Revisit" }, t.Store.GetTags().Select(x => x.Name));
		Assert.Equal(TagColours.Red, t.Store.GetTags().Single(x => x.TagID == revisit.TagID).Colour);

		Assert.Throws<ArgumentException>(() => t.Store.CreateTag("Pink one", "Pink"));
		Assert.Throws<ArgumentException>(() => t.Store.SetTagColour(revisit.TagID, "Pink"));
		Assert.Throws<ArgumentException>(() => t.Store.CreateTag("   "));
		Assert.Throws<ArgumentException>(() => t.Store.CreateTag(new string('x', SdeStore.MaxTagNameLength + 1)));
		Assert.Throws<ArgumentException>(() => t.Store.RenameTag(12345, "Gone"));

		t.Store.SetTags(p.PropertyID, new[] { revisit.TagID, called.TagID });
		Assert.Equal(1, t.Store.DeleteTag(revisit.TagID));
		Assert.Equal("Owner called", Assert.Single(t.Store.GetTags()).Name);
		Assert.Equal("Owner called", Assert.Single(t.Store.GetTagsFor(p.PropertyID)).Name);
		Assert.Equal(1, t.Database.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM PropertyTag")));
	}

	[Fact]
	public void TagNames_AreUnique_IgnoringCase()
	{
		using TempDatabase t = new TempDatabase();
		TagRecord revisit = t.Store.CreateTag("Revisit");
		TagRecord other = t.Store.CreateTag("Other");
		ArgumentException e = Assert.Throws<ArgumentException>(() => t.Store.CreateTag("REVISIT"));
		Assert.Contains("already a tag called Revisit", e.Message);
		Assert.Throws<ArgumentException>(() => t.Store.RenameTag(other.TagID, "revisit"));
		t.Store.RenameTag(revisit.TagID, "REVISIT");
		Assert.Equal("REVISIT", t.Store.GetTags().Single(x => x.TagID == revisit.TagID).Name);
		// The table itself refuses a duplicate, whoever writes it.
		Assert.Throws<SqliteException>(() => t.Database.InTransaction((c, tx) =>
			c.Execute("INSERT INTO Tag (Name, Colour) VALUES ('revisit', 'Blue')", transaction: tx)));
	}

	[Fact]
	public void Tags_CanBeSetOnOneStructure_OrAddedAndRemovedOnManyAtOnce()
	{
		using TempDatabase t = new TempDatabase();
		PropertyRecord a = Structure(t.Store, "Alpha");
		PropertyRecord b = Structure(t.Store, "Bravo");
		PropertyRecord c = Structure(t.Store, "Charlie");
		TagRecord revisit = t.Store.CreateTag("Revisit");
		TagRecord called = t.Store.CreateTag("Owner called");

		t.Store.SetTags(a.PropertyID, new[] { called.TagID, revisit.TagID, revisit.TagID });
		Assert.Equal(new[] { "Revisit", "Owner called" }, t.Store.GetTagsFor(a.PropertyID).Select(x => x.Name));
		t.Store.SetTags(a.PropertyID, new[] { called.TagID });
		Assert.Equal("Owner called", Assert.Single(t.Store.GetTagsFor(a.PropertyID)).Name);
		Assert.Throws<ArgumentException>(() => t.Store.SetTags(a.PropertyID, new[] { 999L }));
		Assert.Throws<ArgumentException>(() => t.Store.SetTags(Guid.NewGuid(), new[] { called.TagID }));
		Assert.Equal("Owner called", Assert.Single(t.Store.GetTagsFor(a.PropertyID)).Name);

		Assert.Equal(2, t.Store.AddTag(revisit.TagID, new[] { a.PropertyID, b.PropertyID, b.PropertyID, Guid.NewGuid() }));
		Assert.Equal(1, t.Store.AddTag(revisit.TagID, new[] { a.PropertyID, c.PropertyID }));
		Assert.Equal(new[] { a.PropertyID, b.PropertyID, c.PropertyID }.OrderBy(g => g.ToString("D")), t.Store.GetPropertiesWithTag(revisit.TagID));
		Assert.Equal(2, t.Store.RemoveTag(revisit.TagID, new[] { b.PropertyID, c.PropertyID, Guid.NewGuid() }));

		ILookup<Guid, TagRecord> many = t.Store.GetTagsForMany(new[] { a.PropertyID, b.PropertyID, c.PropertyID });
		Assert.Equal(new[] { "Revisit", "Owner called" }, many[a.PropertyID].Select(x => x.Name));
		Assert.Empty(many[b.PropertyID]);
		Dictionary<long, int> counts = t.Store.GetTagCounts();
		Assert.Equal(1, counts[revisit.TagID]);
		Assert.Equal(1, counts[called.TagID]);
	}

	[Fact]
	public void Tagging_IsNotAnEdit_AndWorksOnFinalAssessments()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		t.Store.SetStatus(f.Assessment.AssessmentID, AssessmentStatus.Final);
		int history = t.Store.GetPropertyHistory(f.Property.PropertyID).Count;
		long version = t.Store.GetVersion(f.Property.PropertyID);
		TagRecord tag = t.Store.CreateTag("Revisit");
		t.Store.AddTag(tag.TagID, new[] { f.Property.PropertyID });
		Assert.Single(t.Store.GetTagsFor(f.Property.PropertyID));
		Assert.Equal(history, t.Store.GetPropertyHistory(f.Property.PropertyID).Count);
		Assert.Equal(version, t.Store.GetVersion(f.Property.PropertyID));
	}

	[Fact]
	public void RecentlyDeleted_KeepsAStructuresTags_AndRemovingItForGoodRemovesThem()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		PropertyRecord other = Structure(t.Store, "Other");
		Guid pid = f.Property.PropertyID;
		TagRecord revisit = t.Store.CreateTag("Revisit");
		TagRecord called = t.Store.CreateTag("Owner called");
		t.Store.SetTags(pid, new[] { revisit.TagID, called.TagID });
		t.Store.AddTag(revisit.TagID, new[] { other.PropertyID });

		t.Store.TrashProperty(pid);
		Assert.Equal(1, t.Store.GetTagCounts()[revisit.TagID]);
		Assert.Equal(0, t.Store.GetTagCounts()[called.TagID]);
		Assert.Equal(new[] { other.PropertyID }, t.Store.GetPropertiesWithTag(revisit.TagID));
		Assert.Equal(2, t.Store.GetPropertiesWithTag(revisit.TagID, activeOnly: false).Count);

		t.Store.Restore("S:" + pid.ToString("D"));
		Assert.Equal(new[] { "Revisit", "Owner called" }, t.Store.GetTagsFor(pid).Select(x => x.Name));
		Assert.Equal(2, t.Store.GetTagCounts()[revisit.TagID]);

		t.Store.TrashProperty(pid);
		t.Store.EmptyTrash();
		Assert.Null(t.Store.GetProperty(pid));
		Assert.Equal(0, t.Database.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM PropertyTag WHERE PropertyID = @pid", new { pid })));
		Assert.Equal(2, t.Store.GetTags().Count);
		Assert.Single(t.Store.GetTagsFor(other.PropertyID));
	}

	[Fact]
	public void Tagging_NeverChangesTheFemaExport()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		ExchangeService x = new ExchangeService(t.Store);
		(string before, _) = x.Export(x.ReadDataset(), Path.Combine(t.Folder, "a"));
		TagRecord tag = t.Store.CreateTag("Revisit", TagColours.Violet);
		t.Store.AddTag(tag.TagID, new[] { f.Property.PropertyID });
		(string after, _) = x.Export(x.ReadDataset(), Path.Combine(t.Folder, "b"));
		Assert.Equal(File.ReadAllBytes(Path.Combine(before, "Export.json")), File.ReadAllBytes(Path.Combine(after, "Export.json")));
		Assert.DoesNotContain("Revisit", File.ReadAllText(Path.Combine(after, "Export.json")));
	}

	[Fact]
	public void WorkPackages_CarryAStructuresTags_OutAndBack_MatchingTagsByName()
	{
		using TempDatabase office = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(office.Store);
		Guid pid = f.Property.PropertyID;
		Guid aid = f.Assessment.AssessmentID;
		TagRecord unused = office.Store.CreateTag("Unused");
		TagRecord revisit = office.Store.CreateTag("Revisit", TagColours.Amber);
		office.Store.SetTags(pid, new[] { revisit.TagID });
		string outPath = Path.Combine(office.Folder, "out" + WorkPackages.Extension);
		string backPath = Path.Combine(office.Folder, "back" + WorkPackages.Extension);
		WorkPackages.CheckOut(office.Store, new[] { aid }, outPath, "Team 1");

		SdeStore field = new SdeStore(SdeDatabase.Open(WorkPackages.Open(outPath, Path.Combine(office.Folder, "field"))));
		TagRecord there = Assert.Single(field.GetTagsFor(pid));
		Assert.Equal("Revisit", there.Name);
		Assert.Equal(TagColours.Amber, there.Colour);
		Assert.DoesNotContain(field.GetTags(), x => x.Name == "Unused");
		// The field adds a tag of its own and changes the assessment.
		TagRecord fieldTag = field.CreateTag("Dog on site", TagColours.Red);
		field.SetTags(pid, new[] { there.TagID, fieldTag.TagID });
		field.Apply(aid, x => x.Assessment.InspectorName = "Field tech");
		WorkPackages.SendBack(field, backPath);

		// Meanwhile the office makes a tag that takes the number the field's tag has over there.
		office.Store.CreateTag("Office only");
		CheckInResult result = WorkPackages.CheckIn(office.Store, backPath);
		Assert.Equal(1, result.CheckedIn);
		Assert.Equal(new[] { "Revisit", "Dog on site" }.OrderBy(n => n), office.Store.GetTagsFor(pid).Select(x => x.Name).OrderBy(n => n));
		Assert.Equal(TagColours.Red, office.Store.GetTags().Single(x => x.Name == "Dog on site").Colour);
		Assert.Equal(4, office.Store.GetTags().Count);
		Assert.Empty(office.Store.GetPropertiesWithTag(unused.TagID));
	}
}
