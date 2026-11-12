using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Sde.Interop.Internal;

namespace Sde.Interop;

/// <summary>
/// Writes the rows and columns it is given, and nothing else, as a CSV file or an Excel workbook. This is for exporting
/// a list as it appears on screen. It is separate from <see cref="ExcelExport"/>, which writes FEMA's fixed 218 columns.
/// A cell is a string, a number, a date, a yes/no value or null (empty). Text that a spreadsheet could read as a
/// formula (it starts with =, +, -, @, a tab or a carriage return) is written so that it stays plain text.
/// </summary>
public static class ListExport
{
	private const string DateFormat = "yyyy-mm-dd";
	private const string DateTimeFormat = "yyyy-mm-dd hh:mm";

	/// <summary>The most characters an Excel sheet name may have.</summary>
	private const int MaxSheetNameLength = 31;

	/// <summary>Builds CSV text: RFC 4180 quoting, CRLF line ends, dates as yyyy-MM-dd, numbers in plain invariant form.</summary>
	public static string BuildCsv(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object>> rows)
	{
		StringBuilder sb = new StringBuilder();
		AddCsvLine(sb, headers.Select(h => CsvField(GuardText(h))));
		foreach (IReadOnlyList<object> row in rows ?? Enumerable.Empty<IReadOnlyList<object>>())
		{
			CheckWidth(headers, row);
			List<string> fields = new List<string>(headers.Count);
			for (int c = 0; c < headers.Count; c++)
			{
				fields.Add(CsvField(c < row.Count ? CsvText(row[c]) : ""));
			}
			AddCsvLine(sb, fields);
		}
		return sb.ToString();
	}

	/// <summary>Writes a CSV file as UTF-8 with a byte-order mark, so Excel reads accented characters correctly.</summary>
	public static void WriteCsv(string path, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object>> rows)
	{
		File.WriteAllText(path, BuildCsv(headers, rows), new UTF8Encoding(true));
	}

	/// <summary>
	/// Writes an .xlsx workbook with one sheet: a bold frozen header row, numbers and dates stored as real numbers and
	/// dates, and each column as wide as its contents up to a limit.
	/// </summary>
	public static void WriteXlsx(string path, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object>> rows, string sheetName = "List")
	{
		using XLWorkbook wb = new XLWorkbook();
		IXLWorksheet ws = wb.Worksheets.Add(SafeSheetName(sheetName));
		for (int c = 0; c < headers.Count; c++)
		{
			SetText(ws.Cell(1, c + 1), headers[c]);
		}
		int r = 2;
		foreach (IReadOnlyList<object> row in rows ?? Enumerable.Empty<IReadOnlyList<object>>())
		{
			CheckWidth(headers, row);
			for (int c = 0; c < row.Count; c++)
			{
				SetCell(ws.Cell(r, c + 1), row[c]);
			}
			r++;
		}
		SheetWriter.Finish(ws, Math.Max(headers.Count, 1));
		wb.SaveAs(path);
	}

	/// <summary>
	/// Returns the text unchanged unless a spreadsheet could take it for a formula, in which case it gets an apostrophe in
	/// front. Used for CSV, where there is no way to mark a cell as text.
	/// </summary>
	public static string GuardText(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return text ?? "";
		}
		return text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + text : text;
	}

	private static void CheckWidth(IReadOnlyList<string> headers, IReadOnlyList<object> row)
	{
		if (row.Count > headers.Count)
		{
			throw new ArgumentException($"A row has {row.Count} cells but there are only {headers.Count} column headers.");
		}
	}

	private static void AddCsvLine(StringBuilder sb, IEnumerable<string> fields)
	{
		sb.AppendJoin(',', fields).Append("\r\n");
	}

	private static string CsvField(string text)
	{
		if (text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
		{
			return text;
		}
		return "\"" + text.Replace("\"", "\"\"") + "\"";
	}

	private static string CsvText(object value)
	{
		switch (value)
		{
			case null:
				return "";
			case string s:
				return GuardText(s);
			case DateTime d:
				return d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
			case DateOnly d:
				return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
			case bool b:
				return b ? "TRUE" : "FALSE";
			case double x:
				return x.ToString("R", CultureInfo.InvariantCulture);
			case float x:
				return x.ToString("R", CultureInfo.InvariantCulture);
			case IFormattable f:
				return f.ToString(null, CultureInfo.InvariantCulture);
			default:
				return GuardText(value.ToString());
		}
	}

	private static void SetText(IXLCell cell, string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return;
		}
		cell.SetValue(text);
		cell.Style.NumberFormat.Format = "@";
		if (GuardText(text) != text)
		{
			// Excel's "quote prefix": the cell is text even when edited, the same as typing an apostrophe first.
			cell.Style.IncludeQuotePrefix = true;
		}
	}

	private static void SetCell(IXLCell cell, object value)
	{
		switch (value)
		{
			case null:
				return;
			case string s:
				SetText(cell, s);
				return;
			case DateTime d:
				cell.SetValue(d);
				cell.Style.NumberFormat.Format = d.TimeOfDay == TimeSpan.Zero ? DateFormat : DateTimeFormat;
				return;
			case DateOnly d:
				cell.SetValue(d.ToDateTime(TimeOnly.MinValue));
				cell.Style.NumberFormat.Format = DateFormat;
				return;
			case bool b:
				cell.SetValue(b);
				return;
			case sbyte or byte or short or ushort or int or uint or long or ulong:
				cell.SetValue(Convert.ToDouble(value, CultureInfo.InvariantCulture));
				cell.Style.NumberFormat.Format = "0";
				return;
			case float or double or decimal:
				cell.SetValue(Convert.ToDouble(value, CultureInfo.InvariantCulture));
				return;
			default:
				SetText(cell, value.ToString());
				return;
		}
	}

	private static string SafeSheetName(string name)
	{
		StringBuilder sb = new StringBuilder();
		foreach (char c in name ?? "")
		{
			sb.Append(c is ':' or '\\' or '/' or '?' or '*' or '[' or ']' ? ' ' : c);
		}
		string clean = sb.ToString().Trim().Trim('\'');
		if (clean.Length > MaxSheetNameLength)
		{
			clean = clean[..MaxSheetNameLength];
		}
		return clean.Length == 0 ? "List" : clean;
	}
}
