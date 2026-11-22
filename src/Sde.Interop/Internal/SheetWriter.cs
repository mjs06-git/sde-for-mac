using ClosedXML.Excel;

namespace Sde.Interop.Internal;

/// <summary>Common layout for the workbooks SDE writes: a bold, frozen, wrapped header row and sensible widths.</summary>
internal static class SheetWriter
{
	/// <summary>Writes the header row and text rows (blank strings leave the cell empty), returning the longest data length per column.</summary>
	public static int[] WriteText(IXLWorksheet ws, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
	{
		int[] longest = new int[headers.Count];
		for (int c = 0; c < headers.Count; c++)
		{
			ws.Cell(1, c + 1).SetValue(headers[c]);
		}
		int r = 2;
		foreach (IReadOnlyList<string> row in rows)
		{
			for (int c = 0; c < row.Count; c++)
			{
				string val = row[c];
				if (!string.IsNullOrEmpty(val))
				{
					// Text, as the original exported (DevExpress TextExportMode.Text).
					ws.Cell(r, c + 1).SetValue(val);
					if (c < longest.Length && val.Length > longest[c])
					{
						longest[c] = val.Length;
					}
				}
			}
			r++;
		}
		return longest;
	}

	/// <summary>Bold, wrapped, frozen header row; each column as wide as its longest value (header capped so it wraps).</summary>
	public static void Finish(IXLWorksheet ws, int columns, double minWidth = 8, double maxWidth = 60, int headerCap = 24, int[] longestData = null)
	{
		IXLRow header = ws.Row(1);
		header.Style.Font.Bold = true;
		header.Style.Alignment.WrapText = true;
		header.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
		ws.SheetView.FreezeRows(1);

		int[] longest = longestData;
		if (longest == null)
		{
			longest = new int[columns];
			foreach (IXLCell cell in ws.CellsUsed(XLCellsUsedOptions.Contents))
			{
				int rowNum = cell.Address.RowNumber;
				if (rowNum < 2)
				{
					continue;
				}
				int colIdx = cell.Address.ColumnNumber - 1;
				if (colIdx >= 0 && colIdx < columns)
				{
					int len = cell.GetFormattedString().Length;
					if (len > longest[colIdx])
					{
						longest[colIdx] = len;
					}
				}
			}
		}
		for (int c = 1; c <= columns; c++)
		{
			int headerLength = ws.Cell(1, c).GetString().Length;
			double width = Math.Max(longest[c - 1], Math.Min(headerLength, headerCap)) + 2;
			ws.Column(c).Width = Math.Clamp(width, minWidth, maxWidth);
		}
	}
}
