using Sde.Core.Assessments;
using Sde.Core.Improvements;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Tests.Data;

/// <summary>The agenda: one list of what is coming up and overdue, shared by the app, the AI assistant and the calendar file (v3.5).</summary>
public class AgendaTests
{
	private static readonly DateTime Today = new DateTime(2026, 10, 20);

	/// <summary>Two flooded structures in the September flood and one outside it, with one dated item of every kind.</summary>
	private sealed class Sample
	{
		public TempDatabase Db { get; } = new TempDatabase();

		public SdeStore Store => Db.Store;

		public AssessmentFile Late { get; }

		public AssessmentFile Soon { get; }

		public AssessmentFile Elsewhere { get; }

		public EventRecord Flood { get; }

		public ComplianceCaseRecord Overdue { get; }

		public ComplianceCaseRecord Upcoming { get; }

		public Sample()
		{
			Store.Clock = () => Today;
			Late = SampleCases.RanchFlood(Store);
			Soon = SampleCases.RanchFlood(Store);
			Elsewhere = SampleCases.RanchFlood(Store);
			Flood = Store.SaveEvent(new EventRecord { Name = "September flood", StartDate = new DateTime(2026, 9, 14), EndDate = new DateTime(2026, 9, 16) });
			Store.SetEvent(new[] { Late.Assessment.AssessmentID, Soon.Assessment.AssessmentID }, Flood.EventID);

			Overdue = Store.OpenComplianceCase(Late.Assessment.AssessmentID);
			Overdue.Deadline = Today.AddDays(-5);
			Overdue.Path = CompliancePaths.Elevate;
			Overdue.PermitNumber = "BP-0101";
			Store.SaveComplianceCase(Overdue);
			Upcoming = Store.OpenComplianceCase(Soon.Assessment.AssessmentID);
			Upcoming.Deadline = Today.AddDays(10);
			Upcoming.FinalInspectionOn = Today.AddDays(3);
			Upcoming.EcReceivedOn = Today.AddDays(-2);
			Store.SaveComplianceCase(Upcoming);

			Store.SaveAppeal(new AppealRecord { AssessmentID = Elsewhere.Assessment.AssessmentID, ReceivedOn = Today.AddDays(1), Basis = "Value too low" });
			AppealRecord decided = Store.GetAppeal(Elsewhere.Assessment.AssessmentID);
			decided.Outcome = AppealOutcomes.Upheld;
			decided.DecidedOn = Today.AddDays(4);
			Store.SaveAppeal(decided);
			Store.AddLetters(new[] { new LetterLogRecord { AssessmentID = Elsewhere.Assessment.AssessmentID, Kind = "Not substantially damaged" } });
			Store.MarkLetterSent(Elsewhere.Assessment.AssessmentID, Today.AddDays(2));
		}
	}

	[Fact]
	public void EveryKind_IsListedOnItsOwnDate()
	{
		Sample s = new Sample();
		using TempDatabase _ = s.Db;
		List<AgendaEntry> all = s.Store.GetAgenda(null, null);

		AgendaEntry deadline = Assert.Single(all, e => e.Kind == AgendaEntryKind.ComplianceDeadline && e.CaseId == s.Overdue.CaseID);
		Assert.Equal(Today.AddDays(-5), deadline.Date);
		Assert.Equal("Compliance deadline (SD): 123 River Street", deadline.Title);
		Assert.Equal("Path: Elevate. Permit BP-0101.", deadline.Detail);
		Assert.Equal(("123 River Street", s.Late.Property.PropertyID, s.Late.Assessment.AssessmentID), (deadline.Address, deadline.PropertyId, deadline.AssessmentId));
		Assert.Equal(s.Overdue.CaseID, deadline.RecordId);
		Assert.Equal(Today.AddDays(10), Assert.Single(all, e => e.Kind == AgendaEntryKind.ComplianceDeadline && e.CaseId == s.Upcoming.CaseID).Date);
		Assert.Equal(Today.AddDays(3), Assert.Single(all, e => e.Kind == AgendaEntryKind.FinalInspection).Date);
		Assert.Equal(Today.AddDays(-2), Assert.Single(all, e => e.Kind == AgendaEntryKind.ElevationCertificateReceived).Date);
		AgendaEntry received = Assert.Single(all, e => e.Kind == AgendaEntryKind.AppealReceived);
		Assert.Equal((Today.AddDays(1), "Value too low", s.Elsewhere.Assessment.AssessmentID), (received.Date, received.Detail, received.AssessmentId));
		Assert.Equal("Appeal decided (Upheld): 123 River Street", Assert.Single(all, e => e.Kind == AgendaEntryKind.AppealDecision && e.Date == Today.AddDays(4)).Title);
		AgendaEntry letter = Assert.Single(all, e => e.Kind == AgendaEntryKind.LetterSent);
		Assert.Equal((Today.AddDays(2), "Letter sent (Not substantially damaged): 123 River Street"), (letter.Date, letter.Title));
		AgendaEntry start = Assert.Single(all, e => e.Kind == AgendaEntryKind.EventStart);
		Assert.Equal((new DateTime(2026, 9, 14), "September flood starts", s.Flood.EventID), (start.Date, start.Title, start.EventId));
		Assert.Equal(new DateTime(2026, 9, 16), Assert.Single(all, e => e.Kind == AgendaEntryKind.EventEnd).Date);

		// Oldest first.
		Assert.Equal(all.OrderBy(e => e.Date).Select(e => e.Date), all.Select(e => e.Date));
	}

	[Fact]
	public void FromAndTo_LimitTheDays_BothIncluded()
	{
		Sample s = new Sample();
		using TempDatabase _ = s.Db;
		List<AgendaEntry> window = s.Store.GetAgenda(Today.AddDays(1), Today.AddDays(3));
		Assert.Equal(new[] { AgendaEntryKind.AppealReceived, AgendaEntryKind.LetterSent, AgendaEntryKind.FinalInspection }, window.Select(e => e.Kind));
		Assert.All(s.Store.GetAgenda(Today, null), e => Assert.True(e.Date >= Today));
		Assert.All(s.Store.GetAgenda(null, Today), e => Assert.True(e.Date <= Today));
		Assert.Empty(s.Store.GetAgenda(new DateTime(2025, 1, 1), new DateTime(2025, 1, 31)));
	}

	[Fact]
	public void Overdue_AndDaysOverdue_AreCountedFromTheGivenToday()
	{
		Sample s = new Sample();
		using TempDatabase _ = s.Db;
		AgendaEntry late = Assert.Single(s.Store.GetAgenda(null, null), e => e.IsOverdue);
		Assert.Equal((s.Overdue.CaseID, 5), (late.CaseId, late.DaysOverdue));

		List<AgendaEntry> later = s.Store.GetAgenda(null, null, today: Today.AddDays(12));
		Assert.Equal(new[] { (s.Overdue.CaseID, 17), (s.Upcoming.CaseID, 2) },
			later.Where(e => e.IsOverdue).Select(e => (e.CaseId.Value, e.DaysOverdue)));
		Assert.All(later.Where(e => e.Kind != AgendaEntryKind.ComplianceDeadline), e => Assert.Equal((false, 0), (e.IsOverdue, e.DaysOverdue)));
		Assert.Empty(s.Store.GetAgenda(null, null, today: Today.AddDays(-30)).Where(e => e.IsOverdue));
	}

	[Fact]
	public void AnEvent_ListsOnlyItsOwnItems()
	{
		Sample s = new Sample();
		using TempDatabase _ = s.Db;
		EventRecord other = s.Store.SaveEvent(new EventRecord { Name = "Spring flood", StartDate = Today.AddDays(5) });

		List<AgendaEntry> inEvent = s.Store.GetAgenda(null, null, s.Flood.EventID);
		Assert.DoesNotContain(inEvent, e => e.AssessmentId == s.Elsewhere.Assessment.AssessmentID);
		Assert.DoesNotContain(inEvent, e => e.EventId == other.EventID);
		Assert.Equal(6, inEvent.Count);   // two deadlines, the final inspection, the certificate, and the event's two days

		// A substantial improvement case belongs to an event through its structure's assessments.
		SIDeterminationRecord si = s.Store.SaveSiDetermination(new SIDeterminationRecord
		{
			PropertyID = s.Soon.Property.PropertyID, ThresholdPercent = 50m, CumulativeCost = 90000m, MarketValue = 150000m, Percent = 60m, Result = SiResult.SubstantialImprovement,
		});
		ComplianceCaseRecord improvement = s.Store.OpenComplianceCaseForImprovement(si.DeterminationID);
		improvement.Deadline = Today.AddDays(20);
		s.Store.SaveComplianceCase(improvement);
		Assert.Contains(s.Store.GetAgenda(null, null, s.Flood.EventID), e => e.CaseId == improvement.CaseID);
		Assert.Contains(s.Store.GetAgenda(null, null, other.EventID), e => e.Kind == AgendaEntryKind.EventStart);
		Assert.DoesNotContain(s.Store.GetAgenda(null, null, other.EventID), e => e.CaseId == improvement.CaseID);
	}

	[Fact]
	public void ClosedCases_AreLeftOutByDefault()
	{
		Sample s = new Sample();
		using TempDatabase _ = s.Db;
		ComplianceCaseRecord closed = s.Store.GetComplianceCase(s.Upcoming.CaseID);
		closed.Status = ComplianceStatuses.Closed;
		s.Store.SaveComplianceCase(closed);

		Assert.DoesNotContain(s.Store.GetAgenda(null, null), e => e.CaseId == closed.CaseID);
		List<AgendaEntry> withClosed = s.Store.GetAgenda(null, null, includeClosedCases: true);
		Assert.Equal(new[] { AgendaEntryKind.ElevationCertificateReceived, AgendaEntryKind.FinalInspection },
			withClosed.Where(e => e.CaseId == closed.CaseID).Select(e => e.Kind));
	}

	[Fact]
	public void DeletedStructuresAndAssessments_AreLeftOut()
	{
		Sample s = new Sample();
		using TempDatabase _ = s.Db;
		s.Store.SetStatus(s.Elsewhere.Assessment.AssessmentID, AssessmentStatus.Draft, "Entered for the wrong structure");
		s.Store.TrashProperty(s.Elsewhere.Property.PropertyID);
		s.Store.TrashAssessment(s.Soon.Assessment.AssessmentID);
		List<AgendaEntry> all = s.Store.GetAgenda(null, null);
		Assert.DoesNotContain(all, e => e.PropertyId == s.Elsewhere.Property.PropertyID);
		Assert.DoesNotContain(all, e => e.CaseId == s.Upcoming.CaseID);
		Assert.Contains(all, e => e.CaseId == s.Overdue.CaseID);
	}

	[Fact]
	public void TheCounts_MatchTheSidebarRule()
	{
		Sample s = new Sample();
		using TempDatabase _ = s.Db;
		Assert.Equal(1, s.Store.GetOverdueCount(Today));
		Assert.Equal(1, s.Store.GetOverdueCount(Today, s.Flood.EventID));
		Assert.Equal(s.Store.GetActiveComplianceCases().Count(c => c.IsOverdue(Today)), s.Store.GetOverdueCount(Today));
		Assert.Equal(0, s.Store.GetDueSoonCount(7, Today));
		Assert.Equal(1, s.Store.GetDueSoonCount(10, Today));
		Assert.Equal(1, s.Store.GetDueSoonCount(0, Today.AddDays(10)));
		Assert.Equal(2, s.Store.GetOverdueCount(Today.AddDays(11)));
		Assert.Equal(0, s.Store.GetDueSoonCount(30, Today, s.Store.SaveEvent(new EventRecord { Name = "Empty" }).EventID));
		Assert.Throws<ArgumentOutOfRangeException>(() => s.Store.GetDueSoonCount(-1, Today));

		// A deleted assessment's case is not counted, as on the sidebar.
		s.Store.SetStatus(s.Late.Assessment.AssessmentID, AssessmentStatus.Draft);
		s.Store.TrashAssessment(s.Late.Assessment.AssessmentID);
		Assert.Equal(0, s.Store.GetOverdueCount(Today));
	}
}
