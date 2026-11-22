using System.Buffers;
using System.Globalization;
using System.Text;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Data;

namespace Sde.Reports.Internal;

/// <summary>
/// results.csv in a submission folder: one row per assessment with its determination, for entering results back into
/// the system the inspections came from (a permit system, a spreadsheet). Source and Record ID are the record the
/// assessment was imported from, blank when it was entered in SDE. Values come from <see cref="AssessmentView"/>, so they are the ones the PDFs print;
/// money is written as plain numbers so spreadsheets read it as numbers. UTF-8 with a byte-order mark, for Excel.
/// </summary>
internal static class ResultsCsv
{
	public static readonly string[] Headers =
	{
		"Source", "Record ID", "Inspection ID", "Address", "City", "State", "ZIP", "Parcel number", "Owner", "Structure type", "Inspection date",
		"Inspector", "Value of building based on", "Value of building", "Cost of repairs based on", "Cost of repairs", "Percent damaged",
		"Determination", "Report",
	};

	public static void Write(string path, IReadOnlyList<AssessmentFile> files, IReadOnlyDictionary<Guid, ExternalLinkRecord> links, IReadOnlyList<string> reportNames, ReferenceData reference)
	{
		StringBuilder sb = new StringBuilder();
		Line(sb, Headers);
		for (int i = 0; i < files.Count; i++)
		{
			AssessmentView v = new AssessmentView(files[i], reference);
			links.TryGetValue(v.A.AssessmentID, out ExternalLinkRecord link);
			Line(sb, new[]
			{
				link?.Source ?? "",
				link?.RecordId ?? "",
				link?.InspectionId ?? "",
				v.StreetAddress,
				v.City,
				reference.StateAbbreviation(v.P.StateID) ?? "",
				v.Zip,
				v.P.ParcelNumber ?? "",
				Fmt.Join(" ", v.P.OwnerFirstName, v.P.OwnerLastName),
				v.IsResidential ? "Residential" : v.P.StructureTypeID == 2 ? "Non-Residential" : "",
				v.A.DateofInspection?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
				v.A.InspectorName ?? "",
				v.BasisValue,
				Amount(v.ValueOfBuilding),
				v.BasisCost,
				Amount(v.CostOfRepair),
				v.A.PercentDamage?.ToString("0.0", CultureInfo.InvariantCulture) ?? "",
				v.DamageMessage,
				Path.Combine(SubmissionFolder.ReportsFolder, reportNames[i]),
			});
		}
		File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
	}

	/// <summary>"$1,234.57" → "1234.57"; "Not Provided" → "".</summary>
	internal static string Amount(string printed) =>
		decimal.TryParse(printed, NumberStyles.Currency, Fmt.Us, out decimal d) ? d.ToString("0.00", CultureInfo.InvariantCulture) : "";

	private static readonly SearchValues<char> CsvSpecial = SearchValues.Create(",\"\r\n");

	internal static void Line(StringBuilder sb, IEnumerable<string> values)
	{
		bool first = true;
		foreach (string v in values)
		{
			if (!first)
			{
				sb.Append(',');
			}
			first = false;
			AppendQuoted(sb, v);
		}
		sb.Append("\r\n");
	}

	private static void AppendQuoted(StringBuilder sb, string value)
	{
		string s = value ?? "";
		bool prefix = s.Length > 0 && IsFormulaLead(s[0]) && !decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out _);
		if (s.AsSpan().IndexOfAny(CsvSpecial) >= 0)
		{
			sb.Append('"');
			if (prefix)
			{
				sb.Append('\'');
			}
			for (int i = 0; i < s.Length; i++)
			{
				char ch = s[i];
				if (ch == '"')
				{
					sb.Append("\"\"");
				}
				else
				{
					sb.Append(ch);
				}
			}
			sb.Append('"');
		}
		else
		{
			if (prefix)
			{
				sb.Append('\'');
			}
			sb.Append(s);
		}
	}

	private static bool IsFormulaLead(char c) => c is '=' or '+' or '-' or '@';

	/// <summary>RFC 4180 quoting; a leading = + - @ is prefixed with ' so a spreadsheet doesn't run it as a formula.</summary>
	internal static string Quote(string value)
	{
		string s = value ?? "";
		if (s.Length > 0 && IsFormulaLead(s[0]) && !decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
		{
			s = "'" + s;
		}
		return s.AsSpan().IndexOfAny(CsvSpecial) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
	}
}
