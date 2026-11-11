using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Reports.Tests;

/// <summary>The case file PDF (one structure across every event), read back with pdftotext.</summary>
public class CaseFileReportTests : IDisposable
{
	private static readonly DateTime At = new DateTime(2026, 10, 20, 9, 15, 0);
	private readonly TempDatabase _db = new TempDatabase();

	public void Dispose() => _db.Dispose();

	private SdeStore Store => _db.Store;

	private string Pdf(string name) => Path.Combine(_db.Folder, name);

	private string Render(CaseFile cf, string name, CaseFileReportOptions options = null)
	{
		string path = Pdf(name);
		CaseFileReport.RenderToFile(cf, Store.Reference, path, options ?? new CaseFileReportOptions { GeneratedOn = At });
		return path;
	}

	[Fact]
	public void AFullCase_PrintsEverySection()
	{
		CaseFileSample s = CaseFileSample.Create(Store, Path.Combine(_db.Folder, "source"));
		string path = Render(Store.GetCaseFile(s.PropertyId), "full.pdf");

		string text = Poppler.Text(path);
		Assert.Contains("Substantial Damage Estimator", text);
		Assert.Contains(CaseFileReport.Title, text);
		Assert.Contains("123 River Street", text);
		Assert.Contains("Example, Pat", text);
		Assert.Contains("092-0004-0118-00", text);
		Assert.Contains("Current Result", text);
		Assert.Contains("75.0%", text);
		Assert.Contains("Substantially Damaged", text);
		foreach (string section in new[]
		{
			CaseFileReport.AssessmentsTitle, CaseFileReport.ComplianceTitle, CaseFileReport.LettersTitle, CaseFileReport.AppealsTitle,
			CaseFileReport.ImprovementsTitle, CaseFileReport.DocumentsTitle, CaseFileReport.TimelineTitle,
		})
		{
			Assert.Contains(section, text);
		}
		Assert.Contains("September flood (DR-9999)", text);
		Assert.Contains("Spring flood", text);
		Assert.Contains("In Recently deleted", text);
		Assert.Contains("Inspector C", text);
		Assert.Contains("Elevate", text);
		Assert.Contains("BP-0101", text);
		Assert.Contains("Substantial damage", text);
		Assert.Contains("Repair estimate too high", text);
		Assert.Contains("Example Builders", text);
		Assert.Contains("$61,000.00", text);
		Assert.Contains("Second-story addition", text);
		Assert.Contains("Not Substantial Improvement", text);
		Assert.Contains("Elevation certificate 2026", text);
		Assert.Contains("Owner called about the ICC claim.", text);
		Assert.Matches(@"Page 1 of \d", text);
		Assert.InRange(Poppler.Pages(path), 1, 4);
	}

	[Fact]
	public void TheOutput_HasNoFilePaths_AndNothingFromOtherStructures()
	{
		CaseFileSample s = CaseFileSample.Create(Store, Path.Combine(_db.Folder, "source"));
		AssessmentFile neighbour = Fixtures.Office(Store);
		string path = Render(Store.GetCaseFile(s.PropertyId), "private.pdf", new CaseFileReportOptions { GeneratedOn = At, IncludeEdits = true });

		string text = Poppler.Text(path);
		Assert.DoesNotContain(_db.Folder, text);
		Assert.DoesNotContain("/tmp", text);
		Assert.DoesNotContain(".docx", text);
		Assert.DoesNotContain(s.Document.FileName, text);
		Assert.DoesNotContain(new AssessmentViewProbe(neighbour, Store).Street, text);
	}

	[Fact]
	public void EmptySections_AreLeftOut()
	{
		AssessmentFile ranch = Fixtures.Ranch(Store);
		string path = Render(Store.GetCaseFile(ranch.Property.PropertyID), "small.pdf");

		string text = Poppler.Text(path);
		Assert.Contains(CaseFileReport.AssessmentsTitle, text);
		Assert.Contains(CaseFileReport.TimelineTitle, text);
		foreach (string section in new[] { CaseFileReport.ComplianceTitle, CaseFileReport.LettersTitle, CaseFileReport.AppealsTitle, CaseFileReport.ImprovementsTitle, CaseFileReport.DocumentsTitle })
		{
			Assert.DoesNotContain(section, text);
		}
		Assert.Equal(1, Poppler.Pages(path));
	}

	[Fact]
	public void AStructureWithNoAssessment_StillPrints()
	{
		PropertyRecord p = Store.SaveProperty(new PropertyRecord { StreetNumber = "7", StreetName = "Sample", City = "Exampleville", StateID = 36, StructureTypeID = 1 });
		string path = Render(Store.GetCaseFile(p.PropertyID), "none.pdf", new CaseFileReportOptions { GeneratedOn = At, IncludeTimeline = false });

		string text = Poppler.Text(path);
		Assert.Contains("No assessment", text);
		Assert.Contains("0 assessments", text);
		Assert.DoesNotContain("% Damaged", text);   // the Assessments table's header
		Assert.DoesNotContain(CaseFileReport.TimelineTitle, text);
		Assert.Equal(1, Poppler.Pages(path));
	}

	[Fact]
	public void Edits_AreLeftOutUnlessAskedFor()
	{
		CaseFileSample s = CaseFileSample.Create(Store, Path.Combine(_db.Folder, "source"));
		CaseFile cf = Store.GetCaseFile(s.PropertyId);

		string without = Poppler.Text(Render(cf, "without.pdf"));
		Assert.DoesNotContain("From (blank) to 092-0004-0118-00", without);
		Assert.Contains("not listed here", without);
		string with = Poppler.Text(Render(cf, "with.pdf", new CaseFileReportOptions { GeneratedOn = At, IncludeEdits = true }));
		Assert.Contains("From (blank) to 092-0004-0118-00", with);
		Assert.DoesNotContain("not listed here", with);
		string noTimeline = Poppler.Text(Render(cf, "no-timeline.pdf", new CaseFileReportOptions { GeneratedOn = At, IncludeTimeline = false }));
		Assert.DoesNotContain(CaseFileReport.TimelineTitle, noTimeline);
		Assert.DoesNotContain("Owner called about the ICC claim.", noTimeline);
	}

	[Fact]
	public void ALargeCase_RunsOverSeveralNumberedPages()
	{
		CaseFileSample s = CaseFileSample.Create(Store, Path.Combine(_db.Folder, "source"));
		for (int i = 0; i < 40; i++)
		{
			AssessmentFile f = Store.CreateAssessment(s.PropertyId, applyDefaults: false);
			f.Assessment.InspectorName = "Inspector " + i;
			f.Assessment.DateofInspection = new DateTime(2020, 1, 1).AddDays(i * 30);
			Store.SaveAssessment(f);
			Store.AddReviewComment(f.Assessment.AssessmentID, $"Comment number {i} about this inspection.");
		}
		string path = Render(Store.GetCaseFile(s.PropertyId), "large.pdf");

		int pages = Poppler.Pages(path);
		Assert.InRange(pages, 3, 12);
		Assert.Contains($"Page {pages} of {pages}", Poppler.Text(path, pages));
		Assert.Contains("Inspector 39", Poppler.Text(path));
	}

	/// <summary>A neighbour's street, as the reports print it.</summary>
	private sealed class AssessmentViewProbe(AssessmentFile f, SdeStore store)
	{
		public string Street => new Internal.AssessmentView(f, store.Reference).StreetAddress;
	}
}
