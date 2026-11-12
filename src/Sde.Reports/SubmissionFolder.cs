using System.Globalization;
using Sde.Core.Assessments;
using Sde.Core.Events;
using Sde.Core.Exchange;
using Sde.Data;
using Sde.Reports.Internal;

namespace Sde.Reports;

/// <summary>What goes into a submission folder.</summary>
public sealed class SubmissionOptions
{
	/// <summary>The report written for each assessment: <see cref="ReportKind.Summary"/> (one page) or <see cref="ReportKind.Detail"/>.</summary>
	public ReportKind PerAssessmentReport { get; init; } = ReportKind.Summary;

	public bool IncludePhotos { get; init; } = true;

	/// <summary>Also include assessments whose result is "Data Entry Incomplete" (left out by default).</summary>
	public bool IncludeIncomplete { get; init; }

	/// <summary>
	/// Also write a copy of each marked-up photo with its markup drawn on ("Marked-up photos"). FEMA's SDE package always
	/// keeps the original photos.
	/// </summary>
	public bool IncludeMarkedUpCopies { get; init; }

	/// <summary>Also copy the owner letters written for these assessments into "Letters" (off by default).</summary>
	public bool IncludeLetters { get; init; }

	/// <summary>Also copy the documents kept with these structures into "Documents" (off by default).</summary>
	public bool IncludeDocuments { get; init; }

	/// <summary>Also write the compliance report of these structures' cases, "Compliance report.pdf" (off by default).</summary>
	public bool IncludeComplianceReport { get; init; }

	/// <summary>Also write the event summary of these assessments, "Event summary.pdf" (off by default).</summary>
	public bool IncludeEventSummary { get; init; }

	/// <summary>Also write each structure's case file into "Case files" (off by default).</summary>
	public bool IncludeCaseFiles { get; init; }

	/// <summary>The time used in the folder name and printed on the reports (default: now).</summary>
	public DateTime? GeneratedOn { get; init; }
}

/// <summary>How far <see cref="SubmissionFolder.Write"/> has got: <paramref name="Done"/> of <paramref name="Total"/> steps.</summary>
public sealed record SubmissionProgress(int Done, int Total, string Step);

public sealed class SubmissionResult
{
	/// <summary>The folder written, e.g. ".../SDE submission 2026-10-08 1430".</summary>
	public string Folder { get; init; }

	/// <summary>Assessments included (one report each).</summary>
	public int Assessments { get; init; }

	/// <summary>Incomplete assessments left out ("123 River Street (Data Entry Incomplete)").</summary>
	public List<string> Skipped { get; init; } = new();

	/// <summary>Photo files that were missing from the attachments folder (the SDE package lists them without a file).</summary>
	public IReadOnlyList<string> MissingPhotos { get; init; } = Array.Empty<string>();

	/// <summary>
	/// Letters and documents that could not be copied (the file is missing, or is a link), in plain English. The rest of
	/// the folder is written all the same.
	/// </summary>
	public List<string> Warnings { get; init; } = new();
}

/// <summary>
/// One folder with everything a community sends after an event: a PDF report per assessment, the structure &amp;
/// percent damaged list and the community report, the FEMA SDE package (SDE_1: Export.json + photos, which FEMA's
/// SDE and the state can import), and results.csv (one row per assessment, for entering determinations back into
/// the system the inspections came from). Reports are named after the record ID when the assessment was imported.
/// </summary>
public static class SubmissionFolder
{
	public const string ReportsFolder = "Reports";
	public const string PercentDamagedFile = "Percent damaged list.pdf";
	public const string CommunityFile = "Community report.pdf";
	public const string ResultsFile = "results.csv";
	public const string MarkedUpFolder = "Marked-up photos";
	public const string LettersFolder = "Letters";
	public const string DocumentsFolder = "Documents";
	public const string CaseFilesFolder = "Case files";
	public const string ComplianceFile = "Compliance report.pdf";
	public const string EventSummaryFile = "Event summary.pdf";

	/// <summary>
	/// Writes a new "SDE submission &lt;date time&gt;" folder inside <paramref name="parentFolder"/>. Assessments are
	/// written in the order given. Cancelling (or a failure) removes the partly written folder.
	/// </summary>
	public static SubmissionResult Write(SdeStore store, IReadOnlyList<Guid> assessmentIds, string parentFolder, SubmissionOptions options = null,
		IProgress<SubmissionProgress> progress = null, CancellationToken ct = default)
	{
		ArgumentNullException.ThrowIfNull(store);
		ArgumentException.ThrowIfNullOrEmpty(parentFolder);
		options ??= new SubmissionOptions();
		if (options.PerAssessmentReport is not (ReportKind.Summary or ReportKind.Detail))
		{
			throw new ArgumentException("The per-assessment report must be Summary or Detail.", nameof(options));
		}
		DateTime now = options.GeneratedOn ?? DateTime.Now;

		List<AssessmentFile> all = store.LoadAssessments(assessmentIds);
		List<AssessmentFile> files = new();
		List<string> skipped = new();
		foreach (AssessmentFile f in all)
		{
			if (!options.IncludeIncomplete && IsIncomplete(f))
			{
				skipped.Add($"{new AssessmentView(f, store.Reference).StreetAddress.OrIfEmpty("(no address)")} ({f.Assessment.DamageMessage ?? "Data Entry Incomplete"})");
				continue;
			}
			files.Add(f);
		}
		if (files.Count == 0)
		{
			throw new InvalidOperationException(all.Count == 0
				? "There are no assessments to put in the folder."
				: "Every assessment is still incomplete (Data Entry Incomplete). Finish them, or include incomplete assessments.");
		}

		string folder = UniqueFolder(Path.Combine(parentFolder, "SDE submission " + now.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture)));
		Directory.CreateDirectory(folder);
		try
		{
			int extras = new[] { options.IncludeLetters, options.IncludeDocuments, options.IncludeComplianceReport, options.IncludeEventSummary, options.IncludeCaseFiles }.Count(x => x);
			int total = files.Count + 4 + extras, done = 0;
			void Step(string what)
			{
				ct.ThrowIfCancellationRequested();
				progress?.Report(new SubmissionProgress(done, total, what));
			}

			// One report per assessment.
			Dictionary<Guid, ExternalLinkRecord> links = store.GetExternalLinks().ToDictionary(l => l.AssessmentID);
			List<string> names = ReportNames(files, links, store);
			string reports = Path.Combine(folder, ReportsFolder);
			Directory.CreateDirectory(reports);
			for (int i = 0; i < files.Count; i++)
			{
				Step($"Report {i + 1} of {files.Count}: {Path.GetFileNameWithoutExtension(names[i])}");
				ReportGenerator.RenderToFile(Request(options.PerAssessmentReport, new[] { files[i] }, options, store, now), store.Reference, Path.Combine(reports, names[i]));
				done++;
			}

			// The two lists, for everything in the folder.
			Step("Structure & percent damaged list");
			ReportGenerator.RenderToFile(Request(ReportKind.PercentDamaged, files, options, store, now), store.Reference, Path.Combine(folder, PercentDamagedFile));
			done++;
			Step("Community report");
			ReportGenerator.RenderToFile(Request(ReportKind.Community, files, options, store, now), store.Reference, Path.Combine(folder, CommunityFile));
			done++;

			// The FEMA SDE package.
			Step("FEMA SDE package (SDE_1)");
			ExchangeService exchange = new ExchangeService(store);
			SdeDataset data = exchange.ReadDataset(files.Select(f => f.Assessment.AssessmentID));
			(_, IReadOnlyList<string> missing) = exchange.Export(data, folder);
			done++;

			Step("results.csv");
			ResultsCsv.Write(Path.Combine(folder, ResultsFile), files, links, names, store.Reference);
			done++;

			if (options.IncludeMarkedUpCopies)
			{
				WriteMarkedUpCopies(store, files, Path.Combine(folder, MarkedUpFolder));
			}

			List<string> warnings = new();
			if (options.IncludeLetters)
			{
				Step("Letters");
				CopyLetters(store, files, names, Path.Combine(folder, LettersFolder), warnings);
				done++;
			}
			if (options.IncludeDocuments)
			{
				Step("Documents");
				CopyDocuments(store, files, Path.Combine(folder, DocumentsFolder), warnings);
				done++;
			}
			if (options.IncludeComplianceReport)
			{
				Step("Compliance report");
				WriteComplianceReport(store, files, Path.Combine(folder, ComplianceFile), now);
				done++;
			}
			if (options.IncludeEventSummary)
			{
				Step("Event summary");
				WriteEventSummary(store, files, Path.Combine(folder, EventSummaryFile), now);
				done++;
			}
			if (options.IncludeCaseFiles)
			{
				Step("Case files");
				WriteCaseFiles(store, files, Path.Combine(folder, CaseFilesFolder), now);
				done++;
			}
			progress?.Report(new SubmissionProgress(done, total, "Done"));

			return new SubmissionResult { Folder = folder, Assessments = files.Count, Skipped = skipped, MissingPhotos = missing, Warnings = warnings };
		}
		catch
		{
			TryDelete(folder);
			throw;
		}
	}

	/// <summary>"Data Entry Incomplete" (or never calculated); a "damage undetermined" result counts as complete.</summary>
	public static bool IsIncomplete(AssessmentFile f) => f.Assessment.DamageMessage is null or "" or "Data Entry Incomplete";

	private static ReportRequest Request(ReportKind kind, IReadOnlyList<AssessmentFile> files, SubmissionOptions options, SdeStore store, DateTime now) => new()
	{
		Kind = kind,
		Assessments = files,
		IncludePhotos = options.IncludePhotos,
		PhotosFolder = store.AttachmentsFolder,
		GeneratedOn = now,
		PhotoMarkups = Markups(store, files),
	};

	/// <summary>The markup of these assessments' photos, by file name.</summary>
	public static Dictionary<string, string> Markups(SdeStore store, IEnumerable<AssessmentFile> files)
	{
		Dictionary<string, string> all = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (AssessmentFile f in files)
		{
			foreach (KeyValuePair<string, string> m in store.GetPhotoMarkups(f.Assessment.AssessmentID))
			{
				all[m.Key] = m.Value;
			}
		}
		return all;
	}

	/// <summary>A JPEG copy of each marked-up photo with its marks drawn on, named after the stored photo.</summary>
	private static void WriteMarkedUpCopies(SdeStore store, IReadOnlyList<AssessmentFile> files, string folder)
	{
		foreach (KeyValuePair<string, string> m in Markups(store, files))
		{
			Sde.Core.Photos.PhotoMarkup markup = Sde.Core.Photos.PhotoMarkup.Parse(m.Value);
			string photo = Path.Combine(store.AttachmentsFolder, m.Key);
			if (markup == null || markup.IsEmpty || !File.Exists(photo))
			{
				continue;
			}
			Directory.CreateDirectory(folder);
			PhotoMarkupRenderer.WriteMarkedUpCopy(photo, markup, Path.Combine(folder, Path.GetFileNameWithoutExtension(m.Key) + " (marked up).jpg"));
		}
	}

	/// <summary>
	/// Copies the letters written for these assessments, named after the assessment's report and the letter's kind
	/// ("123 River Street – Substantial damage.docx"). A letter whose file is gone, or is a link, is a warning.
	/// </summary>
	private static void CopyLetters(SdeStore store, IReadOnlyList<AssessmentFile> files, IReadOnlyList<string> names, string folder, List<string> warnings)
	{
		HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
		for (int i = 0; i < files.Count; i++)
		{
			string report = Path.GetFileNameWithoutExtension(names[i]);
			foreach (LetterLogRecord l in store.GetLetters(files[i].Assessment.AssessmentID).OrderBy(x => x.GeneratedOn))
			{
				string kind = string.IsNullOrWhiteSpace(l.Kind) ? "Letter" : l.Kind.Trim();
				string what = $"The {kind.ToLowerInvariant()} letter for {report}";
				string source = string.IsNullOrWhiteSpace(l.FileName) || !Path.IsPathRooted(l.FileName) ? null : l.FileName;
				CopySafely(source, folder, $"{report} – {kind}", used, what, warnings);
			}
		}
	}

	/// <summary>
	/// Copies the documents kept with these structures (those of the structure itself or of an assessment in the
	/// folder), named after the structure and the document's title. A missing file, or a link, is a warning.
	/// </summary>
	private static void CopyDocuments(SdeStore store, IReadOnlyList<AssessmentFile> files, string folder, List<string> warnings)
	{
		HashSet<Guid> assessments = files.Select(f => f.Assessment.AssessmentID).ToHashSet();
		HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
		foreach (AssessmentFile f in files.GroupBy(f => f.Property.PropertyID).Select(g => g.First()))
		{
			string address = new AssessmentView(f, store.Reference).StreetAddress.OrIfEmpty("No address");
			foreach (DocumentRecord d in store.GetDocuments(f.Property.PropertyID).Where(d => d.AssessmentID is not Guid a || assessments.Contains(a)).OrderBy(d => d.AddedOn))
			{
				string title = string.IsNullOrWhiteSpace(d.Title) ? Path.GetFileNameWithoutExtension(d.OriginalName ?? "") : d.Title.Trim();
				string what = $"The document \"{title.OrIfEmpty("Document")}\" of {address}";
				CopySafely(string.IsNullOrWhiteSpace(d.FileName) ? null : store.DocumentPath(d), folder, $"{address} – {title.OrIfEmpty("Document")}", used, what, warnings);
			}
		}
	}

	/// <summary>
	/// Copies one file into <paramref name="folder"/> as <paramref name="baseName"/> (sanitised, " (2)" when taken) with
	/// the source's extension. Links are never followed or copied; a missing file is a warning, not a failure.
	/// </summary>
	private static void CopySafely(string source, string folder, string baseName, HashSet<string> used, string what, List<string> warnings)
	{
		if (source == null || !File.Exists(source) && !IsLink(source))
		{
			warnings.Add($"{what} wasn't copied: its file is missing.");
			return;
		}
		if (IsLink(source))
		{
			warnings.Add($"{what} wasn't copied: its file is a link, and links aren't followed.");
			return;
		}
		string extension = Path.GetExtension(source).ToLowerInvariant();
		extension = extension.Length is > 1 and <= 10 && extension.Skip(1).All(char.IsAsciiLetterOrDigit) ? extension : "";
		string name = Sanitize(baseName);
		string unique = name;
		for (int n = 2; !used.Add(unique + extension); n++)
		{
			unique = $"{name} ({n})";
		}
		Directory.CreateDirectory(folder);
		File.Copy(source, Path.Combine(folder, unique + extension), overwrite: false);
	}

	/// <summary>True when the path is a symbolic link (or another reparse point).</summary>
	private static bool IsLink(string path)
	{
		if (string.IsNullOrEmpty(path))
		{
			return false;
		}
		FileInfo info = new FileInfo(path);
		return info.LinkTarget != null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint));
	}

	/// <summary>The compliance cases of the structures in the folder (a substantial damage case only when its assessment is in it).</summary>
	private static void WriteComplianceReport(SdeStore store, IReadOnlyList<AssessmentFile> files, string path, DateTime now)
	{
		HashSet<Guid> structures = files.Select(f => f.Property.PropertyID).ToHashSet();
		HashSet<Guid> assessments = files.Select(f => f.Assessment.AssessmentID).ToHashSet();
		List<ComplianceCaseRecord> cases = store.GetActiveComplianceCases()
			.Where(c => structures.Contains(c.PropertyID) && (c.AssessmentID is not Guid a || assessments.Contains(a))).ToList();
		ComplianceReport.RenderToFile(ComplianceReport.Rows(store, now, cases), new ComplianceReportOptions { GeneratedOn = now, EventName = OneEvent(store, files)?.Label }, path);
	}

	/// <summary>The event summary of the assessments in the folder, named after their event when they all share one.</summary>
	private static void WriteEventSummary(SdeStore store, IReadOnlyList<AssessmentFile> files, string path, DateTime now)
	{
		EventRecord e = OneEvent(store, files);
		EventSummary summary = EventSummary.Build(files, null, e?.Label, store.Reference, details: e?.ToDetails());
		EventSummaryReport.RenderToFile(summary, new EventSummaryOptions { GeneratedOn = now }, path);
	}

	/// <summary>The event every assessment in the folder belongs to, or null when they don't all share one.</summary>
	private static EventRecord OneEvent(SdeStore store, IReadOnlyList<AssessmentFile> files)
	{
		Dictionary<Guid, Guid> memberships = store.GetEventMemberships();
		List<Guid?> events = files.Select(f => memberships.TryGetValue(f.Assessment.AssessmentID, out Guid id) ? id : (Guid?)null).Distinct().ToList();
		return events.Count == 1 && events[0] is Guid only ? store.GetEvent(only) : null;
	}

	/// <summary>One case file per structure in the folder, named after its address ("123 River Street.pdf").</summary>
	private static void WriteCaseFiles(SdeStore store, IReadOnlyList<AssessmentFile> files, string folder, DateTime now)
	{
		Directory.CreateDirectory(folder);
		HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
		foreach (AssessmentFile f in files.GroupBy(f => f.Property.PropertyID).Select(g => g.First()))
		{
			string name = Sanitize(new AssessmentView(f, store.Reference).StreetAddress.OrIfEmpty("No address"));
			string unique = name;
			for (int n = 2; !used.Add(unique); n++)
			{
				unique = $"{name} ({n})";
			}
			CaseFileReport.RenderToFile(store.GetCaseFile(f.Property.PropertyID), store.Reference, Path.Combine(folder, unique + ".pdf"),
				new CaseFileReportOptions { GeneratedOn = now });
		}
	}

	/// <summary>
	/// "BLD-2026-00123 – 123 River Street.pdf" (the record ID when the assessment was imported), with the inspection date added
	/// when a structure has several assessments in the folder, and " (2)" for any name that's still taken.
	/// </summary>
	internal static List<string> ReportNames(IReadOnlyList<AssessmentFile> files, IReadOnlyDictionary<Guid, ExternalLinkRecord> links, SdeStore store)
	{
		HashSet<Guid> repeated = files.GroupBy(f => f.Property.PropertyID).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
		HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
		List<string> names = new();
		foreach (AssessmentFile f in files)
		{
			string address = new AssessmentView(f, store.Reference).StreetAddress.OrIfEmpty("No address");
			string name = links.TryGetValue(f.Assessment.AssessmentID, out ExternalLinkRecord link) ? $"{link.RecordId} – {address}" : address;
			if (repeated.Contains(f.Property.PropertyID))
			{
				name += f.Assessment.DateofInspection is DateTime d ? $" (inspected {d:yyyy-MM-dd})" : " (no inspection date)";
			}
			name = Sanitize(name);
			string unique = name;
			for (int n = 2; !used.Add(unique); n++)
			{
				unique = $"{name} ({n})";
			}
			names.Add(unique + ".pdf");
		}
		return names;
	}

	/// <summary>A file name that works in Finder (no "/" or ":"), at most 120 characters.</summary>
	internal static string Sanitize(string name)
	{
		string s = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Replace(':', '-').Trim().TrimStart('.');
		return s.Length > 120 ? s[..120].TrimEnd() : s.Length == 0 ? "Report" : s;
	}

	internal static string UniqueFolder(string path)
	{
		string unique = path;
		for (int n = 2; Directory.Exists(unique) || File.Exists(unique); n++)
		{
			unique = $"{path} ({n})";
		}
		return unique;
	}

	internal static void TryDelete(string folder)
	{
		try
		{
			Directory.Delete(folder, recursive: true);
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	private static string OrIfEmpty(this string s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s;
}
