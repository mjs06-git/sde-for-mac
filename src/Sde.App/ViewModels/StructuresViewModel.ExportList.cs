using CommunityToolkit.Mvvm.Input;
using Sde.Interop;

namespace Sde.App.ViewModels;

/// <summary>
/// Export ▾ › This List to CSV or Excel: exactly the rows the list shows (after filters and search, in its order;
/// only the checked ones when some are checked) and exactly the columns it shows, in their order. Figures and dates go
/// out as real numbers and dates. This is not FEMA's format (that is Export ▾ › FEMA Excel Workbook).
/// </summary>
public sealed partial class StructuresViewModel
{
	/// <summary>A column of the list as it is exported: its key, header, and the cell for a row.</summary>
	private sealed record ExportColumn(string Key, string Header, Func<StructureRowViewModel, object> Cell);

	private static readonly ExportColumn[] ExportColumns =
	{
		new ExportColumn("Address", "Structure", r => r.Address),
		new ExportColumn("Owner", "Owner", r => OrNull(r.Owner)),
		new ExportColumn("Community", "Community", r => OrNull(r.Place)),
		new ExportColumn("Type", "Type", r => OrNull(r.Type)),
		new ExportColumn("Inspected", "Inspected", r => r.Summary.DateofInspection?.Date),
		new ExportColumn("Inspector", "Inspector", r => OrNull(r.Inspector)),
		new ExportColumn("Status", "Status", r => OrNull(r.Status)),
		new ExportColumn("Tags", "Tags", r => OrNull(r.TagsText)),
		new ExportColumn("Zip", "ZIP", r => OrNull(r.Zip)),
		new ExportColumn("Parcel", "Parcel", r => OrNull(r.Parcel)),
		new ExportColumn("FirmZone", "FIRM zone", r => OrNull(r.FirmZone)),
		new ExportColumn("Source", "Source record", r => OrNull(r.Source)),
		new ExportColumn("Updated", "Last changed", r => r.Summary.LastUpdate),
		new ExportColumn("Damaged", "Damaged (%)", r => r.HasPercent ? r.Summary.PercentDamage : null),
		new ExportColumn("Result", "Result", r => r.Result),
	};

	private static object OrNull(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

	/// <summary>The columns shown, in the list's order (Structure, the optional ones chosen, Damaged and Result).</summary>
	private IReadOnlyList<ExportColumn> ShownExportColumns() =>
		ExportColumns.Where(c => c.Key is "Address" or "Damaged" or "Result" || IsColumnVisible(c.Key)).ToList();

	/// <summary>The rows This List exports: the checked ones the list shows, or every one it shows when none is checked.</summary>
	public IReadOnlyList<StructureRowViewModel> ListExportRows()
	{
		List<StructureRowViewModel> chosen = Rows.Where(r => r.IsChecked).ToList();
		return chosen.Count > 0 ? chosen : Rows.ToList();
	}

	/// <summary>The headers This List exports.</summary>
	public IReadOnlyList<string> ListExportHeaders() => ShownExportColumns().Select(c => c.Header).ToList();

	/// <summary>The cells This List exports, a row per structure, in the list's order.</summary>
	public IReadOnlyList<IReadOnlyList<object>> ListExportCells()
	{
		IReadOnlyList<ExportColumn> columns = ShownExportColumns();
		return ListExportRows().Select(r => (IReadOnlyList<object>)columns.Select(c => c.Cell(r)).ToList()).ToList();
	}

	[RelayCommand]
	private Task ExportListToCsv() => ExportListAsync(excel: false);

	[RelayCommand]
	private Task ExportListToExcel() => ExportListAsync(excel: true);

	private async Task ExportListAsync(bool excel)
	{
		if (Rows.Count == 0)
		{
			_app.Dialogs.Notify("Nothing to export", "The list is empty. Clear the search or filters first.");
			return;
		}
		string name = AppliedView ?? "Structures";
		string extension = excel ? "xlsx" : "csv";
		string path = await _app.Dialogs.SaveFileAsync(excel ? "Export this list to Excel" : "Export this list to CSV",
			SafeFileName(name) + "." + extension, excel ? "Excel workbook" : "CSV file", extension);
		if (string.IsNullOrEmpty(path))
		{
			return;
		}
		IReadOnlyList<string> headers = ListExportHeaders();
		IReadOnlyList<IReadOnlyList<object>> cells = ListExportCells();
		try
		{
			await Task.Run(() =>
			{
				if (excel)
				{
					ListExport.WriteXlsx(path, headers, cells, name);
				}
				else
				{
					ListExport.WriteCsv(path, headers, cells);
				}
			});
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't export the list", ex);
			return;
		}
		_app.Dialogs.Notify("List exported", $"{cells.Count:N0} structure{(cells.Count == 1 ? "" : "s")} in “{Path.GetFileName(path)}”.",
			"Show in Finder", () => _app.Dialogs.Reveal(path));
	}

	private static string SafeFileName(string name)
	{
		string clean = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is ':' or '/' ? ' ' : c).ToArray()).Trim();
		return clean.Length == 0 ? "Structures" : clean;
	}
}
