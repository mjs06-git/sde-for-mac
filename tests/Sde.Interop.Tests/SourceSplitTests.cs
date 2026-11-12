using Sde.Core.Records;
using Sde.Data;

namespace Sde.Interop.Tests;

/// <summary>
/// One source row that covers several buildings: a number-of-buildings column or a delimited list becomes several
/// structures, each with a stable record ID so a second import updates them instead of duplicating them.
/// </summary>
public class SourceSplitTests
{
	private static readonly string[] Header = { "Parcel ID", "Site Address", "Site City", "Site ZIP", "Owner Name", "Property Class", "Year Built", "Living Area", "Stories", "Buildings" };

	private static SourceTable Table(params string[][] rows) =>
		new SourceTable("split.csv", "split", Header, rows.Select((r, i) => new SourceRow(i + 2, r)).ToList());

	private static SourceProfile CountProfile(SourceSplitMode mode = SourceSplitMode.CountColumn)
	{
		SourceProfile p = ShippedProfiles.Get(ShippedProfiles.CountyParcels);
		p.SplitMode = mode;
		p.SplitColumn = "Buildings";
		return p;
	}

	private static SourcePreview Preview(SdeStore store, SourceTable raw, SourceProfile p) =>
		SourceImport.Preview(SourceTable.Shape(raw, p), p, store, TestContext.Current.CancellationToken);

	private static string[] Row(string parcel, string buildings) => new[] { parcel, "100 SAMPLE AVE", "EXAMPLEVILLE", "99999", "EXAMPLE, ALEX", "Single Family", "1955", "1200", "1", buildings };

	[Fact]
	public void Off_ByDefault_ARowIsOneStructure_AndTheColumnIsIgnored()
	{
		using TempDatabase t = new TempDatabase();
		SourceProfile p = ShippedProfiles.Get(ShippedProfiles.CountyParcels);
		Assert.Equal(SourceSplitMode.None, p.SplitMode);
		SourcePreview preview = Preview(t.Store, Table(Row("P-1", "3")), p);
		ImportRow row = Assert.Single(preview.Rows);
		Assert.Equal(("P-1", false, 1), (row.RecordId, row.IsSplit, row.SplitCount));
	}

	[Fact]
	public void CountColumn_NBecomesNStructures_SharedFieldsCopied_SuffixInTheUnit()
	{
		using TempDatabase t = new TempDatabase();
		SourcePreview preview = Preview(t.Store, Table(Row("P-1", "3"), Row("P-2", "1"), Row("P-3", ""), Row("P-4", "0")), CountProfile());
		Assert.Equal(new[] { "P-1", "P-1#2", "P-1#3", "P-2", "P-3", "P-4" }, preview.Rows.Select(r => r.RecordId).ToArray());
		Assert.All(preview.Rows, r => Assert.Equal(ImportRowStatus.NewStructure, r.Status));

		List<ImportRow> group = preview.Groups[0].Rows;
		Assert.Equal(3, group.Count);
		Assert.True(preview.Groups[0].IsSplit);
		Assert.Equal(new[] { 1, 2, 3 }, group.Select(r => r.SplitIndex).ToArray());
		Assert.All(group, r => Assert.Equal(("P-1", 3, 2, "EXAMPLEVILLE", "EXAMPLE"), (r.BaseRecordId, r.SplitCount, r.RowNumber, r.Property.City, r.Property.OwnerLastName)));
		Assert.Equal(new[] { "100 Bldg 1", "100 Bldg 2", "100 Bldg 3" }, group.Select(r => r.Property.StreetNumber).ToArray());
		Assert.Equal(new[] { "Bldg 1", "Bldg 2", "Bldg 3" }, group.Select(r => r.SplitLabel).ToArray());

		// A count of 1, blank or 0 is the plain row.
		Assert.All(preview.Rows.Skip(3), r => Assert.Equal(("100", false), (r.Property.StreetNumber, r.IsSplit)));

		SourceImportResult result = SourceImport.Import(t.Store, preview, "split.csv");
		Assert.Equal(6, result.Created);
		Assert.Equal(6, t.Store.Counts().Properties);
	}

	[Fact]
	public void DelimitedColumn_OneStructurePerItem_OrderDoesNotMatter()
	{
		using TempDatabase t = new TempDatabase();
		SourceProfile p = CountProfile(SourceSplitMode.DelimitedColumn);
		p.SplitDelimiter = ",";
		SourcePreview first = Preview(t.Store, Table(Row("P-1", "Unit A, Unit B,, Unit A")), p);
		Assert.Equal(new[] { "P-1#Unit A", "P-1#Unit B" }, first.Rows.Select(r => r.RecordId).ToArray());
		Assert.Equal(new[] { "100 Unit A", "100 Unit B" }, first.Rows.Select(r => r.Property.StreetNumber).ToArray());
		Assert.Contains(first.Rows[0].Warnings, w => w.Contains("listed more than once"));
		SourceImport.Import(t.Store, first, "a.csv");

		// The same list in another order updates the same two structures.
		SourcePreview again = Preview(t.Store, Table(Row("P-1", "Unit B,Unit A")), p);
		Assert.All(again.Rows, r => Assert.Equal(ImportRowStatus.Unchanged, r.Status));
		Assert.Empty(again.MissingSplitStructures);
		Assert.False(again.CanImport);
	}

	[Fact]
	public void DefaultDelimiter_IsASemicolon_AndALotNumberCanBeTheSuffixField()
	{
		using TempDatabase t = new TempDatabase();
		SourceProfile p = CountProfile(SourceSplitMode.DelimitedColumn);
		p.SplitSuffixField = SourceProfile.SplitFieldLot;
		SourcePreview preview = Preview(t.Store, Table(Row("P-1", "N;S")), p);
		Assert.Equal(new[] { "N", "S" }, preview.Rows.Select(r => r.Property.LotNumber).ToArray());
		Assert.All(preview.Rows, r => Assert.Equal("100", r.Property.StreetNumber));
	}

	[Fact]
	public void ReImport_IsStable_AndCreatesNoDuplicates()
	{
		using TempDatabase t = new TempDatabase();
		SourceProfile p = CountProfile();
		SourceTable raw = Table(Row("P-1", "3"));
		SourceImport.Import(t.Store, Preview(t.Store, raw, p), "a.csv");
		Dictionary<string, Guid> ids = t.Store.GetStructureLinks("County parcels").ToDictionary(l => l.RecordId, l => l.PropertyID);
		Assert.Equal(3, ids.Count);

		SourcePreview again = Preview(t.Store, raw, p);
		Assert.All(again.Rows, r => Assert.Equal(ImportRowStatus.Unchanged, r.Status));
		SourceImport.Import(t.Store, again, "a.csv");
		Assert.Equal(3, t.Store.Counts().Properties);
		Assert.Equal(ids, t.Store.GetStructureLinks("County parcels").ToDictionary(l => l.RecordId, l => l.PropertyID));
	}

	[Fact]
	public void ReImport_MoreBuildings_AddsNew_FewerBuildings_AreReportedNeverDeleted()
	{
		using TempDatabase t = new TempDatabase();
		SourceProfile p = CountProfile();
		SourceImport.Import(t.Store, Preview(t.Store, Table(Row("P-1", "2")), p), "a.csv");

		SourcePreview more = Preview(t.Store, Table(Row("P-1", "4")), p);
		Assert.Equal(new[] { ImportRowStatus.Unchanged, ImportRowStatus.Unchanged, ImportRowStatus.NewStructure, ImportRowStatus.NewStructure }, more.Rows.Select(r => r.Status).ToArray());
		SourceImport.Import(t.Store, more, "b.csv");
		Assert.Equal(4, t.Store.Counts().Properties);

		SourcePreview fewer = Preview(t.Store, Table(Row("P-1", "2")), p);
		Assert.All(fewer.Rows, r => Assert.Equal(ImportRowStatus.Unchanged, r.Status));
		Assert.Equal(new[] { "P-1#3", "P-1#4" }, fewer.MissingSplitStructures.Select(m => m.RecordId).OrderBy(x => x).ToArray());
		Assert.All(fewer.MissingSplitStructures, m => Assert.Contains("no longer in the file", m.Description));
		Assert.Contains(fewer.Notes, n => n.Contains("2 structures"));
		SourceImport.Import(t.Store, fewer, "c.csv");
		Assert.Equal(4, t.Store.Counts().Properties);          // nothing deleted

		// Down to one building: the first structure keeps its record ID, the other three are reported.
		SourcePreview one = Preview(t.Store, Table(Row("P-1", "1")), p);
		Assert.Equal("P-1", Assert.Single(one.Rows).RecordId);
		Assert.Equal(3, one.MissingSplitStructures.Count);
	}

	[Fact]
	public void Cap_RefusesTooManyBuildings_WithAClearMessage_AndBadNumbersAreProblems()
	{
		using TempDatabase t = new TempDatabase();
		SourcePreview preview = Preview(t.Store, Table(Row("P-1", "200"), Row("P-2", "201"), Row("P-3", "two"), Row("P-4", "-1"), Row("P-5", "2.5")), CountProfile());
		Assert.Equal(200, preview.Rows.Count(r => r.BaseRecordId == "P-1"));
		ImportRow tooMany = preview.Rows.Single(r => r.RecordId == "P-2");
		Assert.Equal(ImportRowStatus.Problem, tooMany.Status);
		Assert.Contains("limit of 200", Assert.Single(tooMany.Problems));
		Assert.False(tooMany.CanImport);
		foreach (string id in new[] { "P-3", "P-4", "P-5" })
		{
			Assert.Equal(ImportRowStatus.Problem, preview.Rows.Single(r => r.RecordId == id).Status);
		}

		SourceProfile list = CountProfile(SourceSplitMode.DelimitedColumn);
		string many = string.Join(";", Enumerable.Range(1, 201).Select(i => "B" + i));
		ImportRow listed = Assert.Single(Preview(t.Store, Table(Row("P-9", many)), list).Rows);
		Assert.Contains("limit of 200", Assert.Single(listed.Problems));
	}

	[Fact]
	public void Profile_NeedsAColumn_AndOldProfilesLoadWithTheSettingOff()
	{
		using TempDatabase t = new TempDatabase();
		SourceProfile p = CountProfile();
		p.SplitColumn = null;
		Assert.Contains("number of buildings", Assert.Single(Preview(t.Store, Table(Row("P-1", "2")), p).MappingProblems));
		p.SplitColumn = "Nope";
		Assert.Contains("'Nope'", Assert.Single(Preview(t.Store, Table(Row("P-1", "2")), p).MappingProblems));

		// A profile saved before the setting existed.
		string old = ShippedProfiles.Get(ShippedProfiles.CountyParcels).ToJson();
		Assert.DoesNotContain("Split", old);                      // off: nothing extra is written
		SourceProfile loaded = SourceProfile.FromJson(old, out List<string> warnings);
		Assert.Empty(warnings);
		Assert.Equal(SourceSplitMode.None, loaded.SplitMode);

		// A profile with the setting round-trips, and a bad suffix field falls back to the unit.
		SourceProfile on = CountProfile(SourceSplitMode.DelimitedColumn);
		on.SplitDelimiter = "|";
		SourceProfile back = SourceProfile.FromJson(on.ToJson());
		Assert.Equal((SourceSplitMode.DelimitedColumn, "Buildings", "|"), (back.SplitMode, back.SplitColumn, back.SplitDelimiter));
		SourceProfile bad = SourceProfile.FromJson("{\"SplitMode\":\"CountColumn\",\"SplitColumn\":\"X\",\"SplitSuffixField\":\"City\"}", out List<string> badWarnings);
		Assert.Single(badWarnings);
		Assert.Equal(SourceProfile.SplitFieldUnit, bad.EffectiveSplitSuffixField);
	}

	[Fact]
	public void Inspections_EachBuildingGetsItsOwnAssessmentAndStructure()
	{
		using TempDatabase t = new TempDatabase();
		SourceTable file = SourceTable.Read(SourceMatchingTests.Fixture("generic-inspections.csv"));
		List<string> columns = file.Columns.Append("Buildings").ToList();
		SourceTable raw = new SourceTable(file.Path, file.SheetName, columns,
			file.Rows.Select((r, i) => new SourceRow(r.RowNumber, r.Values.Append(i == 0 ? "2" : "").ToList())).ToList());
		SourceProfile p = ShippedProfiles.Get(ShippedProfiles.GenericInspections);
		p.ValueIsBuildingOnly = true;
		p.SplitMode = SourceSplitMode.CountColumn;
		p.SplitColumn = "Buildings";

		SourceProfile plain = ShippedProfiles.Get(ShippedProfiles.GenericInspections);
		plain.ValueIsBuildingOnly = true;
		SourcePreview baseline = Preview(t.Store, file, plain);
		Assert.NotEmpty(baseline.Rows);
		SourcePreview preview = Preview(t.Store, raw, p);
		Assert.Equal(baseline.Rows.Count + 1, preview.Rows.Count);
		List<ImportRow> group = preview.Groups[0].Rows;
		Assert.Equal(new[] { "INS-0001", "INS-0001#2" }, group.Select(r => r.RecordId).ToArray());
		Assert.All(group, r => Assert.NotNull(r.File));
		Assert.NotEqual(group[0].Property.StreetNumber, group[1].Property.StreetNumber);
		Assert.Equal(ImportRowStatus.NewStructure, group[1].Status);       // never matched to the first building's address

		SourceImport.Import(t.Store, preview, "ins.csv");
		SourcePreview again = Preview(t.Store, raw, p);
		Assert.All(again.Rows.Where(r => r.CanImport), r => Assert.Equal(ImportRowStatus.Unchanged, r.Status));
	}
}
