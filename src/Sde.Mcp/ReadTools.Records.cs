using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Improvements;
using Sde.Core.Records;
using Sde.Data;
using Sde.Reports;

namespace Sde.Mcp;

/// <summary>
/// The read tools for what SDE keeps beside an assessment: letters to the owner, documents, an appeal and its bids,
/// improvement projects and substantial improvement determinations, and one compliance case with its timeline. File
/// names are given without their folder, and no file's contents are read.
/// </summary>
public sealed partial class ReadTools
{
	/// <summary>The most timeline lines one page of get_compliance_case returns.</summary>
	public const int MaxTimelinePage = 100;

	[McpServerTool(Name = "list_letters", Title = "List the letters to an owner", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
	[Description("Lists the letters SDE wrote to an owner (from its Letters page and compliance cases), newest first: what each letter is, which template it came from, when it was written and when someone marked it as sent. Give an assessment ID for that assessment's letters, or a structure ID for the letters of every assessment of the structure. Letters are marked as sent only by a person, in SDE." + Untrusted)]
	public LetterList ListLetters(
		[Description("The assessment's ID (find_structures lists them).")] string assessmentId = null,
		[Description("Or the structure's ID (find_structures lists it as propertyId), for the letters of all its assessments.")] string structureId = null)
	{
		sde.RequireDatabase();
		SdeStore store = sde.Reader;
		(PropertyRecord p, AssessmentFile file) = RequireStructure(store, structureId, assessmentId);
		List<Guid> assessments = file != null
			? new List<Guid> { file.Assessment.AssessmentID }
			: store.GetAssessmentsForProperty(p.PropertyID).Select(a => a.AssessmentID).ToList();
		List<LetterItem> letters = assessments.SelectMany(store.GetLetters)
			.OrderByDescending(l => l.GeneratedOn ?? DateTime.MinValue)
			.Select(l => new LetterItem(l.LetterID.ToString(), l.AssessmentID.ToString(), Blank(l.Kind), Blank(FileNameOnly(l.Template)),
				Describe.Date(l.GeneratedOn), Describe.Date(l.SentOn), l.SentOn != null, Blank(FileNameOnly(l.FileName))))
			.ToList();
		string note = letters.Count == 0
			? (file != null ? "SDE hasn't written a letter for this assessment." : "SDE hasn't written a letter for any assessment of this structure.")
			: null;
		return new LetterList(sde.DatabasePath, p.PropertyID.ToString(), file?.Assessment.AssessmentID.ToString(), Describe.Address(p, store.Reference),
			letters.Count, letters.Count(l => l.IsSent), letters, note);
	}

	[McpServerTool(Name = "list_documents", Title = "List a structure's documents", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
	[Description("Lists the documents SDE keeps with a structure (elevation certificates, permits and others), newest first: the kind, title, file name, when it was added and by whom, and for an elevation certificate the values SDE read from it. The files themselves aren't read or shared." + Untrusted)]
	public DocumentList ListDocuments(
		[Description("The structure's ID (find_structures lists it as propertyId).")] string structureId = null,
		[Description("Or an assessment's ID, for its structure's documents.")] string assessmentId = null)
	{
		sde.RequireDatabase();
		SdeStore store = sde.Reader;
		(PropertyRecord p, _) = RequireStructure(store, structureId, assessmentId);
		List<DocumentItem> documents = store.GetDocuments(p.PropertyID).Select(d => new DocumentItem(d.DocumentID.ToString(), d.AssessmentID?.ToString(),
			DocumentKind(d.Kind), Blank(Describe.Shorten(d.Title, 200)), Blank(Describe.Shorten(FileNameOnly(d.OriginalName), 200)), Describe.Date(d.AddedOn),
			Blank(d.AddedBy), DocumentFields(d.Fields))).ToList();
		return new DocumentList(sde.DatabasePath, p.PropertyID.ToString(), Describe.Address(p, store.Reference), documents.Count, documents,
			documents.Count == 0 ? "No documents are kept with this structure." : null);
	}

	[McpServerTool(Name = "get_appeal", Title = "Read an appeal", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
	[Description("Reads the owner's appeal of an assessment's determination: when it was received, what the owner disputes, the outcome (Pending, Upheld, Revised or Withdrawn) and when it was decided, with the contractor bids sent in and which one, if any, the assessment's repair cost uses. Appeals, bids and outcomes are changed only by a person, in SDE." + Untrusted)]
	public AppealResult GetAppeal([Description("The assessment's ID (find_structures lists them).")] string assessmentId)
	{
		AssessmentFile file = sde.LoadAssessment(sde.Reader, assessmentId);
		SdeStore store = sde.Reader;
		Guid id = file.Assessment.AssessmentID;
		AssessmentRecord a = file.Assessment;
		AppealRecord appeal = store.GetAppeal(id);
		List<BidItem> bids = store.GetBids(id).Select(b => new BidItem(b.BidID.ToString(), Blank(Describe.Shorten(b.Contractor, 120)), b.Amount,
			Describe.Date(b.BidDate), Describe.Shorten(Blank(b.Notes)),
			a.CostOfRepairsID == LookupIds.RepairContractorEstimate && b.Amount != null && a.ContractorEstimate == b.Amount)).ToList();
		string note = appeal == null
			? (bids.Count == 0 ? "No appeal is recorded for this assessment." : "No appeal is recorded for this assessment, but it has contractor bids.")
			: null;
		return new AppealResult(sde.DatabasePath, id.ToString(), Describe.Address(file.Property, store.Reference), appeal != null,
			Describe.Date(appeal?.ReceivedOn), Describe.Shorten(Blank(appeal?.Basis)), appeal == null ? null : appeal.Outcome ?? AppealOutcomes.Pending,
			Describe.Date(appeal?.DecidedOn), Blank(appeal?.DecidedBy), Describe.Shorten(Blank(appeal?.Notes)), Describe.Result(a), bids, note);
	}

	[McpServerTool(Name = "list_improvements", Title = "List improvements and SI determinations", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
	[Description("Lists a structure's improvement projects (permits) and its substantial improvement (SI) determinations, newest first. For each project it gives the cumulative figures SDE's Improvements sheet shows: the cost counted with earlier projects under the community's look-back rule, the percent of the market value, the result and why each project was or wasn't counted. Substantial improvement is separate from substantial damage and never changes an assessment's result." + Untrusted)]
	public ImprovementList ListImprovements(
		[Description("The structure's ID (find_structures lists it as propertyId).")] string structureId = null,
		[Description("Or an assessment's ID, for its structure.")] string assessmentId = null)
	{
		sde.RequireDatabase();
		SdeStore store = sde.Reader;
		(PropertyRecord p, _) = RequireStructure(store, structureId, assessmentId);
		SiSettings settings = store.GetSiSettings();
		// The market value the Improvements sheet starts with: the latest assessment's building value, on its basis.
		AssessmentRecord latest = store.GetAssessmentsForProperty(p.PropertyID).FirstOrDefault();
		decimal? marketValue = null;
		string basis = null;
		if (latest != null && CumulativeDamage.BuildingValue(latest) is decimal value)
		{
			marketValue = Math.Round(value, 2);
			basis = store.Reference.LookupName(latest.ValueOfBuildingID) ?? "Building value from the damage assessment";
		}
		DateTime today = store.Clock().Date;
		List<ImprovementProjectRecord> projects = store.GetImprovementProjects(p.PropertyID);
		List<ImprovementProjectItem> items = projects.Select(project =>
		{
			SiResult r = ImprovementCalculator.Evaluate(project, projects.Where(o => o.ProjectID != project.ProjectID).ToList(), marketValue, settings, today);
			SiFigures figures = new SiFigures(r.CostCounted, r.Percent, r.ThresholdPercent, r.LookbackYears, Describe.Date(r.WindowStart), r.ResultText,
				r.Projects.Select(l => new SiLineItem(l.Project.ProjectID.ToString(), ProjectTitle(l.Project), l.IsCurrent, l.Counted, l.CostCounted, l.Reason)).ToList(),
				r.Explanation);
			return new ImprovementProjectItem(project.ProjectID.ToString(), Blank(project.PermitNumber), Describe.Shorten(Blank(project.Description)),
				Describe.Date(project.ProjectDate), project.Cost, Blank(project.CostBasis), project.Included, Describe.Shorten(Blank(project.ExclusionReason)), figures);
		}).ToList();
		List<SiDeterminationItem> determinations = store.GetSiDeterminations(p.PropertyID).Select(d => new SiDeterminationItem(d.DeterminationID.ToString(),
			Describe.Date(d.DeterminedOn), d.Result, d.CumulativeCost, d.Percent, d.ThresholdPercent, d.MarketValue, Blank(d.MarketValueBasis), d.LookbackYears,
			Blank(d.DeterminedBy), Describe.Shorten(Blank(d.Notes)), store.GetComplianceCaseForImprovement(d.DeterminationID)?.CaseID.ToString())).ToList();
		string note = projects.Count == 0 && determinations.Count == 0
			? "No improvement projects or substantial improvement determinations are recorded for this structure."
			: marketValue == null && projects.Count > 0
				? "The latest assessment has no building value, so the percentages can't be worked out. In SDE's Improvements sheet a person can enter the market value."
				: null;
		return new ImprovementList(sde.DatabasePath, p.PropertyID.ToString(), Describe.Address(p, store.Reference),
			new SiRules(settings.ThresholdPercent, settings.LookbackYears, settings.IncludePriorProjects), marketValue, basis, items, determinations, note);
	}

	[McpServerTool(Name = "get_compliance_case", Title = "Read a compliance case", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
	[Description("Reads one compliance case as list_compliance_cases shows it (path, permit, deadline, ICC claim, elevation certificate, final inspection, certificate of compliance, status), with its timeline of notes, calls, letters and status changes, newest first, a page at a time. Case details are changed only by a person, in SDE." + Untrusted)]
	public ComplianceCaseDetails GetComplianceCase(
		[Description("The case's ID (list_compliance_cases lists them).")] string caseId,
		[Description("The page of the timeline to return, starting at 1.")] int page = 1,
		[Description("Timeline lines per page, 1 to 100 (default 50).")] int pageSize = 50)
	{
		sde.RequireDatabase();
		SdeStore store = sde.Reader;
		ComplianceCaseRecord c = RequireCase(store, caseId);
		pageSize = Math.Clamp(pageSize, 1, MaxTimelinePage);
		page = Math.Max(page, 1);
		ComplianceRow row = ComplianceReport.Rows(store, store.Clock().Date, new[] { c }).Single();
		List<ComplianceEntryRecord> entries = store.GetComplianceEntries(c.CaseID);
		entries.Reverse();
		int pages = Math.Max(1, (entries.Count + pageSize - 1) / pageSize);
		List<ComplianceEntryItem> timeline = entries.Skip((page - 1) * pageSize).Take(pageSize).Select(e => new ComplianceEntryItem(e.EntryID.ToString(),
			Describe.Time(e.EntryOn), Blank(e.Kind), Describe.Shorten(Blank(e.Text), 2000), Blank(e.CreatedBy), Blank(e.LetterID))).ToList();
		string next = page < pages ? $"Showing page {page} of {pages}. For older lines, call get_compliance_case again with page = {page + 1}." : null;
		return new ComplianceCaseDetails(sde.DatabasePath, CaseItem(row), c.DeterminationID?.ToString(), Describe.Date(c.CreatedOn), Blank(c.CreatedBy),
			entries.Count, page, pageSize, pages, timeline, next);
	}

	/// <summary>A compliance case by ID, or a tool error saying where case IDs come from.</summary>
	internal static ComplianceCaseRecord RequireCase(SdeStore store, string caseId)
	{
		if (!Guid.TryParse(caseId?.Trim(), out Guid id))
		{
			throw new McpException($"\"{Describe.Shorten(caseId, 60)}\" isn't a compliance case ID. list_compliance_cases lists the cases and their IDs.");
		}
		return store.GetComplianceCase(id) ?? throw new McpException($"There's no compliance case {id} in this database. list_compliance_cases lists the cases.");
	}

	/// <summary>
	/// The structure a tool is about, from its structure ID or an assessment's ID (with the assessment, when one was
	/// given). A tool error when neither is given, or for one that isn't there or is in Recently deleted.
	/// </summary>
	private (PropertyRecord Property, AssessmentFile Assessment) RequireStructure(SdeStore store, string structureId, string assessmentId)
	{
		if (!string.IsNullOrWhiteSpace(assessmentId))
		{
			AssessmentFile file = sde.LoadAssessment(store, assessmentId);
			return (file.Property, file);
		}
		if (string.IsNullOrWhiteSpace(structureId))
		{
			throw new McpException("Give a structure ID or an assessment ID; find_structures lists both (propertyId and assessmentId).");
		}
		if (!Guid.TryParse(structureId.Trim(), out Guid id))
		{
			throw new McpException($"\"{Describe.Shorten(structureId, 60)}\" isn't a structure ID. IDs look like 3f2c9a1e-5b7d-4c1a-9e8f-0a1b2c3d4e5f; find_structures lists them as propertyId.");
		}
		PropertyRecord p = store.GetProperty(id);
		if (p == null)
		{
			// An assessment ID given as the structure works too, as assign_to_team takes either.
			if (store.LoadAssessment(id) != null)
			{
				AssessmentFile file = sde.LoadAssessment(store, id.ToString());
				return (file.Property, null);
			}
			throw new McpException($"There's no structure {id} in this database. find_structures lists the structures (propertyId).");
		}
		if (!p.Active)
		{
			throw new McpException($"The structure {id} is in Recently deleted in SDE, so it can't be used here.");
		}
		return (p, null);
	}

	/// <summary>A file's name without its folder, so no tool gives out where files are kept on the Mac.</summary>
	private static string FileNameOnly(string path) => string.IsNullOrWhiteSpace(path) ? null : Path.GetFileName(path.Replace('\\', '/').TrimEnd('/'));

	private static string DocumentKind(string kind) => kind switch
	{
		DocumentKinds.ElevationCertificate => "Elevation certificate",
		DocumentKinds.Permit => "Permit",
		_ => "Other",
	};

	/// <summary>The values read from an elevation certificate (stored as JSON), shortened; null when there are none or they can't be read.</summary>
	private static IReadOnlyDictionary<string, string> DocumentFields(string json)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return null;
		}
		try
		{
			Dictionary<string, string> fields = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
			return fields == null || fields.Count == 0 ? null
				: fields.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).OrderBy(kv => kv.Key, StringComparer.Ordinal)
					.ToDictionary(kv => Describe.Shorten(kv.Key, 40), kv => Describe.Shorten(kv.Value.Trim(), 200));
		}
		catch (JsonException)
		{
			return null;
		}
	}

	/// <summary>"BP-0002 Second-story addition", as the Improvements sheet names a project.</summary>
	private static string ProjectTitle(ImprovementProjectRecord p)
	{
		string text = string.Join(" ", new[] { p.PermitNumber, p.Description }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
		return text.Length == 0 ? "(no permit or description)" : Describe.Shorten(text, 120);
	}
}
