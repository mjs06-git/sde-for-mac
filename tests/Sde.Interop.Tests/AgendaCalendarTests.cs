using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.Interop.Tests;

/// <summary>The store's agenda written as a calendar file through <see cref="AgendaItem.FromEntry"/>.</summary>
public class AgendaCalendarTests
{
	private static readonly DateTime Today = new DateTime(2026, 10, 20);

	[Fact]
	public void EveryKind_MapsToTheCalendarKindOfTheSameName()
	{
		foreach (AgendaEntryKind kind in Enum.GetValues<AgendaEntryKind>())
		{
			Assert.Equal(kind.ToString(), AgendaItem.KindOf(kind).ToString());
		}
		Assert.Equal(Enum.GetValues<AgendaEntryKind>().Length, Enum.GetValues<AgendaKind>().Length);
	}

	[Fact]
	public void TheAgenda_RoundTripsIntoACalendarFile()
	{
		using TempDatabase t = new TempDatabase();
		t.Store.Clock = () => Today;
		AssessmentFile ranch = SampleCases.RanchFlood(t.Store);
		ComplianceCaseRecord c = t.Store.OpenComplianceCase(ranch.Assessment.AssessmentID);
		c.Deadline = Today.AddDays(10);
		c.Path = CompliancePaths.Elevate;
		c.EcReceivedOn = Today.AddDays(-1);
		t.Store.SaveComplianceCase(c);

		List<AgendaEntry> entries = t.Store.GetAgenda(null, null);
		List<AgendaItem> items = entries.Select(AgendaItem.FromEntry).ToList();
		AgendaItem deadline = Assert.Single(items, i => i.Kind == AgendaKind.ComplianceDeadline);
		Assert.Equal(Today.AddDays(10), deadline.Date);
		Assert.Equal("Compliance deadline (SD): 123 River Street", deadline.Title);
		Assert.Equal("Path: Elevate.", deadline.Notes);
		Assert.Equal("123 River Street", deadline.Address);
		Assert.Equal(c.CaseID.ToString(), deadline.RecordId);
		Assert.Single(items, i => i.Kind == AgendaKind.ElevationCertificateReceived);

		string ics = Unfold(IcsWriter.Build(items, new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc)));
		Assert.Contains("SUMMARY:Compliance deadline (SD): 123 River Street", ics);
		Assert.Contains("DTSTART;VALUE=DATE:20261030", ics);
		Assert.Contains("UID:" + IcsWriter.Uid(deadline), ics);
		Assert.Contains("UID:elevationcertificatereceived-" + c.CaseID.ToString().ToLowerInvariant() + "@sde-for-mac", ics);
		Assert.Equal(entries.Count, ics.Split("BEGIN:VEVENT").Length - 1);

		// Writing again gives the same identifiers, so Calendar updates the entries instead of adding more.
		string again = Unfold(IcsWriter.Build(t.Store.GetAgenda(null, null).Select(AgendaItem.FromEntry), new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)));
		Assert.Equal(Uids(ics), Uids(again));
	}

	/// <summary>Joins folded lines back together (RFC 5545 section 3.1).</summary>
	private static string Unfold(string ics) => ics.Replace("\r\n ", "");

	private static List<string> Uids(string ics) => ics.Split("\r\n").Where(l => l.StartsWith("UID:", StringComparison.Ordinal)).ToList();
}
