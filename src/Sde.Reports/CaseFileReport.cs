using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Improvements;
using Sde.Core.Records;
using Sde.Data;
using Sde.Reports.Internal;
using Column = Sde.Reports.Internal.Column;

namespace Sde.Reports;

/// <summary>What to print in a case file besides the structure's records.</summary>
public sealed class CaseFileReportOptions
{
	/// <summary>Print the timeline (on by default).</summary>
	public bool IncludeTimeline { get; init; } = true;

	/// <summary>Also list field changes in the timeline (off by default: edit history can run to many pages).</summary>
	public bool IncludeEdits { get; init; }

	/// <summary>The time printed in the footer (default: now).</summary>
	public DateTime? GeneratedOn { get; init; }
}

/// <summary>
/// One structure's case file as a PDF, across every event: who and where it is and its current result, then its
/// assessments, compliance, letters, appeals and bids, improvements and SI determinations, documents (names only)
/// and a dated timeline. Sections with nothing in them are left out. Same fonts and styling as the other reports.
/// </summary>
public static class CaseFileReport
{
	public const string Title = "Case File";

	public const string AssessmentsTitle = "Assessments";
	public const string ComplianceTitle = "Compliance";
	public const string LettersTitle = "Letters";
	public const string AppealsTitle = "Appeals and bids";
	public const string ImprovementsTitle = "Improvements and SI determinations";
	public const string DocumentsTitle = "Documents";
	public const string TimelineTitle = "Timeline";

	/// <summary>Writes the report as PDF to <paramref name="pdfOutput"/> (left open).</summary>
	public static void Render(CaseFile caseFile, ReferenceData reference, Stream pdfOutput, CaseFileReportOptions options = null)
	{
		ArgumentNullException.ThrowIfNull(pdfOutput);
		Document doc = Build(caseFile, reference, options ?? new CaseFileReportOptions());
		PdfDocumentRenderer renderer = new PdfDocumentRenderer { Document = doc };
		renderer.RenderDocument();
		renderer.PdfDocument.Save(pdfOutput, false);
	}

	/// <summary>Writes the report to a PDF file (overwriting it). The file is only replaced once rendering succeeded.</summary>
	public static void RenderToFile(CaseFile caseFile, ReferenceData reference, string path, CaseFileReportOptions options = null)
	{
		ArgumentException.ThrowIfNullOrEmpty(path);
		string dir = Path.GetDirectoryName(Path.GetFullPath(path));
		if (!string.IsNullOrEmpty(dir))
		{
			Directory.CreateDirectory(dir);
		}
		using MemoryStream buffer = new MemoryStream();
		Render(caseFile, reference, buffer, options);
		File.WriteAllBytes(path, buffer.ToArray());
	}

	internal static Document Build(CaseFile cf, ReferenceData reference, CaseFileReportOptions options)
	{
		ArgumentNullException.ThrowIfNull(cf);
		ArgumentNullException.ThrowIfNull(reference);
		if (cf.Property == null)
		{
			throw new ArgumentException("The case file has no structure.", nameof(cf));
		}
		DateTime printed = options.GeneratedOn ?? DateTime.Now;
		AssessmentView v = new AssessmentView(new AssessmentFile { Property = cf.Property, Assessment = cf.Latest?.Assessment ?? new AssessmentRecord() }, reference);

		Document doc = Theme.NewDocument(Title, v.StreetAddress);
		Section sec = Kit.NewSection(doc, landscape: false, Title, printed, footerCenter: null, restartPageNumbers: false);

		Hero(sec, v, cf);
		Kit.Gap(sec, 2);
		Kit.Panels(sec,
			new Panel("Structure", new[]
			{
				new Field("Owner's Name", v.OwnerName),
				new Field("Street Address", v.StreetAddress),
				new Field("City, State Zip", v.CityStateZip),
				new Field("County/Parish", v.County),
				new Field("Parcel #", v.P.ParcelNumber),
			}, 1.25, 2.25),
			new Panel("Community", new[]
			{
				new Field("NFIP Community Name", v.P.NFIPCommunityName),
				new Field("NFIP Community ID #", v.P.NFIPCID),
				new Field("Firm Zone", v.Name(v.P.FIRMZone)),
				new Field("BFE", Fmt.Number2(v.P.BFE)),
				new Field("Year of Construction", v.P.YearOfConstruction?.ToString()),
			}, 1.25, 2.25));

		Assessments(sec, cf);
		Compliance(sec, cf);
		Letters(sec, cf);
		Appeals(sec, cf);
		Improvements(sec, cf);
		Documents(sec, cf);
		if (options.IncludeTimeline)
		{
			Timeline(sec, cf, options.IncludeEdits);
		}
		return doc;
	}

	/// <summary>The structure on the left; the current result (with the staff gauge) in a colored panel on the right.</summary>
	private static void Hero(Section sec, AssessmentView v, CaseFile cf)
	{
		Table t = Kit.NewTable(sec, new[] { 3.6, 0.15, 3.55 });
		Row row = t.AddRow();
		row.VerticalAlignment = VerticalAlignment.Center;
		row.TopPadding = Unit.FromPoint(8);
		row.BottomPadding = Unit.FromPoint(8);

		Cell a = row.Cells[0];
		Paragraph street = a.AddParagraph(string.IsNullOrEmpty(v.StreetAddress) ? "(No street address)" : v.StreetAddress);
		street.Format.Font.Size = 15;
		street.Format.Font.Bold = true;
		Paragraph city = a.AddParagraph(v.CityStateZip);
		city.Format.Font.Size = 9.5;
		city.Format.SpaceBefore = 1;
		Paragraph meta = a.AddParagraph();
		meta.Style = Theme.LabelStyle;
		meta.Format.SpaceBefore = 6;
		AddMeta(meta, "Owner", v.OwnerName);
		AddMeta(meta, "Parcel", v.P.ParcelNumber);
		AddMeta(meta, "NFIP Community", Fmt.Join(" ", v.P.NFIPCID, v.P.NFIPCommunityName));
		int trashed = cf.Assessments.Count(x => x.IsTrashed);
		int inUse = cf.Assessments.Count - trashed;
		AddMeta(meta, "Assessments", Count(inUse, "assessment") + (trashed > 0 ? $", {trashed} in Recently deleted" : ""));

		CaseFileAssessment latest = cf.Latest;
		Determination tone = latest == null ? Determination.Other : v.Determination;
		Color ink = Theme.ToneInk(tone);
		Cell d = row.Cells[2];
		d.Shading.Color = Theme.ToneFill(tone);
		d.Borders.Left.Width = 3;
		d.Borders.Left.Color = ink;
		Paragraph cap = d.AddParagraph("Current Result");
		cap.Format.Font.Size = 7;
		cap.Format.Font.Bold = true;
		cap.Format.Font.Color = ink;
		decimal? percent = latest?.Assessment.PercentDamage;
		Paragraph pct = d.AddParagraph(percent is decimal ? Fmt.Percent1(percent) : latest == null ? "No assessment" : "Not available");
		pct.Format.Font.Size = percent is null ? 14 : 24;
		pct.Format.Font.Bold = true;
		pct.Format.Font.Color = ink;
		Paragraph msg = d.AddParagraph(latest == null ? "Nothing has been assessed yet" : string.IsNullOrEmpty(v.DamageMessage) ? "Not calculated" : v.DamageMessage);
		msg.Format.Font.Size = 9;
		msg.Format.Font.Bold = true;
		msg.Format.Font.Color = ink;
		if (latest != null && tone != Determination.Other && percent is decimal gauge)
		{
			StaffGauge.Add(d, gauge, 50m, 190, 5);
		}
		if (latest != null)
		{
			Paragraph when = d.AddParagraph(Fmt.Join(". ",
				latest.Assessment.DateofInspection is DateTime di ? "Inspected " + Fmt.Date(di) : "No inspection date",
				latest.Event?.Label,
				AssessmentStatuses.Label(latest.Status.Value)));
			when.Style = Theme.LabelStyle;
			when.Format.SpaceBefore = 4;
		}
		if (cf.Cumulative is CumulativeResult cr && cr.Status == CumulativeStatus.Evaluated)
		{
			string period = cr.LookbackYears == 0 ? "all events" : cr.LookbackYears == 1 ? "1 year" : cr.LookbackYears + " years";
			string total = cr.TotalPercent is decimal tp ? Fmt.Percent1(tp) : "not worked out";
			string verdict = cr.TotalPercent is null ? "" : cr.MeetsThreshold ? ", meets the threshold" : ", below the threshold";
			Paragraph cum = d.AddParagraph($"Cumulative over {period}: {total}{verdict}");
			cum.Style = Theme.LabelStyle;
			cum.Format.Font.Bold = true;
			cum.Format.Font.Color = ink;
			cum.Format.SpaceBefore = 2;
		}
		foreach (Paragraph para in d.Elements.OfType<Paragraph>())
		{
			para.Format.LeftIndent = Unit.FromPoint(10);
			para.Format.RightIndent = Unit.FromPoint(5);
		}
	}

	private static void Assessments(Section sec, CaseFile cf)
	{
		if (cf.Assessments.Count == 0)
		{
			return;
		}
		Column[] columns =
		{
			new Column("Event", 1.75),
			new Column("Inspected", 0.8),
			new Column("Inspector", 1.25),
			new Column("Status", 1.15),
			new Column("% Damaged", 0.75, ParagraphAlignment.Right),
			new Column("Determination", 1.6),
		};
		Kit.Gap(sec, 4);
		Kit.Data(sec, AssessmentsTitle, columns, cf.Assessments.Select(a => new[]
		{
			a.Event?.Label ?? "No event",
			Fmt.Date(a.Assessment.DateofInspection),
			a.Assessment.InspectorName ?? "",
			a.IsTrashed ? "In Recently deleted" : AssessmentStatuses.Label(a.Status.Value),
			Fmt.Percent1(a.Assessment.PercentDamage),
			a.Assessment.DamageMessage ?? "",
		}));
	}

	private static void Compliance(Section sec, CaseFile cf)
	{
		if (cf.ComplianceCases.Count == 0)
		{
			return;
		}
		Column[] columns =
		{
			new Column("Kind", 0.45),
			new Column("Path", 1.7),
			new Column("Permit", 0.95),
			new Column("Deadline", 0.8),
			new Column("ICC", 0.95),
			new Column("EC", 0.8),
			new Column("Final insp.", 0.8),
			new Column("Status", 0.85),
		};
		Kit.Gap(sec, 4);
		Kit.Data(sec, ComplianceTitle, columns, cf.ComplianceCases.Select(x => x.Case).Select(c => new[]
		{
			c.Kind ?? "",
			c.Path == null ? "Not chosen" : CompliancePaths.Label(c.Path),
			c.PermitNumber ?? "",
			Fmt.Date(c.Deadline),
			IccStatuses.Label(c.IccStatus) + (c.IccAmount is decimal m ? " " + m.ToString("C0", Fmt.Us) : ""),
			Fmt.Date(c.EcReceivedOn),
			Fmt.Date(c.FinalInspectionOn),
			c.IsOpen ? "Open" : "Closed " + Fmt.Date(c.ClosedOn),
		}));
	}

	private static void Letters(Section sec, CaseFile cf)
	{
		if (cf.Letters.Count == 0)
		{
			return;
		}
		Dictionary<Guid, CaseFileAssessment> byId = cf.Assessments.ToDictionary(a => a.Assessment.AssessmentID);
		Column[] columns =
		{
			new Column("Letter", 2.4),
			new Column("For the assessment of", 2.5),
			new Column("Written", 1.2),
			new Column("Sent", 1.2),
		};
		Kit.Gap(sec, 4);
		Kit.Data(sec, LettersTitle, columns, cf.Letters.Select(l => new[]
		{
			string.IsNullOrWhiteSpace(l.Kind) ? "Letter" : l.Kind.Trim(),
			byId.TryGetValue(l.AssessmentID, out CaseFileAssessment a) ? AssessmentLabel(a) : "",
			Fmt.Date(l.GeneratedOn),
			l.SentOn is DateTime s ? Fmt.Date(s) : "Not sent",
		}));
	}

	private static void Appeals(Section sec, CaseFile cf)
	{
		if (cf.Appeals.Count == 0)
		{
			return;
		}
		Dictionary<Guid, CaseFileAssessment> byId = cf.Assessments.ToDictionary(a => a.Assessment.AssessmentID);
		List<string[]> rows = new();
		foreach (CaseFileAppeal ap in cf.Appeals)
		{
			string of = byId.TryGetValue(ap.AssessmentId, out CaseFileAssessment a) ? AssessmentLabel(a) : "";
			if (ap.Appeal is AppealRecord r)
			{
				string outcome = r.Outcome ?? AppealOutcomes.Pending;
				rows.Add(new[] { of, "Appeal", Fmt.Date(r.ReceivedOn), r.Basis ?? "", r.DecidedOn is DateTime dd && outcome != AppealOutcomes.Pending ? $"{outcome} {Fmt.Date(dd)}" : outcome });
			}
			foreach (ContractorBidRecord b in ap.Bids)
			{
				rows.Add(new[] { of, "Bid: " + (string.IsNullOrWhiteSpace(b.Contractor) ? "(no contractor named)" : b.Contractor.Trim()), Fmt.Date(b.BidDate), b.Notes ?? "", Fmt.Money(b.Amount) });
			}
		}
		Column[] columns =
		{
			new Column("For the assessment of", 1.95),
			new Column("What", 1.45),
			new Column("Date", 0.8),
			new Column("Basis or notes", 2.0),
			new Column("Outcome or amount", 1.1),
		};
		Kit.Gap(sec, 4);
		Kit.Data(sec, AppealsTitle, columns, rows);
	}

	private static void Improvements(Section sec, CaseFile cf)
	{
		if (cf.Improvements.Count == 0 && cf.SiDeterminations.Count == 0)
		{
			return;
		}
		Kit.Gap(sec, 4);
		bool titled = false;
		if (cf.Improvements.Count > 0)
		{
			Column[] columns =
			{
				new Column("Permit #", 1.0),
				new Column("Description", 2.55),
				new Column("Date", 0.8),
				new Column("Cost", 1.0, ParagraphAlignment.Right),
				new Column("Counted", 1.95),
			};
			Kit.Data(sec, ImprovementsTitle, columns, cf.Improvements.Select(p => new[]
			{
				p.PermitNumber ?? "",
				p.Description ?? "",
				Fmt.Date(p.ProjectDate),
				Fmt.Money(p.Cost),
				p.Included ? "Yes" : "No" + (string.IsNullOrWhiteSpace(p.ExclusionReason) ? "" : ": " + p.ExclusionReason.Trim()),
			}));
			titled = true;
		}
		if (cf.SiDeterminations.Count > 0)
		{
			if (titled)
			{
				Kit.Gap(sec, 4);
			}
			Column[] columns =
			{
				new Column("SI determined", 1.0),
				new Column("Cost counted", 1.1, ParagraphAlignment.Right),
				new Column("Market value", 1.1, ParagraphAlignment.Right),
				new Column("Percent", 0.7, ParagraphAlignment.Right),
				new Column("Result", 2.1),
				new Column("By", 1.3),
			};
			Kit.Data(sec, titled ? null : ImprovementsTitle, columns, cf.SiDeterminations.Select(d => new[]
			{
				Fmt.Date(d.DeterminedOn),
				Fmt.Money(d.CumulativeCost),
				Fmt.Money(d.MarketValue),
				Fmt.Percent1(d.Percent),
				d.Result ?? "",
				d.DeterminedBy ?? "",
			}));
		}
	}

	private static void Documents(Section sec, CaseFile cf)
	{
		if (cf.Documents.Count == 0)
		{
			return;
		}
		Column[] columns =
		{
			new Column("Document", 4.3),
			new Column("Kind", 1.8),
			new Column("Added", 1.2),
		};
		Kit.Gap(sec, 4);
		Kit.Data(sec, DocumentsTitle, columns, cf.Documents.Select(d => new[]
		{
			DocumentName(d),
			DocumentKind(d.Kind),
			Fmt.Date(d.AddedOn),
		}));
	}

	private static void Timeline(Section sec, CaseFile cf, bool includeEdits)
	{
		List<CaseFileEntry> entries = cf.Timeline.Where(e => includeEdits || !e.IsEdit).ToList();
		if (entries.Count == 0)
		{
			return;
		}
		Column[] columns =
		{
			new Column("Date", 1.15),
			new Column("What", 2.55),
			new Column("Detail", 2.45),
			new Column("Who", 1.15),
		};
		Kit.Gap(sec, 4);
		Kit.Data(sec, TimelineTitle, columns, entries.Select(e => new[]
		{
			e.IsDateOnly ? Fmt.Date(e.When) : e.When.ToString("MM/dd/yyyy HH:mm", Fmt.Us),
			e.Title ?? "",
			Shorten(e.Detail, 240),
			e.Who ?? "",
		}));
		if (!includeEdits && cf.Timeline.Any(e => e.IsEdit))
		{
			int edits = cf.Timeline.Count(e => e.IsEdit);
			Kit.Note(sec, $"{Count(edits, "field change")} in the edit history {(edits == 1 ? "is" : "are")} not listed here.");
		}
	}

	/// <summary>"09/21/2026, September flood" (the assessment's inspection date and event).</summary>
	private static string AssessmentLabel(CaseFileAssessment a) => Fmt.Join(", ",
		a.Assessment.DateofInspection is DateTime d ? Fmt.Date(d) : "No inspection date",
		a.Event?.Label,
		a.IsTrashed ? "in Recently deleted" : null);

	/// <summary>The document's title, or the name of the file it was added from (never where it is stored).</summary>
	private static string DocumentName(DocumentRecord d)
	{
		string name = !string.IsNullOrWhiteSpace(d.Title) ? d.Title : Path.GetFileName(d.OriginalName ?? "");
		return string.IsNullOrWhiteSpace(name) ? "Document" : name.Trim();
	}

	private static string DocumentKind(string kind) => kind switch
	{
		DocumentKinds.ElevationCertificate => "Elevation certificate",
		DocumentKinds.Permit => "Permit",
		null or "" or DocumentKinds.Other => "Other",
		_ => kind,
	};

	private static void AddMeta(Paragraph p, string label, string value)
	{
		if (p.Elements.Count > 0)
		{
			p.AddLineBreak();
		}
		p.AddText(label + "  ");
		FormattedText f = p.AddFormattedText(string.IsNullOrEmpty(value) ? "–" : value);
		f.Color = Theme.Ink;
		f.Size = 8;
	}

	private static string Count(int n, string noun) => n == 1 ? "1 " + noun : $"{n:N0} {noun}s";

	private static string Shorten(string text, int max) =>
		string.IsNullOrEmpty(text) ? "" : text.Length <= max ? text : text[..max].TrimEnd() + "...";
}
