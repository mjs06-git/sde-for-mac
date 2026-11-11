using System.Text;
using ClosedXML.Excel;
using Sde.Interop.Internal;

namespace Sde.Interop.Tests;

/// <summary>Exporting a list as it is shown: CSV quoting, formula-injection guard, and real numbers and dates in Excel.</summary>
public class ListExportTests
{
	private static readonly string[] Headers = { "Name", "Count", "Date" };

	private static IReadOnlyList<object> Row(params object[] cells) => cells;

	private static string TempFile(string extension) => Path.Combine(Path.GetTempPath(), "list-" + Guid.NewGuid().ToString("N") + extension);

	[Fact]
	public void BuildCsv_QuotesCommasQuotesAndLineBreaks()
	{
		string csv = ListExport.BuildCsv(Headers, new[]
		{
			Row("Smith, Jo", 3, null),
			Row("He said \"hi\"", 1.5, null),
			Row("two\nlines", 0, null),
		});
		Assert.Equal("Name,Count,Date\r\n\"Smith, Jo\",3,\r\n\"He said \"\"hi\"\"\",1.5,\r\n\"two\nlines\",0,\r\n", csv);
	}

	[Fact]
	public void BuildCsv_WritesDatesAsIsoAndNumbersInvariant()
	{
		string csv = ListExport.BuildCsv(Headers, new[] { Row("A", 1234.5m, new DateTime(2026, 7, 4)), Row("B", 0.1, new DateTime(2026, 7, 4, 13, 5, 9)) });
		Assert.Contains("A,1234.5,2026-07-04\r\n", csv);
		Assert.Contains("B,0.1,2026-07-04 13:05:09\r\n", csv);
	}

	[Theory]
	[InlineData("=1+1")]
	[InlineData("+SUM(A1)")]
	[InlineData("-2+3")]
	[InlineData("@cmd")]
	[InlineData("\tTabbed")]
	[InlineData("\rReturn")]
	public void BuildCsv_PrefixesAnApostropheOnFormulaLikeText(string text)
	{
		string csv = ListExport.BuildCsv(new[] { "H" }, new[] { Row(text) });
		string line = csv.Split("\r\n")[1];
		Assert.StartsWith("'", line.TrimStart('"'));
	}

	[Fact]
	public void BuildCsv_LeavesNegativeNumbersAndOrdinaryTextAlone()
	{
		string csv = ListExport.BuildCsv(new[] { "H", "I" }, new[] { Row(-5, "a-b") });
		Assert.Equal("H,I\r\n-5,a-b\r\n", csv);
	}

	[Fact]
	public void BuildCsv_GuardsHeadersToo()
	{
		Assert.StartsWith("'=bad,ok\r\n", ListExport.BuildCsv(new[] { "=bad", "ok" }, Array.Empty<IReadOnlyList<object>>()));
	}

	[Fact]
	public void WriteCsv_HasByteOrderMark_AndKeepsAccents()
	{
		string path = TempFile(".csv");
		try
		{
			ListExport.WriteCsv(path, Headers, new[] { Row("Zoë", 1, null) });
			byte[] bytes = File.ReadAllBytes(path);
			Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
			Assert.Contains("Zoë", Encoding.UTF8.GetString(bytes));
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void WriteCsv_EmptyList_WritesJustTheHeader()
	{
		Assert.Equal("Name,Count,Date\r\n", ListExport.BuildCsv(Headers, Array.Empty<IReadOnlyList<object>>()));
	}

	[Fact]
	public void BuildCsv_RowWithMoreCellsThanHeaders_Throws()
	{
		Assert.Throws<ArgumentException>(() => ListExport.BuildCsv(new[] { "H" }, new[] { Row(1, 2) }));
	}

	[Fact]
	public void WriteXlsx_NumbersAndDatesSurviveAReadBack()
	{
		string path = TempFile(".xlsx");
		try
		{
			ListExport.WriteXlsx(path, Headers, new[]
			{
				Row("Oak St", 12, new DateTime(2026, 7, 4)),
				Row("Elm St", 3.25m, new DateTime(2026, 12, 31, 8, 30, 0)),
				Row(null, null, null),
			});
			SourceTable table = SheetReader.Read(path, null, true);
			Assert.Equal(Headers, table.Columns);
			Assert.Equal(new[] { "Oak St", "12", "2026-07-04" }, table.Rows[0].Values);
			Assert.Equal(new[] { "Elm St", "3.25", "2026-12-31 08:30:00" }, table.Rows[1].Values);

			using XLWorkbook wb = new XLWorkbook(path);
			IXLWorksheet ws = wb.Worksheet(1);
			Assert.Equal(XLDataType.Number, ws.Cell(2, 2).DataType);
			Assert.Equal(XLDataType.DateTime, ws.Cell(2, 3).DataType);
			Assert.Equal("2026-07-04", ws.Cell(2, 3).GetFormattedString());
			Assert.True(ws.Cell(1, 1).Style.Font.Bold);
			Assert.Equal(1, ws.SheetView.SplitRow);
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void WriteXlsx_FormulaLikeText_IsStoredAsTextNotAFormula()
	{
		string path = TempFile(".xlsx");
		try
		{
			string[] attacks = { "=1+1", "+1", "-1", "@SUM(A1)", "\tx", "\rx" };
			ListExport.WriteXlsx(path, new[] { "=head", "Value" }, attacks.Select(a => Row(a, 1)).ToList());
			using XLWorkbook wb = new XLWorkbook(path);
			IXLWorksheet ws = wb.Worksheet(1);
			Assert.False(ws.Cell(1, 1).HasFormula);
			Assert.Equal("=head", ws.Cell(1, 1).GetString());
			for (int i = 0; i < attacks.Length; i++)
			{
				IXLCell cell = ws.Cell(i + 2, 1);
				Assert.False(cell.HasFormula, attacks[i]);
				Assert.Equal(XLDataType.Text, cell.DataType);
				// XML turns a carriage return in a cell into a line feed; the text is otherwise unchanged.
				Assert.Equal(attacks[i].Replace('\r', '\n'), cell.GetString());
			}
		}
		finally
		{
			File.Delete(path);
		}
	}

	[Fact]
	public void WriteXlsx_ColumnWidthsAreBounded_AndEmptyListWritesJustTheHeader()
	{
		string path = TempFile(".xlsx");
		try
		{
			ListExport.WriteXlsx(path, Headers, new[] { Row(new string('x', 500), 1, null) });
			using (XLWorkbook wb = new XLWorkbook(path))
			{
				Assert.InRange(wb.Worksheet(1).Column(1).Width, 8, 60);
			}
			ListExport.WriteXlsx(path, Headers, Array.Empty<IReadOnlyList<object>>());
			SourceTable table = SheetReader.Read(path, null, true);
			Assert.Equal(Headers, table.Columns);
			Assert.Empty(table.Rows);
		}
		finally
		{
			File.Delete(path);
		}
	}
}
