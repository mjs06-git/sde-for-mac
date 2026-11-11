using System.Globalization;
using System.Text;

namespace Sde.Interop;

/// <summary>What kind of date an <see cref="AgendaItem"/> is. Part of the item's stable identifier.</summary>
public enum AgendaKind
{
	/// <summary>The date a compliance case must be resolved by.</summary>
	ComplianceDeadline,

	/// <summary>The date an appeal was received.</summary>
	AppealReceived,

	/// <summary>The date an appeal was decided.</summary>
	AppealDecision,

	/// <summary>The date a letter was sent to an owner.</summary>
	LetterSent,

	/// <summary>The date of a final inspection.</summary>
	FinalInspection,

	/// <summary>The first day of an event.</summary>
	EventStart,

	/// <summary>The last day of an event.</summary>
	EventEnd,
}

/// <summary>One all-day entry for a calendar file: a date with a title and optional notes and place.</summary>
public sealed class AgendaItem
{
	/// <summary>The day the entry falls on. Only the date is used; the entry lasts all day.</summary>
	public DateTime Date { get; set; }

	/// <summary>The short heading shown in the calendar.</summary>
	public string Title { get; set; }

	/// <summary>Longer text shown when the entry is opened. May contain line breaks.</summary>
	public string Notes { get; set; }

	/// <summary>What the date is for.</summary>
	public AgendaKind Kind { get; set; }

	/// <summary>
	/// The id of the record the date belongs to (a case, an appeal, an event). Together with <see cref="Kind"/> it makes
	/// the entry's identifier, so exporting again updates the entry in Calendar instead of adding a second one.
	/// </summary>
	public string RecordId { get; set; }

	/// <summary>The street address, shown as the entry's location. Optional.</summary>
	public string Address { get; set; }
}

/// <summary>
/// Writes an iCalendar file (.ics, RFC 5545) of all-day entries, so compliance deadlines and similar dates can be added
/// to Calendar. Lines end with CRLF, are folded at 75 bytes without splitting a character, and the file is UTF-8
/// without a byte-order mark.
/// </summary>
public static class IcsWriter
{
	/// <summary>The product identifier written to every file.</summary>
	public const string ProductId = "-//SDE for Mac//Agenda//EN";

	/// <summary>The longest a line may be, in bytes, before it is folded (RFC 5545 section 3.1).</summary>
	public const int MaxLineBytes = 75;

	private const string Crlf = "\r\n";

	/// <summary>Builds the calendar text. <paramref name="now"/> is the time stamped on each entry; leave it out to use the current time.</summary>
	public static string Build(IEnumerable<AgendaItem> items, DateTime? now = null)
	{
		DateTime stamp = (now ?? DateTime.UtcNow).ToUniversalTime();
		string stampText = stamp.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
		StringBuilder sb = new StringBuilder();
		AddLine(sb, "BEGIN:VCALENDAR");
		AddLine(sb, "VERSION:2.0");
		AddLine(sb, "PRODID:" + ProductId);
		AddLine(sb, "CALSCALE:GREGORIAN");
		foreach (AgendaItem item in items ?? Enumerable.Empty<AgendaItem>())
		{
			DateTime day = item.Date.Date;
			AddLine(sb, "BEGIN:VEVENT");
			AddLine(sb, "UID:" + Uid(item));
			AddLine(sb, "DTSTAMP:" + stampText);
			AddLine(sb, "DTSTART;VALUE=DATE:" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
			AddLine(sb, "DTEND;VALUE=DATE:" + day.AddDays(1).ToString("yyyyMMdd", CultureInfo.InvariantCulture));
			AddLine(sb, "SUMMARY:" + Escape(item.Title));
			if (!string.IsNullOrEmpty(item.Notes))
			{
				AddLine(sb, "DESCRIPTION:" + Escape(item.Notes));
			}
			if (!string.IsNullOrEmpty(item.Address))
			{
				AddLine(sb, "LOCATION:" + Escape(item.Address));
			}
			AddLine(sb, "TRANSP:TRANSPARENT");
			AddLine(sb, "END:VEVENT");
		}
		AddLine(sb, "END:VCALENDAR");
		return sb.ToString();
	}

	/// <summary>Writes the calendar to a file as UTF-8 without a byte-order mark.</summary>
	public static void Write(string path, IEnumerable<AgendaItem> items, DateTime? now = null)
	{
		File.WriteAllText(path, Build(items, now), new UTF8Encoding(false));
	}

	/// <summary>The stable identifier of an entry: its kind and record id, so the same record always gets the same one.</summary>
	public static string Uid(AgendaItem item)
	{
		string id = string.IsNullOrEmpty(item.RecordId) ? "none" : item.RecordId;
		return $"{item.Kind}-{id}@sde-for-mac".ToLowerInvariant().Replace(' ', '-');
	}

	/// <summary>Escapes backslashes, semicolons, commas and line breaks in a text value (RFC 5545 section 3.3.11).</summary>
	public static string Escape(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return "";
		}
		StringBuilder sb = new StringBuilder(text.Length + 8);
		for (int i = 0; i < text.Length; i++)
		{
			char c = text[i];
			switch (c)
			{
				case '\\': sb.Append("\\\\"); break;
				case ';': sb.Append("\\;"); break;
				case ',': sb.Append("\\,"); break;
				case '\r':
					sb.Append("\\n");
					if (i + 1 < text.Length && text[i + 1] == '\n')
					{
						i++;
					}
					break;
				case '\n': sb.Append("\\n"); break;
				default: sb.Append(c); break;
			}
		}
		return sb.ToString();
	}

	/// <summary>Folds a line at 75 bytes. Continuation lines start with one space, which counts toward their 75 bytes.</summary>
	public static string Fold(string line)
	{
		StringBuilder sb = new StringBuilder();
		int bytes = 0;
		for (int i = 0; i < line.Length; i++)
		{
			int length = char.IsHighSurrogate(line[i]) && i + 1 < line.Length ? 2 : 1;
			string piece = line.Substring(i, length);
			int size = Encoding.UTF8.GetByteCount(piece);
			if (bytes + size > MaxLineBytes)
			{
				sb.Append(Crlf).Append(' ');
				bytes = 1;
			}
			sb.Append(piece);
			bytes += size;
			i += length - 1;
		}
		return sb.ToString();
	}

	private static void AddLine(StringBuilder sb, string line)
	{
		sb.Append(Fold(line)).Append(Crlf);
	}
}
