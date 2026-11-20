using Sde.Core.Assessments;
using Sde.Core.Records;

namespace Sde.Tests.Data;

public class StoreTests
{
	[Fact]
	public void RanchFlood_CalculatesHandCheckedResult()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		AssessmentRecord a = t.Store.LoadAssessment(f.Assessment.AssessmentID).Assessment;
		// 118.75 × 1450 × 0.93 = 160,134.375; + garage 240 × 32.50 + shed 2,500 = 170,434.375
		Assert.Equal(170434.38m, a.TotalReplacementCost);
		Assert.Equal(10300m, a.TotalAdjustments);
		Assert.Equal(24.2m, a.DepreciationValue);
		Assert.Equal(129189.26m, a.ComputedActualCashValue);   // 170,434.375 × 0.758
		Assert.Equal(96900.46m, a.ComputedDamages);
		Assert.Equal(75.0m, a.PercentDamage);
		Assert.Equal("Substantially Damaged", a.DamageMessage);
	}

	/// <summary>
	/// Regression: SQLite NUMERIC columns stored 75.0 as INTEGER and 49.9 as REAL, and Dapper (which compiles one
	/// conversion per query) crashed reading a list that mixed them. Lists must read any mix of whole/fractional values.
	/// </summary>
	[Fact]
	public void Lists_ReadMixedWholeAndFractionalNumbers()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile whole = SampleCases.RanchFlood(t.Store);                 // 75.0 %
		AssessmentFile fractional = SampleCases.RanchFlood(t.Store);
		fractional.ElementPercentages[3].UserDamage = 33.3m;
		fractional.Assessment.BaseCost = 101.37m;
		t.Store.SaveAssessment(fractional);
		AssessmentFile third = SampleCases.RanchFlood(t.Store);
		third.Property.SquareFootage = 1000m;
		t.Store.SaveAssessment(third);

		Assert.Equal(3, t.Store.ListStructures().Count);
		Assert.Equal(3, t.Store.LoadAllAssessments().Count);
		Assert.Equal(3, new Sde.Data.ExchangeService(t.Store).ReadDataset().Assessments.Count);
		Assert.Contains(t.Store.ListStructures(), s => s.PercentDamage == 75.0m);
		Assert.Contains(t.Store.ListStructures(), s => s.PercentDamage != 75.0m);
	}

	[Fact]
	public void SaveAndLoad_PreservesEverything()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile saved = SampleCases.RanchFlood(t.Store);
		AssessmentFile loaded = t.Store.LoadAssessment(saved.Assessment.AssessmentID);
		AssertSameRecord(saved.Property, loaded.Property, "LastUpdate", "ImportDate");
		AssertSameRecord(saved.Assessment, loaded.Assessment, "LastUpdate");
		Assert.Equal(12, loaded.ElementPercentages.Count);
		Assert.Equal(7, loaded.AdditionalAdjustments.Count);
		foreach (ElementPercentageRecord e in saved.ElementPercentages)
		{
			AssertSameRecord(e, loaded.ElementPercentages.Single(x => x.ElementID == e.ElementID));
		}
	}

	[Fact]
	public void ManufacturedHome_ShowsSkirtingInsteadOfFoundation()
	{
		using TempDatabase t = new TempDatabase();
		PropertyRecord p = t.Store.SaveProperty(new PropertyRecord { StructureTypeID = 1, ResidenceTypeID = 94, StreetName = "Lot 4" });
		AssessmentFile f = t.Store.CreateAssessment(p.PropertyID, false);
		Assert.Contains(f.ElementPercentages, e => e.CalcID == 16 && e.ElementName == "Skirting / Forms Piers");
		Assert.DoesNotContain(f.ElementPercentages, e => e.CalcID == 4);
	}

	[Fact]
	public void NonResidential_HasSevenElements()
	{
		using TempDatabase t = new TempDatabase();
		PropertyRecord p = t.Store.SaveProperty(new PropertyRecord { StructureTypeID = 2, StoryTypeID = 101, StructureUseID = 106 });
		AssessmentFile f = t.Store.CreateAssessment(p.PropertyID, false);
		Assert.Equal(new[] { "Foundation", "Superstructure", "Roof Covering", "Plumbing", "Electrical", "HVAC", "Interiors" },
			f.ElementPercentages.Select(e => e.ElementName));
	}

	[Fact]
	public void DeleteProperty_RemovesEverything()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		string photo = Path.Combine(t.Folder, "photo.jpg");
		File.WriteAllBytes(photo, new byte[] { 1, 2, 3 });
		AttachmentRecord att = t.Store.ImportPhoto(f.Assessment.AssessmentID, photo, "front");
		f.Attachments.Add(att);
		t.Store.SaveAssessment(f);
		Assert.True(File.Exists(t.Store.PhotoPath(att)));

		t.Store.DeleteProperty(f.Property.PropertyID);
		Assert.Null(t.Store.LoadAssessment(f.Assessment.AssessmentID));
		Assert.Null(t.Store.GetProperty(f.Property.PropertyID));
		Assert.False(File.Exists(t.Store.PhotoPath(att)));
		Assert.Equal((0, 0), t.Store.Counts());
	}

	[Fact]
	public void Shapes_SetSquareFootage()
	{
		using TempDatabase t = new TempDatabase();
		PropertyRecord p = t.Store.SaveProperty(new PropertyRecord { StructureTypeID = 1 });
		decimal total = t.Store.SaveShapes(p.PropertyID, new[]
		{
			new ShapeRecord { ShapeType = "r", LengthA = 40, WidthA = 30, StoriesA = 1, TotalSqFt = 1200 },
			new ShapeRecord { ShapeType = "r", LengthA = 10, WidthA = 12, StoriesA = 2, TotalSqFt = 240 },
		});
		Assert.Equal(1440m, total);
		Assert.Equal(1440m, t.Store.GetProperty(p.PropertyID).SquareFootage);
		Assert.Equal(2, t.Store.GetShapes(p.PropertyID).Count);
	}

	/// <summary>Loading many assessments at once gives exactly what loading each one alone gives, rows in the same order.</summary>
	[Fact]
	public void LoadAssessments_MatchesLoadingEachAlone()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile first = SampleCases.RanchFlood(t.Store);
		AssessmentFile second = SampleCases.RanchFlood(t.Store);
		// A second assessment of the first structure, so two files share a structure.
		AssessmentFile again = t.Store.CreateAssessment(first.Property.PropertyID, applyDefaults: false);
		t.Store.SaveAssessment(again);
		foreach (AssessmentFile f in new[] { first, second })
		{
			f.Attachments.Add(new AttachmentRecord { FileName = f.Assessment.AssessmentID + "-b.jpg", Active = true });
			f.Attachments.Add(new AttachmentRecord { FileName = f.Assessment.AssessmentID + "-a.jpg", Active = true, DefaultImage = true });
			t.Store.SaveAssessment(f);
		}
		// Custom-field values with tied zOrder, saved out of ID order.
		List<CustomFieldRecord> fields = t.Store.Reference.CustomFields.Take(3).ToList();
		if (fields.Count > 0)
		{
			t.Store.SaveCustomFieldResults(second.Assessment.AssessmentID, "assessment",
				fields.AsEnumerable().Reverse().Select(cf => new CustomFieldResultRecord { CustomFieldID = cf.CustomFieldID, Result = cf.CustomField, zOrder = 1 }).ToList());
		}

		List<Guid> ids = new() { second.Assessment.AssessmentID, Guid.NewGuid(), first.Assessment.AssessmentID, again.Assessment.AssessmentID, second.Assessment.AssessmentID };
		List<AssessmentFile> bulk = t.Store.LoadAssessments(ids);
		List<AssessmentFile> single = ids.Select(t.Store.LoadAssessment).Where(f => f != null).ToList();

		Assert.Equal(single.Count, bulk.Count);
		for (int i = 0; i < single.Count; i++)
		{
			AssertSameRecord(single[i].Assessment, bulk[i].Assessment);
			AssertSameRecord(single[i].Property, bulk[i].Property);
			AssertSameRows(single[i].CostAdjustments, bulk[i].CostAdjustments);
			AssertSameRows(single[i].AdditionalAdjustments, bulk[i].AdditionalAdjustments);
			AssertSameRows(single[i].ElementPercentages, bulk[i].ElementPercentages);
			AssertSameRows(single[i].Attachments, bulk[i].Attachments);
			AssertSameRows(single[i].CustomFields, bulk[i].CustomFields);
		}
		// Each file has records of its own, even for a shared structure or an ID asked for twice.
		Assert.NotSame(bulk[1].Property, bulk[2].Property);
		Assert.NotSame(bulk[0].Assessment, bulk[3].Assessment);
		Assert.NotSame(bulk[0].ElementPercentages, bulk[3].ElementPercentages);
		Assert.Equal(single.Count, t.Store.LoadAllAssessments().Count + 1);
	}

	private static void AssertSameRows<T>(List<T> expected, List<T> actual)
	{
		Assert.Equal(expected.Count, actual.Count);
		for (int i = 0; i < expected.Count; i++)
		{
			AssertSameRecord(expected[i], actual[i]);
		}
	}

	internal static void AssertSameRecord<T>(T expected, T actual, params string[] ignore)
	{
		foreach (var prop in typeof(T).GetProperties().Where(p => !ignore.Contains(p.Name)))
		{
			object e = prop.GetValue(expected);
			object a = prop.GetValue(actual);
			Assert.True(Equals(e, a), $"{typeof(T).Name}.{prop.Name}: expected {e ?? "null"}, got {a ?? "null"}");
		}
	}

	[Fact]
	public void ImportingAPhotoNamedLikeAnotherAssessmentsPhoto_NeverReplacesIt()
	{
		using TempDatabase office = new TempDatabase();
		AssessmentFile mine = SampleCases.RanchFlood(office.Store);
		Directory.CreateDirectory(office.Store.AttachmentsFolder);
		File.WriteAllText(Path.Combine(office.Store.AttachmentsFolder, "front.jpg"), "office photo");
		mine.Attachments.Add(new AttachmentRecord { FileName = "front.jpg", Active = true, AttachmentType = "Photo" });
		office.Store.SaveAssessment(mine);
		using TempDatabase other = new TempDatabase();
		AssessmentFile theirs = SampleCases.RanchFlood(other.Store);
		Directory.CreateDirectory(other.Store.AttachmentsFolder);
		File.WriteAllText(Path.Combine(other.Store.AttachmentsFolder, "FRONT.jpg"), "imported photo");
		theirs.Attachments.Add(new AttachmentRecord { FileName = "FRONT.jpg", Active = true, AttachmentType = "Photo" });
		other.Store.SaveAssessment(theirs);

		new Sde.Data.ExchangeService(office.Store).Import(new Sde.Data.ExchangeService(other.Store).ReadDataset(), other.Store.AttachmentsFolder, Sde.Data.ExistingRecordAction.Skip);

		Assert.Equal("office photo", File.ReadAllText(Path.Combine(office.Store.AttachmentsFolder, "front.jpg")));
		AttachmentRecord imported = Assert.Single(office.Store.LoadAssessment(theirs.Assessment.AssessmentID).Attachments);
		Assert.NotEqual("front.jpg", imported.FileName, StringComparer.OrdinalIgnoreCase);
		Assert.Equal("imported photo", File.ReadAllText(office.Store.PhotoPath(imported)));
	}

	[Fact]
	public void Export_CopiesPhotosOnlyFromTheAttachmentsFolder()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		Directory.CreateDirectory(t.Store.AttachmentsFolder);
		string outside = Path.Combine(Path.GetDirectoryName(t.Store.AttachmentsFolder)!, "outside.txt");
		File.WriteAllText(outside, "not a photo");
		// As a database made elsewhere could store them: a name that climbs out, and a full path.
		f.Attachments.Add(new AttachmentRecord { FileName = "../outside.txt", Active = true, AttachmentType = "Photo" });
		f.Attachments.Add(new AttachmentRecord { FileName = outside, Active = true, AttachmentType = "Photo" });
		t.Store.SaveAssessment(f);
		string parent = Directory.CreateDirectory(Path.Combine(t.Folder, "export")).FullName;

		(string folder, IReadOnlyList<string> missing) = new Sde.Data.ExchangeService(t.Store).Export(new Sde.Data.ExchangeService(t.Store).ReadDataset(), parent);

		Assert.Empty(Directory.GetFiles(Path.Combine(folder, "photos")));
		Assert.Equal(2, missing.Count);
	}

	[Fact]
	public void Import_NeverFollowsALinkInTheImportFolder()
	{
		using TempDatabase office = new TempDatabase();
		using TempDatabase other = new TempDatabase();
		AssessmentFile theirs = SampleCases.RanchFlood(other.Store);
		Directory.CreateDirectory(other.Store.AttachmentsFolder);
		string secret = Path.Combine(other.Folder, "secret.txt");
		File.WriteAllText(secret, "private");
		File.CreateSymbolicLink(Path.Combine(other.Store.AttachmentsFolder, "p.jpg"), secret);
		theirs.Attachments.Add(new AttachmentRecord { FileName = "p.jpg", Active = true, AttachmentType = "Photo" });
		other.Store.SaveAssessment(theirs);

		Sde.Data.ImportResult result = new Sde.Data.ExchangeService(office.Store).Import(new Sde.Data.ExchangeService(other.Store).ReadDataset(), other.Store.AttachmentsFolder, Sde.Data.ExistingRecordAction.Skip);

		Assert.Contains(result.Warnings, w => w.Contains("links aren't followed"));
		Assert.False(File.Exists(Path.Combine(office.Store.AttachmentsFolder, "p.jpg")));
	}

	[Fact]
	public void ANumberStoredNearDecimalsLimit_StillReadsBack()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile f = SampleCases.RanchFlood(t.Store);
		// What an import can write: REAL keeps it as a double just past decimal's range.
		t.Store.Database.InTransaction((c, tx) => Dapper.SqlMapper.Execute(c, "UPDATE Property SET FirstFloorElevation = 7.9228162514264337E28 WHERE PropertyID = @id",
			new { id = f.Property.PropertyID.ToString("D") }, tx));

		Assert.Equal(decimal.MaxValue, t.Store.GetProperty(f.Property.PropertyID).FirstFloorElevation);
		Assert.NotEmpty(t.Store.ListMapPoints());
		Assert.NotNull(t.Store.LoadAssessment(f.Assessment.AssessmentID));
	}
}
