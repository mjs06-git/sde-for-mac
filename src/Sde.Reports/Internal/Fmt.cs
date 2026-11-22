using System.Globalization;

namespace Sde.Reports.Internal;

/// <summary>
/// Value formatting used by the RDLC layouts. Always en-US (SDE is a U.S. program), whatever the Mac's locale.
/// Null prints as an empty string, as an empty RDLC textbox did.
/// </summary>
internal static class Fmt
{
	public static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	/// <summary>Format "c2" / FormatCurrency(x, 2): $1,234.56.</summary>
	public static string Money(decimal? v) => v is decimal d ? d.ToString("C2", Us) : "";

	/// <summary>Currency from FEMA's string-typed money columns (ElementPercentage.ItemCost / DamageValues).</summary>
	public static string Money(string s) => ParseDecimal(s) is decimal d ? Money(d) : "";

	/// <summary>Format "#0.0 \%" applied to a value that is already a percentage (75.0 → "75.0%").</summary>
	public static string Percent1(decimal? v) => v is decimal d ? d.ToString("#0.0", Us) + "%" : "";

	/// <summary>Format "#0.00 \%" (the detail report's depreciation percentage).</summary>
	public static string Percent2(decimal? v) => v is decimal d ? d.ToString("#0.00", Us) + "%" : "";

	/// <summary>Format "#,##0.00" (BFE, flood depths).</summary>
	public static string Number2(decimal? v) => v is decimal d ? d.ToString("#,##0.00", Us) : "";

	/// <summary>A plain number without trailing zeros (quantities, square footage, coordinates, multipliers).</summary>
	public static string Plain(decimal? v) => v is decimal d ? d.ToString("#,##0.############", Us) : "";

	/// <summary>Like <see cref="Plain(decimal?)"/> but without grouping (latitude/longitude, multipliers).</summary>
	public static string Raw(decimal? v) => v is decimal d ? d.ToString("0.############", Us) : "";

	/// <summary>Format "MM/dd/yyyy".</summary>
	public static string Date(DateTime? v) => v is DateTime d ? d.ToString("MM/dd/yyyy", Us) : "";

	/// <summary>Format "D" (the footer's print date): Wednesday, October 7, 2026.</summary>
	public static string LongDate(DateTime d) => d.ToString("dddd, MMMM d, yyyy", Us);

	/// <summary>
	/// Port of the RDLC code.FormatZipCode: 5 characters print as they are, otherwise the value is formatted
	/// "#####-####". The original converted to Double first (a ZIP+4 already containing "-" broke it); here
	/// only a 9-digit value is reformatted and anything else prints unchanged.
	/// </summary>
	public static string Zip(string zip)
	{
		if (string.IsNullOrEmpty(zip))
		{
			return zip ?? "";
		}
		ReadOnlySpan<char> z = zip.AsSpan().Trim();
		if (z.Length == 9)
		{
			bool allDigits = true;
			for (int i = 0; i < 9; i++)
			{
				if (!char.IsAsciiDigit(z[i]))
				{
					allDigits = false;
					break;
				}
			}
			if (allDigits)
			{
				return string.Concat(z.Slice(0, 5), "-", z.Slice(5));
			}
		}
		return z.Length == zip.Length ? zip : z.ToString();
	}

	/// <summary>"Last, First" as the RDLC owner textboxes concatenated it, without a dangling comma when one part is empty.</summary>
	public static string LastFirst(string last, string first)
	{
		last = last?.Trim();
		first = first?.Trim();
		if (string.IsNullOrEmpty(last))
		{
			return first ?? "";
		}
		return string.IsNullOrEmpty(first) ? last : last + ", " + first;
	}

	/// <summary>Joins two non-empty parts with a separator without allocating a params array.</summary>
	public static string Join(string separator, string a, string b)
	{
		ReadOnlySpan<char> sa = a.AsSpan().Trim();
		ReadOnlySpan<char> sb = b.AsSpan().Trim();
		if (sa.IsEmpty)
		{
			return sb.IsEmpty ? "" : (sb.Length == b.Length ? b : sb.ToString());
		}
		if (sb.IsEmpty)
		{
			return sa.Length == a.Length ? a : sa.ToString();
		}
		return string.Concat(sa, separator, sb);
	}

	/// <summary>Joins three non-empty parts with a separator without allocating a params array.</summary>
	public static string Join(string separator, string a, string b, string c) =>
		Join(separator, Join(separator, a, b), c);

	/// <summary>Joins the non-empty parts with a separator.</summary>
	public static string Join(string separator, params string[] parts)
	{
		if (parts == null || parts.Length == 0)
		{
			return "";
		}
		string first = null;
		System.Text.StringBuilder sb = null;
		for (int i = 0; i < parts.Length; i++)
		{
			string part = parts[i];
			if (string.IsNullOrWhiteSpace(part))
			{
				continue;
			}
			ReadOnlySpan<char> trimmed = part.AsSpan().Trim();
			if (first == null)
			{
				first = trimmed.Length == part.Length ? part : trimmed.ToString();
			}
			else
			{
				if (sb == null)
				{
					sb = new System.Text.StringBuilder(first.Length + separator.Length + trimmed.Length);
					sb.Append(first);
				}
				sb.Append(separator);
				sb.Append(trimmed);
			}
		}
		return sb != null ? sb.ToString() : first ?? "";
	}

	public static decimal? ParseDecimal(string s)
	{
		if (string.IsNullOrWhiteSpace(s))
		{
			return null;
		}
		ReadOnlySpan<char> span = s.AsSpan().Trim();
		if (span.IndexOf('$') < 0 && span.IndexOf(',') < 0)
		{
			return decimal.TryParse(span, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal direct) ? direct : null;
		}
		return decimal.TryParse(s.Replace("$", "").Replace(",", "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d) ? d : null;
	}
}
