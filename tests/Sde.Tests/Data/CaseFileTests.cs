using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Tests.Data;

/// <summary>The case file: one structure across every event, as one timeline and grouped sections (v3.5).</summary>
public class CaseFileTests
{
	[Fact]
	public void TheHeader_NamesTheStructure_AndItsCurrentResult()
	{
		using TempDatabase t = new TempDatabase();
		CaseFileSample s = CaseFileSample.Create(t.Store, Path.Combine(t.Folder, "source"));

		CaseFile cf = t.Store.GetCaseFile(s.PropertyId);
		Assert.Equal("123 River Street", cf.Address);
		Assert.Equal("Pat Example", cf.Owner);
		Assert.Equal("092-0004-0118-00", cf.ParcelNumber);
		Assert.Equal(("399990", "Exampleville, City of"), (cf.CommunityId, cf.CommunityName));
		Assert.StartsWith("Exampleville, ", cf.CityStateZip);
		Assert.EndsWith("43702", cf.CityStateZip);
		Assert.Equal(s.Current.Assessment.AssessmentID, cf.Latest.Assessment.AssessmentID);
		Assert.Equal(75.0m, cf.Latest.Assessment.PercentDamage);
		Assert.Equal("Substantially Damaged", cf.Latest.Assessment.DamageMessage);
		Assert.Equal(AssessmentStatus.LetterSent, cf.Latest.Status.Value);
		Assert.Equal(s.Flood.EventID, cf.Latest.Event.EventID);
		Assert.Null(cf.Cumulative);
	}

	[Fact]
	public void TheGroupedSections_HoldEveryRecord()
	{
		using TempDatabase t = new TempDatabase();
		CaseFileSample s = CaseFileSample.Create(t.Store, Path.Combine(t.Folder, "source"));

		CaseFile cf = t.Store.GetCaseFile(s.PropertyId);
		Assert.Equal(new[] { s.Trashed.Assessment.AssessmentID, s.Current.Assessment.AssessmentID, s.Earlier.Assessment.AssessmentID },
			cf.Assessments.Select(a => a.Assessment.AssessmentID));
		Assert.Equal(new[] { true, false, false }, cf.Assessments.Select(a => a.IsTrashed));
		Assert.Equal(1, cf.PhotoCounts[s.Current.Assessment.AssessmentID]);
		Assert.Equal(1, cf.Assessments[1].PhotoCount);
		Assert.Equal(s.EarlierFlood.EventID, cf.Assessments[2].Event.EventID);
		Assert.Equal(s.Case.CaseID, Assert.Single(cf.ComplianceCases).Case.CaseID);
		Assert.Contains(cf.ComplianceCases[0].Entries, e => e.Text == "Owner called about the ICC claim.");
		LetterLogRecord letter = Assert.Single(cf.Letters);
		Assert.Equal(new DateTime(2026, 9, 28), letter.SentOn);
		CaseFileAppeal appeal = Assert.Single(cf.Appeals);
		Assert.Equal(AppealOutcomes.Upheld, appeal.Appeal.Outcome);
		Assert.Equal("Example Builders", Assert.Single(appeal.Bids).Contractor);
		Assert.Equal(s.Project.ProjectID, Assert.Single(cf.Improvements).ProjectID);
		Assert.Equal(s.Determination.DeterminationID, Assert.Single(cf.SiDeterminations).DeterminationID);
		Assert.Equal(s.Document.DocumentID, Assert.Single(cf.Documents).DocumentID);
		Assert.Single(cf.Comments);
		Assert.NotEmpty(cf.History);
	}

	[Fact]
	public void TheTimeline_IsNewestFirst_AndHasAnEntryForEveryArea()
	{
		using TempDatabase t = new TempDatabase();
		CaseFileSample s = CaseFileSample.Create(t.Store, Path.Combine(t.Folder, "source"));

		CaseFile cf = t.Store.GetCaseFile(s.PropertyId);
		List<CaseFileEntry> timeline = cf.Timeline.ToList();
		for (int i = 1; i < timeline.Count; i++)
		{
			Assert.True(timeline[i - 1].When >= timeline[i].When, $"{timeline[i - 1].Title} ({timeline[i - 1].When}) is listed before {timeline[i].Title} ({timeline[i].When})");
		}
		foreach (CaseFileEntryKind kind in new[]
		{
			CaseFileEntryKind.Inspection, CaseFileEntryKind.Status, CaseFileEntryKind.EventMembership, CaseFileEntryKind.LetterWritten,
			CaseFileEntryKind.LetterSent, CaseFileEntryKind.AppealReceived, CaseFileEntryKind.AppealDecided, CaseFileEntryKind.ContractorBid,
			CaseFileEntryKind.ImprovementProject, CaseFileEntryKind.SiDetermination, CaseFileEntryKind.Document, CaseFileEntryKind.Comment,
			CaseFileEntryKind.Compliance, CaseFileEntryKind.Merge, CaseFileEntryKind.RecentlyDeleted, CaseFileEntryKind.HistoryEvent, CaseFileEntryKind.Edit,
		})
		{
			Assert.Contains(timeline, e => e.Kind == kind);
		}

		// Dated records carry their own dates; the inspections are date-only.
		CaseFileEntry sent = Assert.Single(timeline, e => e.Kind == CaseFileEntryKind.LetterSent);
		Assert.Equal((new DateTime(2026, 9, 28), true), (sent.When, sent.IsDateOnly));
		Assert.Equal("Letter sent: Substantial damage", sent.Title);
		CaseFileEntry spring = Assert.Single(timeline, e => e.Kind == CaseFileEntryKind.Inspection && e.AssessmentId == s.Earlier.Assessment.AssessmentID);
		Assert.Equal(new DateTime(2024, 4, 10), spring.When);
		Assert.Equal(s.EarlierFlood.EventID, spring.EventId);
		Assert.Equal("Inspector C", spring.Who);
		Assert.Same(spring, timeline[^1]);
		CaseFileEntry received = Assert.Single(timeline, e => e.Kind == CaseFileEntryKind.AppealReceived);
		Assert.Equal(new DateTime(2026, 10, 2), received.When);
		Assert.Equal("Repair estimate too high", received.Detail);
		Assert.Equal(s.Case.CaseID, Assert.Single(timeline, e => e.Kind == CaseFileEntryKind.Compliance && e.Detail == "Owner called about the ICC claim.").CaseId);

		// The record-backed events are not listed a second time from the history.
		Assert.Single(timeline, e => e.Title.StartsWith("Letter written", StringComparison.Ordinal));
		Assert.Single(timeline, e => e.Title.StartsWith("Appeal received", StringComparison.Ordinal));
		Assert.Single(timeline, e => e.Title.StartsWith("Document added", StringComparison.Ordinal));
	}

	[Fact]
	public void FieldChanges_AreFlaggedAsEdits_AndEventsAreNot()
	{
		using TempDatabase t = new TempDatabase();
		CaseFileSample s = CaseFileSample.Create(t.Store, Path.Combine(t.Folder, "source"));

		CaseFile cf = t.Store.GetCaseFile(s.PropertyId);
		CaseFileEntry parcel = Assert.Single(cf.Timeline, e => e.IsEdit && e.AssessmentId == null && e.Detail != null && e.Detail.Contains("092-0004-0118-00"));
		Assert.Equal(CaseFileEntryKind.Edit, parcel.Kind);
		Assert.Equal("Clerk B", parcel.Who);
		Assert.All(cf.Timeline.Where(e => e.IsEdit), e => Assert.Equal(CaseFileEntryKind.Edit, e.Kind));
		Assert.All(cf.Timeline.Where(e => !e.IsEdit), e => Assert.NotEqual(CaseFileEntryKind.Edit, e.Kind));
		Assert.False(Assert.Single(cf.Timeline, e => e.Kind == CaseFileEntryKind.Status && e.Title == "Status: Final").IsEdit);
	}

	[Fact]
	public void ADeletedAssessment_IsListedAsDeleted()
	{
		using TempDatabase t = new TempDatabase();
		CaseFileSample s = CaseFileSample.Create(t.Store, Path.Combine(t.Folder, "source"));

		CaseFile cf = t.Store.GetCaseFile(s.PropertyId);
		CaseFileAssessment trashed = Assert.Single(cf.Assessments, a => a.Assessment.AssessmentID == s.Trashed.Assessment.AssessmentID);
		Assert.True(trashed.IsTrashed);
		Assert.NotEqual(trashed, cf.Latest);
		Assert.Equal("Inspection (in Recently deleted)", Assert.Single(cf.Timeline, e => e.Kind == CaseFileEntryKind.Inspection && e.AssessmentId == trashed.Assessment.AssessmentID).Title);
		Assert.Contains(cf.Timeline, e => e.Kind == CaseFileEntryKind.RecentlyDeleted && e.AssessmentId == trashed.Assessment.AssessmentID);
	}

	[Fact]
	public void ADeletedOrMissingStructure_IsRefused()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile ranch = SampleCases.RanchFlood(t.Store);
		Assert.Contains("no longer exists", Assert.Throws<ArgumentException>(() => t.Store.GetCaseFile(Guid.NewGuid())).Message);
		t.Store.TrashProperty(ranch.Property.PropertyID);
		Assert.Contains("Recently deleted", Assert.Throws<ArgumentException>(() => t.Store.GetCaseFile(ranch.Property.PropertyID)).Message);
	}

	[Fact]
	public void CumulativeDamage_IsWorkedOut_WhenSettingsTurnItOn()
	{
		using TempDatabase t = new TempDatabase();
		CaseFileSample s = CaseFileSample.Create(t.Store, Path.Combine(t.Folder, "source"));
		t.Store.SetCumulativeSettings(new CumulativeSettings { Enabled = true, LookbackYears = 10 });

		CaseFile cf = t.Store.GetCaseFile(s.PropertyId);
		Assert.Equal(CumulativeStatus.Evaluated, cf.Cumulative.Status);
		Assert.Equal(2, cf.Cumulative.Events.Count);
	}

	[Fact]
	public void AStructureWithManyAssessments_IsReadOverOneConnection()
	{
		using TempDatabase t = new TempDatabase();
		AssessmentFile ranch = SampleCases.RanchFlood(t.Store);
		for (int i = 0; i < 12; i++)
		{
			AssessmentFile f = t.Store.CreateAssessment(ranch.Property.PropertyID, applyDefaults: false);
			t.Store.AddReviewComment(f.Assessment.AssessmentID, "Comment " + i);
			t.Store.AddLetters(new[] { new LetterLogRecord { AssessmentID = f.Assessment.AssessmentID, Kind = "Not substantially damaged" } });
		}
		_ = t.Store.Reference;

		long before = t.Database.ConnectionsOpened;
		CaseFile cf = t.Store.GetCaseFile(ranch.Property.PropertyID);
		Assert.Equal(before + 1, t.Database.ConnectionsOpened);
		Assert.Equal(13, cf.Assessments.Count);
		Assert.Equal(12, cf.Letters.Count);
		Assert.Equal(12, cf.Comments.Count);
	}

	[Fact]
	public void TheGettersItIsBuiltFrom_StillReadOneRecordAtATime()
	{
		using TempDatabase t = new TempDatabase();
		CaseFileSample s = CaseFileSample.Create(t.Store, Path.Combine(t.Folder, "source"));
		Guid id = s.Current.Assessment.AssessmentID;
		Assert.Single(t.Store.GetLetters(id));
		Assert.Empty(t.Store.GetLetters(s.Earlier.Assessment.AssessmentID));
		Assert.Equal(AppealOutcomes.Upheld, t.Store.GetAppeal(id).Outcome);
		Assert.Null(t.Store.GetAppeal(s.Earlier.Assessment.AssessmentID));
		Assert.Single(t.Store.GetBids(id));
		Assert.Single(t.Store.GetReviewComments(id));
		Assert.Equal(4, t.Store.GetComplianceEntries(s.Case.CaseID).Count);
		Assert.False(t.Store.GetCumulativeSettings().Enabled);
	}
}
