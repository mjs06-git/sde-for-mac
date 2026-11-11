using Sde.Core.Assessments;
using Sde.Core.Improvements;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.TestSupport;

/// <summary>
/// One synthetic structure that touches every part of a case file: two events, a Final assessment with a photo, a
/// letter written and sent, an appeal with a contractor bid, a review comment, a compliance case, a document, an
/// improvement project, an SI determination, a field change, a merged duplicate and an assessment in Recently deleted.
/// The store's clock moves five minutes on every read, so the order of everything is known.
/// </summary>
public sealed class CaseFileSample
{
	public AssessmentFile Current { get; private set; }

	public AssessmentFile Earlier { get; private set; }

	public AssessmentFile Trashed { get; private set; }

	public EventRecord Flood { get; private set; }

	public EventRecord EarlierFlood { get; private set; }

	public ComplianceCaseRecord Case { get; private set; }

	public DocumentRecord Document { get; private set; }

	public ImprovementProjectRecord Project { get; private set; }

	public SIDeterminationRecord Determination { get; private set; }

	public Guid PropertyId => Current.Property.PropertyID;

	/// <summary>Builds the structure in <paramref name="store"/>; <paramref name="folder"/> holds the document's source file.</summary>
	public static CaseFileSample Create(SdeStore store, string folder)
	{
		DateTime clock = new DateTime(2026, 9, 20, 8, 0, 0);
		store.Clock = () => clock = clock.AddMinutes(5);
		store.UserName = "Clerk B";
		CaseFileSample s = new CaseFileSample();

		s.Flood = store.SaveEvent(new EventRecord { Name = "September flood", DRNumber = "DR-9999", StartDate = new DateTime(2026, 9, 14), EndDate = new DateTime(2026, 9, 16) });
		s.EarlierFlood = store.SaveEvent(new EventRecord { Name = "Spring flood", StartDate = new DateTime(2024, 4, 2) });

		// The current assessment (75.0%, Substantially Damaged), with a photo.
		s.Current = SampleCases.RanchFlood(store);
		s.Current.Assessment.DateofInspection = new DateTime(2026, 9, 21);
		s.Current.Attachments.Add(new AttachmentRecord { FileName = Guid.NewGuid() + ".jpg", Description = "Front", Active = true, DefaultImage = true, AttachmentType = "Photo" });
		store.SaveAssessment(s.Current);
		Guid pid = s.Current.Property.PropertyID;

		// An earlier event's assessment, and an empty one that was deleted.
		s.Earlier = store.CreateAssessment(pid, applyDefaults: false);
		s.Earlier.Assessment.InspectorName = "Inspector C";
		s.Earlier.Assessment.DateofInspection = new DateTime(2024, 4, 10);
		s.Earlier.Assessment.DateDamageOccurred = new DateTime(2024, 4, 2);
		store.SaveAssessment(s.Earlier);
		s.Trashed = store.CreateAssessment(pid, applyDefaults: false);
		s.Trashed.Assessment.DateofInspection = new DateTime(2026, 9, 25);
		store.SaveAssessment(s.Trashed);
		store.TrashAssessment(s.Trashed.Assessment.AssessmentID);

		store.SetEvent(new[] { s.Current.Assessment.AssessmentID }, s.Flood.EventID);
		store.SetEvent(new[] { s.Earlier.Assessment.AssessmentID }, s.EarlierFlood.EventID);

		// A field change to the structure itself (structure-level edit history).
		PropertyRecord p = store.GetProperty(pid);
		p.ParcelNumber = "092-0004-0118-00";
		store.SaveProperty(p);

		// A merged duplicate.
		PropertyRecord duplicate = store.SaveProperty(new PropertyRecord
		{
			StreetNumber = "123", StreetName = "River", StreetSuffixID = 172, City = "Exampleville", StateID = 36, ZipCode = "43702",
			StructureTypeID = 1,
		});
		store.MergeProperties(pid, duplicate.PropertyID);

		// Review, Final, letters, appeal and bid.
		store.AddReviewComment(s.Current.Assessment.AssessmentID, "Check the garage area against the sketch.");
		store.SetStatus(s.Current.Assessment.AssessmentID, AssessmentStatus.Final);
		store.AddLetters(new[] { new LetterLogRecord { AssessmentID = s.Current.Assessment.AssessmentID, Kind = "Substantial damage", FileName = Path.Combine(folder, "letters", "123 River Street.docx") } });
		store.MarkLetterSent(s.Current.Assessment.AssessmentID, new DateTime(2026, 9, 28));
		store.SaveAppeal(new AppealRecord { AssessmentID = s.Current.Assessment.AssessmentID, ReceivedOn = new DateTime(2026, 10, 2), Basis = "Repair estimate too high" });
		store.SaveBid(new ContractorBidRecord { AssessmentID = s.Current.Assessment.AssessmentID, Contractor = "Example Builders", Amount = 61000m, BidDate = new DateTime(2026, 10, 1) });
		AppealRecord appeal = store.GetAppeal(s.Current.Assessment.AssessmentID);
		appeal.Outcome = AppealOutcomes.Upheld;
		store.SaveAppeal(appeal);

		// Compliance.
		s.Case = store.OpenComplianceCase(s.Current.Assessment.AssessmentID);
		s.Case.Path = CompliancePaths.Elevate;
		s.Case.Deadline = new DateTime(2027, 3, 1);
		s.Case.PermitNumber = "BP-0101";
		store.SaveComplianceCase(s.Case);
		store.AddComplianceEntry(s.Case.CaseID, "Call", "Owner called about the ICC claim.");

		// A document, an improvement project and an SI determination.
		Directory.CreateDirectory(folder);
		string source = Path.Combine(folder, "elevation certificate.pdf");
		File.WriteAllText(source, "%PDF-1.4 synthetic");
		s.Document = store.AddDocument(pid, source, DocumentKinds.ElevationCertificate, "Elevation certificate 2026");
		s.Project = store.SaveImprovementProject(new ImprovementProjectRecord { PropertyID = pid, PermitNumber = "BP-0002", Description = "Second-story addition", ProjectDate = new DateTime(2025, 5, 1), Cost = 40000m });
		s.Determination = store.SaveSiDetermination(new SIDeterminationRecord
		{
			PropertyID = pid, ThresholdPercent = 50m, CumulativeCost = 40000m, MarketValue = 150000m, Percent = 26.7m, Result = SiResult.NotSubstantialImprovement,
		});
		return s;
	}
}
