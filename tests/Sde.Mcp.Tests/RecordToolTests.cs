using System.Text.Json;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Improvements;
using Sde.Data;

namespace Sde.Mcp.Tests;

/// <summary>
/// The read tools for what SDE keeps beside an assessment (letters, documents, appeals, improvements, one compliance
/// case) and the agenda, against seeded databases.
/// </summary>
public sealed class RecordToolTests
{
	[Fact]
	public async Task Letters_are_listed_by_assessment_or_structure_with_file_names_only()
	{
		await using McpSession s = await McpSession.StartAsync();
		AssessmentFile ranch = SampleCases.RanchFlood(s.Store);
		AssessmentFile second = s.Store.CreateAssessment(ranch.Property.PropertyID, applyDefaults: false);
		AssessmentFile other = TestData.Structure(s.Store, "9", "Other");
		s.Store.AddLetters(new[]
		{
			new LetterLogRecord { AssessmentID = ranch.Id(), Kind = "Substantial damage", Template = "/Users/staff/Templates/SD letter.docx", FileName = "/Users/staff/Letters/123 River Street.docx", GeneratedOn = new DateTime(2026, 10, 1) },
		});
		s.Store.MarkLetterSent(ranch.Id(), new DateTime(2026, 10, 3));
		s.Store.AddLetters(new[]
		{
			new LetterLogRecord { AssessmentID = second.Id(), Kind = "Compliance options", FileName = "/Users/staff/Letters/options.pdf", GeneratedOn = new DateTime(2026, 10, 9) },
		});

		JsonElement one = await s.CallOkAsync("list_letters", new { assessmentId = ranch.Id().ToString() });
		Assert.Equal(s.Db.Database.FilePath, one.Str("database"));
		Assert.Equal(ranch.Id().ToString(), one.Str("assessmentId"));
		Assert.Equal("123 River Street", one.Str("address"));
		JsonElement letter = Assert.Single(one.List("letters"));
		Assert.Equal("Substantial damage", letter.Str("kind"));
		Assert.Equal("SD letter.docx", letter.Str("template"));
		Assert.Equal("123 River Street.docx", letter.Str("fileName"));
		Assert.Equal("2026-10-01", letter.Str("written"));
		Assert.Equal("2026-10-03", letter.Str("sent"));
		Assert.True(letter.GetProperty("isSent").GetBoolean());
		Assert.Equal(1, one.Int("sent"));

		// By structure: every assessment's letters, newest first. No folder is ever given out.
		JsonElement all = await s.CallOkAsync("list_letters", new { structureId = ranch.Property.PropertyID.ToString() });
		Assert.Null(all.Str("assessmentId"));
		Assert.Equal(2, all.Int("total"));
		Assert.Equal(new[] { "Compliance options", "Substantial damage" }, all.List("letters").Select(l => l.Str("kind")));
		Assert.False(all.List("letters")[0].GetProperty("isSent").GetBoolean());
		Assert.DoesNotContain("/Users/", McpSession.Text(await s.CallAsync("list_letters", new { structureId = ranch.Property.PropertyID.ToString() })));

		// None yet, and IDs that aren't there.
		JsonElement none = await s.CallOkAsync("list_letters", new { assessmentId = other.Id().ToString() });
		Assert.Equal(0, none.Int("total"));
		Assert.Contains("hasn't written", none.Str("note"));
		Assert.Contains("find_structures", await s.CallErrorAsync("list_letters", new { assessmentId = Guid.NewGuid().ToString() }));
		Assert.Contains("find_structures", await s.CallErrorAsync("list_letters", new { structureId = Guid.NewGuid().ToString() }));
		Assert.Contains("structure ID or an assessment ID", await s.CallErrorAsync("list_letters"));
	}

	[Fact]
	public async Task Documents_are_listed_with_elevation_certificate_values_and_no_paths()
	{
		await using McpSession s = await McpSession.StartAsync();
		AssessmentFile ranch = SampleCases.RanchFlood(s.Store);
		AssessmentFile other = TestData.Structure(s.Store, "9", "Other");
		string source = Path.Combine(s.Db.Folder, "EC 123 River.pdf");
		File.WriteAllText(source, "%PDF-1.4 synthetic");
		DocumentRecord ec = s.Store.AddDocument(ranch.Property.PropertyID, source, DocumentKinds.ElevationCertificate, "Elevation certificate 2026", ranch.Id(),
			"{\"C2a\":\"712.4\",\"A5\":\"39.70, -84.20\",\"C2b\":\"\"}");
		s.Store.AddDocument(ranch.Property.PropertyID, source, DocumentKinds.Permit, "Building permit");

		JsonElement list = await s.CallOkAsync("list_documents", new { structureId = ranch.Property.PropertyID.ToString() });
		Assert.Equal(2, list.Int("total"));
		JsonElement cert = list.List("documents").Single(d => d.Str("documentId") == ec.DocumentID.ToString());
		Assert.Equal("Elevation certificate", cert.Str("kind"));
		Assert.Equal("Elevation certificate 2026", cert.Str("title"));
		Assert.Equal("EC 123 River.pdf", cert.Str("fileName"));
		Assert.Equal(ranch.Id().ToString(), cert.Str("assessmentId"));
		Assert.Equal("712.4", cert.GetProperty("fields").GetProperty("C2a").GetString());
		Assert.False(cert.GetProperty("fields").TryGetProperty("C2b", out _));
		Assert.Contains(list.List("documents"), d => d.Str("kind") == "Permit");

		// An assessment's ID gives its structure's documents. Neither the documents folder nor the stored names appear.
		string text = McpSession.Text(await s.CallAsync("list_documents", new { assessmentId = ranch.Id().ToString() }));
		Assert.DoesNotContain(s.Db.Database.DocumentsFolder, text);
		Assert.DoesNotContain(ec.FileName, text);
		Assert.DoesNotContain("synthetic", text);

		JsonElement none = await s.CallOkAsync("list_documents", new { structureId = other.Property.PropertyID.ToString() });
		Assert.Equal(0, none.Int("total"));
		Assert.Contains("No documents", none.Str("note"));
		Assert.Contains("find_structures", await s.CallErrorAsync("list_documents", new { structureId = Guid.NewGuid().ToString() }));
		Assert.Contains("isn't a structure ID", await s.CallErrorAsync("list_documents", new { structureId = "123 River Street" }));
	}

	[Fact]
	public async Task An_appeal_is_read_with_its_bids()
	{
		await using McpSession s = await McpSession.StartAsync();
		AssessmentFile ranch = SampleCases.RanchFlood(s.Store);
		AssessmentFile other = TestData.Structure(s.Store, "9", "Other");
		s.Store.SaveAppeal(new AppealRecord { AssessmentID = ranch.Id(), ReceivedOn = new DateTime(2026, 10, 2), Basis = "Repair estimate too high" });
		s.Store.SaveBid(new ContractorBidRecord { AssessmentID = ranch.Id(), Contractor = "Sample Builders", Amount = 41000m, BidDate = new DateTime(2026, 10, 1) });
		s.Store.SaveBid(new ContractorBidRecord { AssessmentID = ranch.Id(), Contractor = "Example Repairs", Amount = 52000m, BidDate = new DateTime(2026, 10, 4), Notes = "Includes the roof" });
		s.Store.Apply(ranch.Id(), f =>
		{
			f.Assessment.CostOfRepairsID = LookupIds.RepairContractorEstimate;
			f.Assessment.ContractorEstimate = 41000m;
		});

		JsonElement appeal = await s.CallOkAsync("get_appeal", new { assessmentId = ranch.Id().ToString() });
		Assert.True(appeal.GetProperty("hasAppeal").GetBoolean());
		Assert.Equal("2026-10-02", appeal.Str("received"));
		Assert.Equal("Repair estimate too high", appeal.Str("basis"));
		Assert.Equal("Pending", appeal.Str("outcome"));
		Assert.NotNull(appeal.GetProperty("result").Str("result"));
		List<JsonElement> bids = appeal.List("bids");
		Assert.Equal(new[] { "Sample Builders", "Example Repairs" }, bids.Select(b => b.Str("contractor")));
		Assert.True(bids[0].GetProperty("inUse").GetBoolean());
		Assert.False(bids[1].GetProperty("inUse").GetBoolean());
		Assert.Equal(52000m, bids[1].Dec("amount"));
		Assert.Equal("Includes the roof", bids[1].Str("notes"));

		JsonElement none = await s.CallOkAsync("get_appeal", new { assessmentId = other.Id().ToString() });
		Assert.False(none.GetProperty("hasAppeal").GetBoolean());
		Assert.Empty(none.List("bids"));
		Assert.Contains("No appeal", none.Str("note"));
		Assert.Contains("find_structures", await s.CallErrorAsync("get_appeal", new { assessmentId = Guid.NewGuid().ToString() }));
	}

	[Fact]
	public async Task Improvements_show_the_cumulative_figures_the_improvements_sheet_works_out()
	{
		await using McpSession s = await McpSession.StartAsync();
		DateTime today = new DateTime(2026, 10, 20);
		s.Context.Reader.Clock = () => today;
		AssessmentFile ranch = SampleCases.RanchFlood(s.Store);
		AssessmentFile other = TestData.Structure(s.Store, "9", "Other");
		Guid pid = ranch.Property.PropertyID;
		s.Store.SetSiSettings(new SiSettings { LookbackYears = 10 });
		ImprovementProjectRecord earlier = s.Store.SaveImprovementProject(new ImprovementProjectRecord { PropertyID = pid, PermitNumber = "BP-0001", Description = "Kitchen", ProjectDate = new DateTime(2022, 5, 1), Cost = 20000m });
		ImprovementProjectRecord current = s.Store.SaveImprovementProject(new ImprovementProjectRecord { PropertyID = pid, PermitNumber = "BP-0002", Description = "Second-story addition", ProjectDate = new DateTime(2026, 6, 1), Cost = 30000m });
		s.Store.SaveImprovementProject(new ImprovementProjectRecord { PropertyID = pid, Description = "Old porch", ProjectDate = new DateTime(2010, 1, 1), Cost = 5000m });
		SIDeterminationRecord saved = s.Store.SaveSiDetermination(new SIDeterminationRecord { PropertyID = pid, CumulativeCost = 50000m, Percent = 40m, ThresholdPercent = 50m, Result = SiResult.NotSubstantialImprovement, DeterminedBy = "Staff" });
		ComplianceCaseRecord siCase = s.Store.OpenComplianceCaseForImprovement(saved.DeterminationID);

		JsonElement list = await s.CallOkAsync("list_improvements", new { structureId = pid.ToString() });
		decimal marketValue = Math.Round(CumulativeDamage.BuildingValue(s.Store.GetAssessmentsForProperty(pid).First()).Value, 2);
		Assert.Equal(marketValue, list.Dec("marketValue"));
		Assert.Equal(10, list.GetProperty("rules").Int("lookbackYears"));
		Assert.Equal(3, list.List("projects").Count);

		// The same figures as SDE's own calculation, with every other project as the earlier ones.
		JsonElement addition = list.List("projects").Single(p => p.Str("projectId") == current.ProjectID.ToString());
		List<ImprovementProjectRecord> projects = s.Store.GetImprovementProjects(pid);
		SiResult expected = ImprovementCalculator.Evaluate(projects.Single(p => p.ProjectID == current.ProjectID),
			projects.Where(p => p.ProjectID != current.ProjectID).ToList(), marketValue, s.Store.GetSiSettings(), today);
		JsonElement figures = addition.GetProperty("figures");
		Assert.Equal(50000m, figures.Dec("costCounted"));
		Assert.Equal(expected.CostCounted, figures.Dec("costCounted"));
		Assert.Equal(expected.Percent, figures.Dec("percent"));
		Assert.Equal(expected.ResultText, figures.Str("result"));
		Assert.Equal("2016-06-01", figures.Str("lookbackFrom"));
		Assert.Contains(figures.List("lines"), l => l.Str("projectId") == earlier.ProjectID.ToString() && l.GetProperty("counted").GetBoolean());
		Assert.Contains(figures.List("lines"), l => l.Str("project") == "Old porch" && !l.GetProperty("counted").GetBoolean() && l.Str("reason").Contains("look-back"));
		Assert.Equal(expected.Explanation, figures.List("explanation").Select(x => x.GetString()));

		JsonElement determination = Assert.Single(list.List("determinations"));
		Assert.Equal(SiResult.NotSubstantialImprovement, determination.Str("result"));
		Assert.Equal(50000m, determination.Dec("cumulativeCost"));
		Assert.Equal(siCase.CaseID.ToString(), determination.Str("complianceCaseId"));

		JsonElement none = await s.CallOkAsync("list_improvements", new { assessmentId = other.Id().ToString() });
		Assert.Empty(none.List("projects"));
		Assert.Empty(none.List("determinations"));
		Assert.Contains("No improvement projects", none.Str("note"));
		Assert.Contains("find_structures", await s.CallErrorAsync("list_improvements", new { structureId = Guid.NewGuid().ToString() }));
	}

	[Fact]
	public async Task A_compliance_case_is_read_with_its_timeline_newest_first()
	{
		await using McpSession s = await McpSession.StartAsync();
		DateTime today = new DateTime(2026, 10, 20);
		s.Context.Reader.Clock = () => today;
		AssessmentFile ranch = SampleCases.RanchFlood(s.Store);
		ComplianceCaseRecord c = s.Store.OpenComplianceCase(ranch.Id());
		c.Path = CompliancePaths.Elevate;
		c.Deadline = today.AddDays(-2);
		s.Store.SaveComplianceCase(c);
		s.Store.AddComplianceEntry(c.CaseID, "Call", "Owner called about the ICC claim.");

		JsonElement read = await s.CallOkAsync("get_compliance_case", new { caseId = c.CaseID.ToString() });
		JsonElement item = read.GetProperty("case");
		Assert.Equal(c.CaseID.ToString(), item.Str("caseId"));
		Assert.Equal("Elevate", item.Str("path"));
		Assert.True(item.GetProperty("overdue").GetBoolean());
		Assert.Equal(2, item.Int("daysOverdue"));
		Assert.Equal("Pat Example", item.Str("owner"));
		Assert.Equal(4, read.Int("total"));
		List<JsonElement> timeline = read.List("timeline");
		Assert.Equal("Call", timeline[0].Str("kind"));
		Assert.Equal("Owner called about the ICC claim.", timeline[0].Str("text"));
		Assert.Equal("Case opened for the substantial damage determination", timeline[^1].Str("text"));

		JsonElement paged = await s.CallOkAsync("get_compliance_case", new { caseId = c.CaseID.ToString(), pageSize = 1, page = 2 });
		Assert.Single(paged.List("timeline"));
		Assert.Contains("page = 3", paged.Str("next"));
		Assert.Contains("list_compliance_cases", await s.CallErrorAsync("get_compliance_case", new { caseId = Guid.NewGuid().ToString() }));
		Assert.Contains("isn't a compliance case ID", await s.CallErrorAsync("get_compliance_case", new { caseId = "case 1" }));
	}

	[Fact]
	public async Task The_agenda_lists_dated_items_and_overdue_deadlines()
	{
		await using McpSession s = await McpSession.StartAsync();
		DateTime today = new DateTime(2026, 10, 20);
		s.Context.Reader.Clock = () => today;
		AssessmentFile late = SampleCases.RanchFlood(s.Store);
		AssessmentFile soon = SampleCases.RanchFlood(s.Store);
		AssessmentFile elsewhere = TestData.Structure(s.Store, "9", "Other");
		EventRecord flood = TestData.Event(s.Store);
		s.Store.SetEvent(new[] { late.Id(), soon.Id() }, flood.EventID);
		ComplianceCaseRecord overdue = s.Store.OpenComplianceCase(late.Id());
		overdue.Deadline = today.AddDays(-5);
		s.Store.SaveComplianceCase(overdue);
		ComplianceCaseRecord upcoming = s.Store.OpenComplianceCase(soon.Id());
		upcoming.Deadline = today.AddDays(10);
		upcoming.FinalInspectionOn = today.AddDays(3);
		s.Store.SaveComplianceCase(upcoming);
		s.Store.SaveAppeal(new AppealRecord { AssessmentID = elsewhere.Id(), ReceivedOn = today.AddDays(1), Basis = "Value too low" });
		s.Store.AddLetters(new[] { new LetterLogRecord { AssessmentID = elsewhere.Id(), Kind = "Not substantially damaged", FileName = "/Users/staff/letter.docx" } });
		s.Store.MarkLetterSent(elsewhere.Id(), today.AddDays(2));

		// Today and the next 30 days, with the deadline that has already passed.
		JsonElement agenda = await s.CallOkAsync("agenda");
		Assert.Equal("2026-10-20", agenda.Str("from"));
		Assert.Equal("2026-11-19", agenda.Str("to"));
		Assert.Equal(new[] { "ComplianceDeadline", "AppealReceived", "LetterSent", "FinalInspection", "ComplianceDeadline" }, agenda.List("items").Select(i => i.Str("kind")));
		JsonElement first = agenda.List("items")[0];
		Assert.True(first.GetProperty("overdue").GetBoolean());
		Assert.Equal(5, first.Int("daysOverdue"));
		Assert.Equal(overdue.CaseID.ToString(), first.Str("caseId"));
		Assert.Equal("123 River Street", first.Str("address"));
		Assert.Equal(1, agenda.Int("overdue"));
		Assert.Contains("overdue", agenda.Str("note"));
		Assert.Equal(elsewhere.Id().ToString(), agenda.List("items")[1].Str("assessmentId"));
		Assert.Contains("Not substantially damaged", agenda.List("items")[2].Str("title"));

		// Overdue only; one event; a window of past dates; paging.
		JsonElement overdueOnly = await s.CallOkAsync("agenda", new { overdueOnly = true });
		Assert.Equal(overdue.CaseID.ToString(), Assert.Single(overdueOnly.List("items")).Str("caseId"));
		JsonElement inEvent = await s.CallOkAsync("agenda", new { @event = "September flood" });
		Assert.Equal(3, inEvent.Int("total"));
		Assert.DoesNotContain(inEvent.List("items"), i => i.Str("assessmentId") == elsewhere.Id().ToString());
		JsonElement september = await s.CallOkAsync("agenda", new { from = "2026-09-01", to = "2026-09-30" });
		JsonElement start = Assert.Single(september.List("items"));
		Assert.Equal("EventStart", start.Str("kind"));
		Assert.Equal(flood.EventID.ToString(), start.Str("eventId"));
		Assert.Equal("2026-09-14", start.Str("date"));
		JsonElement paged = await s.CallOkAsync("agenda", new { pageSize = 2 });
		Assert.Equal(2, paged.List("items").Count);
		Assert.Contains("page = 2", paged.Str("next"));

		// Nothing dated in the window.
		JsonElement empty = await s.CallOkAsync("agenda", new { from = "2025-01-01", to = "2025-01-31" });
		Assert.Equal(0, empty.Int("total"));
		Assert.Contains("Nothing is dated", empty.Str("note"));
		Assert.Contains("yyyy-MM-dd", await s.CallErrorAsync("agenda", new { from = "next week" }));
		Assert.Contains("before from", await s.CallErrorAsync("agenda", new { from = "2026-10-20", to = "2026-10-01" }));
		Assert.Contains("list_events", await s.CallErrorAsync("agenda", new { @event = "Winter flood" }));
	}

	[Fact]
	public async Task The_agenda_of_an_empty_database_is_empty()
	{
		await using McpSession s = await McpSession.StartAsync();
		JsonElement agenda = await s.CallOkAsync("agenda");
		Assert.Equal(0, agenda.Int("total"));
		Assert.Empty(agenda.List("items"));
		Assert.Contains("no open compliance case is past its deadline", agenda.Str("note"));
	}
}
