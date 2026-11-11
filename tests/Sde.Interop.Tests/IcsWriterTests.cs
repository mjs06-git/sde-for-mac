using System.Text;

namespace Sde.Interop.Tests;

/// <summary>The calendar (.ics) writer: escaping, folding, all-day dates and stable identifiers.</summary>
public class IcsWriterTests
{
	private static readonly DateTime Now = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

	/// <summary>A tiny reader for the tests: unfolds lines, then returns each VEVENT as property name to unescaped value.</summary>
	private static List<Dictionary<string, string>> Parse(string ics)
	{
		string unfolded = ics.Replace("\r\n ", "");
		List<Dictionary<string, string>> events = new List<Dictionary<string, string>>();
		Dictionary<string, string> current = null;
		foreach (string line in unfolded.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
		{
			if (line == "BEGIN:VEVENT")
			{
				current = new Dictionary<string, string>();
			}
			else if (line == "END:VEVENT")
			{
				events.Add(current);
				current = null;
			}
			else if (current != null)
			{
				int colon = line.IndexOf(':');
				string name = line[..colon];
				int semi = name.IndexOf(';');
				current[semi >= 0 ? name[..semi] : name] = Unescape(line[(colon + 1)..]);
			}
		}
		return events;
	}

	private static string Unescape(string value)
	{
		StringBuilder sb = new StringBuilder();
		for (int i = 0; i < value.Length; i++)
		{
			if (value[i] == '\\' && i + 1 < value.Length)
			{
				i++;
				sb.Append(value[i] == 'n' || value[i] == 'N' ? '\n' : value[i]);
			}
			else
			{
				sb.Append(value[i]);
			}
		}
		return sb.ToString();
	}

	private static AgendaItem Item(DateTime date, string title = "Deadline", AgendaKind kind = AgendaKind.ComplianceDeadline, string id = "42")
		=> new AgendaItem { Date = date, Title = title, Kind = kind, RecordId = id };

	[Fact]
	public void Escape_HandlesCommasSemicolonsBackslashesAndNewlines()
	{
		Assert.Equal("a\\,b\\;c\\\\d\\ne\\nf\\ng", IcsWriter.Escape("a,b;c\\d\ne\r\nf\rg"));
	}

	[Fact]
	public void Build_EscapedTextRoundTrips()
	{
		AgendaItem item = Item(new DateTime(2026, 5, 1), "Fix roof, doors; gutters");
		item.Notes = "Line one\nLine two\\end";
		item.Address = "12 Main St, Springfield";
		Dictionary<string, string> e = Parse(IcsWriter.Build(new[] { item }, Now)).Single();
		Assert.Equal("Fix roof, doors; gutters", e["SUMMARY"]);
		Assert.Equal("Line one\nLine two\\end", e["DESCRIPTION"]);
		Assert.Equal("12 Main St, Springfield", e["LOCATION"]);
	}

	[Fact]
	public void Fold_NeverExceeds75Bytes_AndNeverSplitsACharacter()
	{
		string text = string.Concat(Enumerable.Repeat("Überschwemmung 水害 🌊 ", 12));
		AgendaItem item = Item(new DateTime(2026, 5, 1), text);
		string ics = IcsWriter.Build(new[] { item }, Now);
		foreach (string line in ics.Split("\r\n"))
		{
			Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, line);
		}
		Assert.Equal(text, Parse(ics).Single()["SUMMARY"]);
		Assert.DoesNotContain('�', ics);
	}

	[Fact]
	public void Build_UsesCrlfEverywhere()
	{
		string ics = IcsWriter.Build(new[] { Item(new DateTime(2026, 5, 1)) }, Now);
		Assert.EndsWith("\r\n", ics);
		Assert.DoesNotContain("\n", ics.Replace("\r\n", ""));
	}

	[Theory]
	[InlineData(2026, 1, 31, "20260131", "20260201")]
	[InlineData(2026, 2, 28, "20260228", "20260301")]
	[InlineData(2028, 2, 28, "20280228", "20280229")]
	[InlineData(2026, 12, 31, "20261231", "20270101")]
	public void Build_AllDayEnd_IsTheNextDay(int y, int m, int d, string start, string end)
	{
		string ics = IcsWriter.Build(new[] { Item(new DateTime(y, m, d, 17, 30, 0)) }, Now);
		Assert.Contains("DTSTART;VALUE=DATE:" + start + "\r\n", ics);
		Assert.Contains("DTEND;VALUE=DATE:" + end + "\r\n", ics);
	}

	[Fact]
	public void Uid_IsStable_AndDiffersByKindAndRecord()
	{
		AgendaItem a = Item(new DateTime(2026, 5, 1), "A");
		AgendaItem sameRecordLaterDate = Item(new DateTime(2026, 6, 1), "B");
		Assert.Equal(IcsWriter.Uid(a), IcsWriter.Uid(sameRecordLaterDate));
		Assert.NotEqual(IcsWriter.Uid(a), IcsWriter.Uid(Item(new DateTime(2026, 5, 1), "A", AgendaKind.AppealDecision)));
		Assert.NotEqual(IcsWriter.Uid(a), IcsWriter.Uid(Item(new DateTime(2026, 5, 1), "A", id: "43")));
		Assert.Equal(Parse(IcsWriter.Build(new[] { a }, Now)).Single()["UID"], Parse(IcsWriter.Build(new[] { a }, Now.AddDays(9))).Single()["UID"]);
	}

	[Fact]
	public void Build_StampsTheInjectedClock()
	{
		Dictionary<string, string> e = Parse(IcsWriter.Build(new[] { Item(new DateTime(2026, 5, 1)) }, Now)).Single();
		Assert.Equal("20260304T050607Z", e["DTSTAMP"]);
	}

	[Fact]
	public void Build_EmptyList_IsAValidEmptyCalendar()
	{
		string ics = IcsWriter.Build(Array.Empty<AgendaItem>(), Now);
		Assert.Equal("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:" + IcsWriter.ProductId + "\r\nCALSCALE:GREGORIAN\r\nEND:VCALENDAR\r\n", ics);
	}

	[Fact]
	public void Write_UsesUtf8WithoutByteOrderMark()
	{
		string path = Path.Combine(Path.GetTempPath(), "ics-" + Guid.NewGuid().ToString("N") + ".ics");
		try
		{
			IcsWriter.Write(path, new[] { Item(new DateTime(2026, 5, 1), "Café") }, Now);
			byte[] bytes = File.ReadAllBytes(path);
			Assert.Equal((byte)'B', bytes[0]);
			Assert.Contains("Café", Encoding.UTF8.GetString(bytes));
		}
		finally
		{
			File.Delete(path);
		}
	}
}
