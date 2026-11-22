using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Sde.Interop.ElevationCertificates;

/// <summary>
/// Works around a PDFsharp 6.2.4 limit when reading a filled form. When a PDF is saved by adding an update to the end
/// (an "incremental update", which is how Adobe Reader saves a filled-in form), the update lists only the objects that
/// changed. If the PDF is encrypted and the newest update doesn't list the encryption dictionary again, PDFsharp
/// can't open it: it throws "Indirect reference without value" while checking each update's /Encrypt entry. FEMA's
/// fillable elevation certificate is encrypted, so a certificate filled in and saved in Reader can hit this. The
/// repair adds, in memory only, one more small update that lists the encryption dictionary where it already is, as
/// Adobe's own tools do in some of their updates. Nothing else is touched, and the person's file is never changed.
/// </summary>
internal static class PdfUpdateRepair
{
	/// <summary>Larger files aren't repaired (an elevation certificate is a few megabytes).</summary>
	private const int MaxBytes = 200 * 1024 * 1024;

	private static readonly Regex StartXrefRegex = new Regex(@"^startxref\s+(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
	private static readonly Regex EncryptRegex = new Regex(@"/Encrypt\s+(\d+)\s+(\d+)\s+R", RegexOptions.Compiled | RegexOptions.CultureInvariant);
	private static readonly Regex RootRegex = new Regex(@"/Root\s+(\d+\s+\d+\s+R)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
	private static readonly Regex SizeRegex = new Regex(@"/Size\s+(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
	private static readonly Regex InfoRegex = new Regex(@"/Info\s+(\d+\s+\d+\s+R)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
	private static readonly Regex FilterStandardRegex = new Regex(@"/Filter\s*/Standard", RegexOptions.Compiled | RegexOptions.CultureInvariant);
	private static readonly Regex ObjHeaderRegex = new Regex(@"^\d+\s+\d+\s+obj\s*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	/// <summary>The PDF with the extra update appended, or null when it isn't an encrypted PDF this repair applies to.</summary>
	public static byte[] RelistEncryption(byte[] pdf)
	{
		if (pdf == null || pdf.Length > MaxBytes)
		{
			return null;
		}
		string text = Encoding.Latin1.GetString(pdf);
		int startxref = text.LastIndexOf("startxref", StringComparison.Ordinal);
		if (startxref < 0)
		{
			return null;
		}
		Match last = StartXrefRegex.Match(text.Substring(startxref));
		if (!last.Success || !long.TryParse(last.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long lastXref) || lastXref >= text.Length)
		{
			return null;
		}
		string trailer = TrailerDictionary(text, (int)lastXref);
		if (trailer == null)
		{
			return null;
		}
		Match encrypt = EncryptRegex.Match(trailer);
		Match root = RootRegex.Match(trailer);
		Match size = SizeRegex.Match(trailer);
		if (!encrypt.Success || !root.Success || !size.Success)
		{
			return null;
		}
		Match info = InfoRegex.Match(trailer);
		string id = ArrayAfter(trailer, "/ID");
		int encryptNumber = int.Parse(encrypt.Groups[1].Value, CultureInfo.InvariantCulture);
		int encryptGeneration = int.Parse(encrypt.Groups[2].Value, CultureInfo.InvariantCulture);
		int xrefNumber = int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture);

		// The encryption dictionary as last written: the last "n g obj" for it, which must be a standard security handler.
		Match definition = Regex.Match(text, $@"(?<![0-9]){encryptNumber}\s+{encryptGeneration}\s+obj\b", RegexOptions.RightToLeft | RegexOptions.CultureInvariant);
		if (!definition.Success)
		{
			return null;
		}
		int encryptOffset = definition.Index;
		string head = text.Substring(encryptOffset, Math.Min(2000, text.Length - encryptOffset));
		if (!FilterStandardRegex.IsMatch(head))
		{
			return null;
		}

		long xrefOffset = pdf.Length + 1;
		byte[] entry1 = Entry(encryptOffset, encryptGeneration);
		byte[] entry2 = Entry(xrefOffset, 0);
		int entriesLength = entry1.Length + entry2.Length;
		StringBuilder dict = new StringBuilder(256);
		dict.Append(CultureInfo.InvariantCulture, $"<</Type/XRef/Size {xrefNumber + 1}/Index[{encryptNumber} 1 {xrefNumber} 1]/W[1 4 2]");
		dict.Append("/Root ").Append(root.Groups[1].Value);
		if (info.Success)
		{
			dict.Append("/Info ").Append(info.Groups[1].Value);
		}
		dict.Append(CultureInfo.InvariantCulture, $"/Encrypt {encryptNumber} {encryptGeneration} R");
		if (id != null)
		{
			dict.Append("/ID").Append(id);
		}
		dict.Append(CultureInfo.InvariantCulture, $"/Prev {lastXref}/Length {entriesLength}>>");
		byte[] prefix = Encoding.Latin1.GetBytes($"\n{xrefNumber} 0 obj\n{dict}\nstream\n");
		byte[] suffix = Encoding.Latin1.GetBytes($"\nendstream\nendobj\nstartxref\n{xrefOffset.ToString(CultureInfo.InvariantCulture)}\n%%EOF\n");
		byte[] result = new byte[pdf.Length + prefix.Length + entriesLength + suffix.Length];
		int offset = 0;
		Buffer.BlockCopy(pdf, 0, result, offset, pdf.Length);
		offset += pdf.Length;
		Buffer.BlockCopy(prefix, 0, result, offset, prefix.Length);
		offset += prefix.Length;
		Buffer.BlockCopy(entry1, 0, result, offset, entry1.Length);
		offset += entry1.Length;
		Buffer.BlockCopy(entry2, 0, result, offset, entry2.Length);
		offset += entry2.Length;
		Buffer.BlockCopy(suffix, 0, result, offset, suffix.Length);
		return result;
	}

	/// <summary>A cross-reference stream entry for an object at an offset: type 1, 4 bytes of offset, 2 of generation.</summary>
	private static byte[] Entry(long offset, int generation) => new byte[]
	{
		1, (byte)(offset >> 24), (byte)(offset >> 16), (byte)(offset >> 8), (byte)offset, (byte)(generation >> 8), (byte)generation,
	};

	/// <summary>The trailer dictionary of the update whose cross-reference starts at <paramref name="offset"/> (a table or a stream).</summary>
	private static string TrailerDictionary(string text, int offset)
	{
		int at = offset;
		while (at < text.Length && char.IsWhiteSpace(text[at]))
		{
			at++;
		}
		int open;
		if (string.CompareOrdinal(text, at, "xref", 0, 4) == 0)
		{
			int trailer = text.IndexOf("trailer", at, StringComparison.Ordinal);
			open = trailer < 0 ? -1 : text.IndexOf("<<", trailer, StringComparison.Ordinal);
		}
		else
		{
			Match obj = ObjHeaderRegex.Match(text.Substring(at, Math.Min(64, text.Length - at)));
			open = obj.Success && string.CompareOrdinal(text, at + obj.Length, "<<", 0, 2) == 0 ? at + obj.Length : -1;
		}
		if (open < 0)
		{
			return null;
		}
		int end = Balanced(text, open, '<', '>', dictionary: true);
		return end < 0 ? null : text.Substring(open, end - open);
	}

	/// <summary>The raw array after a key at the top of a dictionary ("[&lt;..&gt;&lt;..&gt;]"), or null.</summary>
	private static string ArrayAfter(string dictionary, string key)
	{
		Match m = Regex.Match(dictionary, Regex.Escape(key) + @"\s*\[");
		if (!m.Success)
		{
			return null;
		}
		int open = m.Index + m.Length - 1;
		int end = Balanced(dictionary, open, '[', ']', dictionary: false);
		return end < 0 ? null : dictionary.Substring(open, end - open);
	}

	/// <summary>
	/// The index just past the dictionary ("&lt;&lt;...&gt;&gt;") or array ("[...]") starting at <paramref name="open"/>, skipping
	/// over strings ("(...)" with escapes and nested brackets, and "&lt;hex&gt;"); -1 when it doesn't close.
	/// </summary>
	private static int Balanced(string text, int open, char openChar, char closeChar, bool dictionary)
	{
		int depth = 0;
		for (int i = open; i < text.Length; i++)
		{
			char c = text[i];
			if (c == '(')
			{
				i = SkipLiteral(text, i);
				if (i < 0)
				{
					return -1;
				}
			}
			else if (dictionary && c == '<' && i + 1 < text.Length && text[i + 1] == '<')
			{
				depth++;
				i++;
			}
			else if (dictionary && c == '>' && i + 1 < text.Length && text[i + 1] == '>')
			{
				depth--;
				i++;
				if (depth == 0)
				{
					return i + 1;
				}
			}
			else if (c == '<')
			{
				int close = text.IndexOf('>', i + 1);
				if (close < 0)
				{
					return -1;
				}
				i = close;
			}
			else if (!dictionary && c == openChar)
			{
				depth++;
			}
			else if (!dictionary && c == closeChar)
			{
				depth--;
				if (depth == 0)
				{
					return i + 1;
				}
			}
		}
		return -1;
	}

	/// <summary>The index of the ")" that closes the literal string opening at <paramref name="open"/>, or -1.</summary>
	private static int SkipLiteral(string text, int open)
	{
		int depth = 0;
		for (int i = open; i < text.Length; i++)
		{
			char c = text[i];
			if (c == '\\')
			{
				i++;
			}
			else if (c == '(')
			{
				depth++;
			}
			else if (c == ')')
			{
				depth--;
				if (depth == 0)
				{
					return i;
				}
			}
		}
		return -1;
	}
}
