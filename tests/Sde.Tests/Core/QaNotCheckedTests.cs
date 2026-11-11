using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Quality;
using Sde.Core.Records;

namespace Sde.Tests.Core;

/// <summary>
/// The checks that need data the database doesn't keep (a flood lookup, high-water marks) say so when it is missing,
/// run when it is there, and never go online.
/// </summary>
public class QaNotCheckedTests
{
	private static readonly Lazy<ReferenceData> Reference = new Lazy<ReferenceData>(() =>
	{
		using TempDatabase t = new TempDatabase();
		return t.Store.Reference;
	});

	private static AssessmentFile File(string number, int zone = 30, decimal? depth = 5m)
	{
		PropertyRecord p = new PropertyRecord
		{
			PropertyID = Guid.NewGuid(), StreetNumber = number, StreetName = "Example", StreetSuffixID = 172, FIRMZone = zone,
			Latitude = 39.1m, Longitude = -84.1m, MailingStreetName = "Post Office Box",
		};
		AssessmentRecord a = new AssessmentRecord
		{
			AssessmentID = Guid.NewGuid(), PropertyID = p.PropertyID, Active = true, CauseofDamageID = LookupIds.CauseFlood,
			DepthOfFlood = depth, DateDamageOccurred = new DateTime(2026, 9, 14), DateofInspection = new DateTime(2026, 9, 20),
		};
		return new AssessmentFile { Property = p, Assessment = a };
	}

	private static QaInput Input(params AssessmentFile[] files) => new QaInput { Assessments = files, Reference = Reference.Value };

	[Fact]
	public void WithNoStoredData_BothChecksAreListedAsNotChecked_AndNothingIsReportedAsFound()
	{
		QaResult result = QaRules.CheckWithStatus(Input(File("1"), File("2")));
		Assert.DoesNotContain(result.Findings, f => f.RuleId is QaRuleIds.ZoneDiffersFromNfhl or QaRuleIds.DepthVsHighWaterMarks);
		QaNotChecked zone = result.NotChecked.Single(n => n.RuleId == QaRuleIds.ZoneDiffersFromNfhl);
		Assert.Equal(("Not checked: no stored flood lookup.", QaNotCheckedReason.NoStoredFloodLookup, true, 0), (zone.Message, zone.Reason, zone.CanCheckOnline, zone.Count));
		QaNotChecked marks = result.NotChecked.Single(n => n.RuleId == QaRuleIds.DepthVsHighWaterMarks);
		Assert.Equal(("Not checked: no high-water marks loaded.", QaNotCheckedReason.NoHighWaterMarks), (marks.Message, marks.Reason));
		Assert.Equal(2, result.NotChecked.Count);
	}

	[Fact]
	public void WhenTheDataIsThere_TheChecksRunAndAreNotListed()
	{
		AssessmentFile same = File("1");
		AssessmentFile differs = File("2");
		Dictionary<Guid, string> zones = new() { [same.Property.PropertyID] = Reference.Value.LookupName(30), [differs.Property.PropertyID] = "X" };
		Dictionary<Guid, decimal> marks = new() { [same.Assessment.AssessmentID] = 5m, [differs.Assessment.AssessmentID] = 9m };
		QaResult result = QaRules.CheckWithStatus(Input(same, differs) with { NfhlZones = zones, HighWaterMarkEstimates = marks });
		Assert.Empty(result.NotChecked);
		Assert.Equal(differs.Property.PropertyID, Assert.Single(result.Findings, f => f.RuleId == QaRuleIds.ZoneDiffersFromNfhl).PropertyId);
		Assert.Equal(differs.Assessment.AssessmentID, Assert.Single(result.Findings, f => f.RuleId == QaRuleIds.DepthVsHighWaterMarks).AssessmentId);
	}

	[Fact]
	public void ALookupForSomeStructures_ListsHowManyWereNotChecked()
	{
		AssessmentFile looked = File("1");
		AssessmentFile notLooked = File("2");
		QaInput input = Input(looked, notLooked) with
		{
			NfhlZones = new Dictionary<Guid, string> { [looked.Property.PropertyID] = Reference.Value.LookupName(30) },
			HighWaterMarkEstimates = new Dictionary<Guid, decimal>(),
		};
		QaNotChecked zone = Assert.Single(QaRules.NotChecked(input));
		Assert.Equal((QaRuleIds.ZoneDiffersFromNfhl, 1, "Not checked for 1 of 2 structures: no stored flood lookup."), (zone.RuleId, zone.Count, zone.Message));
	}

	[Fact]
	public void NothingToCheck_ListsNothing_AndCheckIsUnchanged()
	{
		Assert.Empty(QaRules.NotChecked(Input()));
		AssessmentFile f = File("1");
		Assert.Equal(QaRules.Check(Input(f)).Select(x => x.RuleId), QaRules.CheckWithStatus(Input(f)).Findings.Select(x => x.RuleId));
	}

	[Fact]
	public void TheQualityChecksNeverReachTheNetwork()
	{
		// The rules are pure functions over their input and the assembly has no HTTP code to call.
		Assert.DoesNotContain(typeof(QaRules).Assembly.GetReferencedAssemblies(), a => a.Name == "System.Net.Http");
	}
}
