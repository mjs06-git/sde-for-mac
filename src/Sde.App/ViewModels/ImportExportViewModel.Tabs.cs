using CommunityToolkit.Mvvm.ComponentModel;

namespace Sde.App.ViewModels;

/// <summary>The three tabs of Import &amp; Export, in the order of the switch in the title band.</summary>
public enum ImportExportTab
{
	Import,
	Export,
	FieldWork,
}

/// <summary>
/// Import &amp; Export's tabs: Import (the drop zone, SDE data, other systems and settings files), Export (SDE data,
/// spreadsheets, maps and your setup) and Field Work (photos, work packages and coordinates). The tab chosen is kept
/// while SDE is open, because the view model lives as long as the window.
/// </summary>
public sealed partial class ImportExportViewModel
{
	/// <summary>The tabs' names, for the switch.</summary>
	public static IReadOnlyList<string> TabNames { get; } = new[] { "Import", "Export", "Field Work" };

	/// <summary>The tab showing (an <see cref="ImportExportTab"/> as a number, for the switch's SelectedIndex).</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(Tab), nameof(IsImportTab), nameof(IsExportTab), nameof(IsFieldWorkTab))]
	public partial int TabIndex { get; set; }

	public ImportExportTab Tab
	{
		get => (ImportExportTab)Math.Clamp(TabIndex, 0, 2);
		set => TabIndex = (int)value;
	}

	public bool IsImportTab => Tab == ImportExportTab.Import;

	public bool IsExportTab => Tab == ImportExportTab.Export;

	public bool IsFieldWorkTab => Tab == ImportExportTab.FieldWork;

	/// <summary>The spreadsheet files Import Inspections or Parcels reads (a dropped one opens that page).</summary>
	private static readonly string[] SpreadsheetExtensions = { ".xlsx", ".xlsm", ".xls", ".csv", ".dbf", ".shp" };

	/// <summary>
	/// A file or folder dropped on the page: a spreadsheet opens Import Inspections or Parcels with it; an SDE export
	/// folder or database is read into the Import tab's preview (nothing is written yet).
	/// </summary>
	public Task DropAsync(string path)
	{
		if (File.Exists(path) && SpreadsheetExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
		{
			_main.ShowCommand.Execute(Page.SourceImport);
			_main.SourceImport.OpenFile(path);
			return Task.CompletedTask;
		}
		return LoadPendingAsync(path);
	}

	/// <summary>The switch can't be left with nothing chosen.</summary>
	partial void OnTabIndexChanged(int value)
	{
		if (value < 0)
		{
			TabIndex = 0;
		}
	}
}
