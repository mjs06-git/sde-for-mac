using System.Text;

namespace Sde.Data.Infrastructure;

/// <summary>A CSV cell; <see cref="Quoted"/> distinguishes "" (empty text) from an empty unquoted cell (NULL in mdb-export output).</summary>
internal readonly record struct CsvCell(string Text, bool Quoted)
{
	public bool IsNull => !Quoted && Text.Length == 0;
}

/// <summary>RFC 4180 reader for mdb-export output (quoted fields, doubled quotes, line breaks inside quotes).</summary>
internal static class CsvReader
{
	public static IEnumerable<List<CsvCell>> Read(TextReader reader)
	{
		char[] buffer = new char[4096];
		int pos = 0;
		int len = 0;
		int prevCols = 8;
		List<CsvCell> row = new List<CsvCell>(prevCols);
		StringBuilder cell = new StringBuilder(64);
		bool quoted = false;
		bool inQuotes = false;
		bool pendingQuote = false;
		bool any = false;
		while (true)
		{
			if (pos >= len)
			{
				len = reader.Read(buffer, 0, buffer.Length);
				pos = 0;
				if (len == 0)
				{
					break;
				}
			}
			if (pendingQuote)
			{
				pendingQuote = false;
				if (buffer[pos] == '"')
				{
					cell.Append('"');
					pos++;
					continue;
				}
				inQuotes = false;
			}
			if (inQuotes)
			{
				int start = pos;
				while (pos < len && buffer[pos] != '"')
				{
					pos++;
				}
				if (pos > start)
				{
					cell.Append(buffer, start, pos - start);
				}
				if (pos < len)
				{
					pos++;
					if (pos < len)
					{
						if (buffer[pos] == '"')
						{
							cell.Append('"');
							pos++;
						}
						else
						{
							inQuotes = false;
						}
					}
					else
					{
						pendingQuote = true;
					}
				}
				continue;
			}
			int textStart = pos;
			while (pos < len)
			{
				char ch = buffer[pos];
				if (ch == '"' || ch == ',' || ch == '\r' || ch == '\n')
				{
					break;
				}
				pos++;
			}
			if (pos > textStart)
			{
				cell.Append(buffer, textStart, pos - textStart);
				any = true;
			}
			if (pos >= len)
			{
				continue;
			}
			char c = buffer[pos++];
			any = true;
			switch (c)
			{
			case '"':
				inQuotes = true;
				quoted = true;
				break;
			case ',':
				row.Add(new CsvCell(cell.ToString(), quoted));
				cell.Clear();
				quoted = false;
				break;
			case '\r':
				break;
			case '\n':
				row.Add(new CsvCell(cell.ToString(), quoted));
				prevCols = row.Count;
				yield return row;
				row = new List<CsvCell>(prevCols);
				cell.Clear();
				quoted = false;
				any = false;
				break;
			}
		}
		if (any)
		{
			row.Add(new CsvCell(cell.ToString(), quoted));
			yield return row;
		}
	}
}
