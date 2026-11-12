using Sde.App.Services;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Sheets;
using Sde.Core.Exchange;

namespace Sde.App.ViewModels;

/// <summary>What a file or folder held over the window would do when dropped.</summary>
public enum DropKind
{
	/// <summary>Nothing usable is being dragged (text, or a file with no local path).</summary>
	None,

	/// <summary>An SDE export folder (3.x or 2.x): read into Import &amp; Export's preview.</summary>
	SdeExport,

	/// <summary>A FEMA SDE database (.mdb): read into Import &amp; Export's preview.</summary>
	FemaDatabase,

	/// <summary>An SDE database (.sqlite): opened as the current database, after asking.</summary>
	SdeDatabase,

	/// <summary>A spreadsheet: a short chooser asks what it holds.</summary>
	Spreadsheet,

	/// <summary>Photos: added to the open assessment, or matched to structures.</summary>
	Photos,

	/// <summary>A folder of photos: matched to structures.</summary>
	PhotoFolder,

	/// <summary>Map layer files: added on the Map.</summary>
	MapLayer,

	/// <summary>Something SDE can't import.</summary>
	Unsupported,
}

/// <summary>A drop's plan: what it is, the overlay's heading and the line naming what was recognised.</summary>
public sealed record DropPlan(DropKind Kind, string Title, string Description, IReadOnlyList<string> Paths)
{
	/// <summary>True when a drop would do something (the overlay shows the accent outline and the pointer shows a copy).</summary>
	public bool CanDrop => Kind is not (DropKind.None or DropKind.Unsupported);
}

/// <summary>
/// Files dropped anywhere on the window, sent to the flow that already handles that kind of file: an SDE export folder or
/// FEMA .mdb to Import &amp; Export's preview, an SDE database to Open Database (after asking), a spreadsheet to a short
/// chooser (inspections or parcels, Enterprise Import, coordinates, high-water marks), photos to the open assessment or
/// to Match a Photo Folder, map layers to the Map's Add Layer, and anything else to a short "can't import" toast.
/// Nothing is opened, run or previewed beyond what those flows do. Page drop zones (Import &amp; Export, Import
/// Inspections or Parcels, the editor's photos) handle their own drops first, and nothing is taken while a sheet shows.
/// </summary>
public sealed class DropRouter(AppServices app, MainViewModel main)
{
	/// <summary>Spreadsheets whose meaning a drop can't tell, so the chooser asks.</summary>
	public static readonly IReadOnlyList<string> SpreadsheetExtensions = new[] { ".csv", ".xlsx", ".xlsm", ".xls", ".dbf" };

	/// <summary>Map layer files the Map's Add Layer reads (a .json or .csv might be something else, so those are not taken here).</summary>
	public static readonly IReadOnlyList<string> MapLayerExtensions = new[] { ".shp", ".geojson", ".gpkg" };

	/// <summary>Said in the toast for anything SDE doesn't import.</summary>
	public const string CantImport = "SDE can't import that kind of file";

	/// <summary>True when the path is a photo SDE can attach (the editor's photo types).</summary>
	public static bool IsPhoto(string path) =>
		AssessmentEditorViewModel.PhotoPatterns.Any(pat => path.EndsWith(pat[1..], StringComparison.OrdinalIgnoreCase));

	private static bool Has(IReadOnlyList<string> extensions, string path) =>
		extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

	/// <summary>The editor, when an assessment is open on the editor page (dropped photos go to it).</summary>
	private AssessmentEditorViewModel OpenEditor => main.IsEditorPage ? main.Editor : null;

	/// <summary>Works out what dropping <paramref name="paths"/> would do, for the overlay and for <see cref="DropAsync"/>.</summary>
	public DropPlan Plan(IReadOnlyList<string> paths)
	{
		List<string> items = (paths ?? Array.Empty<string>()).Where(p => !string.IsNullOrEmpty(p)).ToList();
		if (items.Count == 0)
		{
			return new DropPlan(DropKind.None, null, null, items);
		}
		if (items.All(p => File.Exists(p) && IsPhoto(p)))
		{
			string photos = items.Count == 1 ? "1 photo" : $"{items.Count:N0} photos";
			return OpenEditor is AssessmentEditorViewModel editor
				? new DropPlan(DropKind.Photos, "Drop to add photos", $"{photos} for {AssessmentEditorViewModel.Address(editor.Property, app.Store.Reference, "this assessment")}", items)
				: new DropPlan(DropKind.Photos, "Drop to import", $"{photos} to match to structures", items);
		}
		if (items.Count > 1)
		{
			return items.All(p => File.Exists(p) && Has(MapLayerExtensions, p))
				? new DropPlan(DropKind.MapLayer, "Drop to import", $"{items.Count:N0} map layers for the Map", items)
				: Unsupported("Drop one file at a time, or several photos", items);
		}

		string path = items[0];
		string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
		if (Directory.Exists(path))
		{
			if (SdeExchangeFolder.IsExchangeFolder(path))
			{
				return new DropPlan(DropKind.SdeExport, "Drop to import", $"An SDE export folder, {name}", items);
			}
			if (SdeExchangeFolder.IsLegacyFolder(path))
			{
				return new DropPlan(DropKind.SdeExport, "Drop to import", $"An SDE 2.x export folder, {name}", items);
			}
			int photos = SafePhotoCount(path);
			return photos > 0
				? new DropPlan(DropKind.PhotoFolder, "Drop to import", $"A folder of {photos:N0} photo{(photos == 1 ? "" : "s")} to match to structures", items)
				: Unsupported("That folder isn't an SDE export", items);
		}
		if (!File.Exists(path))
		{
			return new DropPlan(DropKind.None, null, null, items);
		}
		string extension = Path.GetExtension(path).ToLowerInvariant();
		if (extension == ".mdb")
		{
			return new DropPlan(DropKind.FemaDatabase, "Drop to import", $"A FEMA SDE database, {name}", items);
		}
		if (extension == ".sqlite")
		{
			return new DropPlan(DropKind.SdeDatabase, "Drop to open", $"An SDE database, {name}", items);
		}
		if (Has(SpreadsheetExtensions, path))
		{
			return new DropPlan(DropKind.Spreadsheet, "Drop to import", $"A spreadsheet, {name}. You choose what it holds next.", items);
		}
		if (Has(MapLayerExtensions, path))
		{
			return new DropPlan(DropKind.MapLayer, "Drop to import", $"A map layer for the Map, {name}", items);
		}
		return Unsupported(CantImport, items);
	}

	private static DropPlan Unsupported(string why, IReadOnlyList<string> paths) => new DropPlan(DropKind.Unsupported, "Can't import this", why, paths);

	private static int SafePhotoCount(string folder)
	{
		try
		{
			return ImportExportViewModel.PhotosIn(folder).Count;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return 0;
		}
	}

	/// <summary>Sends dropped files and folders to the flow for their kind (see the class summary).</summary>
	public async Task DropAsync(IReadOnlyList<string> paths)
	{
		if (app.Sheets.HasSheet)
		{
			return;
		}
		DropPlan plan = Plan(paths);
		switch (plan.Kind)
		{
			case DropKind.SdeExport:
			case DropKind.FemaDatabase:
				// The same path as a drop on Import & Export: read into its Import tab's preview, nothing written yet.
				main.ShowCommand.Execute(Page.ImportExport);
				await main.ImportExport.DropAsync(plan.Paths[0]);
				break;
			case DropKind.SdeDatabase:
				await OpenDatabaseAsync(plan.Paths[0]);
				break;
			case DropKind.Spreadsheet:
				await ImportSpreadsheetAsync(plan.Paths[0]);
				break;
			case DropKind.Photos:
				await AddPhotosAsync(plan.Paths);
				break;
			case DropKind.PhotoFolder:
				ShowFieldWork();
				await main.ImportExport.MatchPhotosAsync(plan.Paths[0], ImportExportViewModel.PhotosIn(plan.Paths[0]));
				break;
			case DropKind.MapLayer:
				main.ShowCommand.Execute(Page.Map);
				foreach (string layer in plan.Paths)
				{
					await main.Map.AddFileAsync(layer);
				}
				break;
			case DropKind.Unsupported when plan.Description == CantImport:
				app.Dialogs.Notify(CantImport, string.Join(", ", plan.Paths.Select(p => Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar)))), isError: true);
				break;
			case DropKind.Unsupported:
				app.Dialogs.Notify("Can't import that", plan.Description + ".", isError: true);
				break;
		}
	}

	private void ShowFieldWork()
	{
		main.ShowCommand.Execute(Page.ImportExport);
		main.ImportExport.Tab = ImportExportTab.FieldWork;
	}

	/// <summary>Photos: into the open assessment's Photos step, as a drop on the editor does; otherwise matched to structures.</summary>
	private async Task AddPhotosAsync(IReadOnlyList<string> photos)
	{
		if (OpenEditor is AssessmentEditorViewModel editor)
		{
			if (editor.Sections.OfType<PhotosSectionViewModel>().FirstOrDefault() is PhotosSectionViewModel section)
			{
				editor.SelectedSection = section;
			}
			await editor.AddPhotosAsync(photos);
			return;
		}
		ShowFieldWork();
		string folder = Path.GetDirectoryName(photos[0]);
		await main.ImportExport.MatchPhotosAsync(folder, photos.Order(StringComparer.OrdinalIgnoreCase).ToList());
	}

	/// <summary>An SDE database: File, then Open Database's open, after asking (it replaces the database on screen).</summary>
	private async Task OpenDatabaseAsync(string path)
	{
		string name = Path.GetFileName(path);
		if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(app.Store.Database.FilePath), StringComparison.Ordinal))
		{
			app.Dialogs.Notify("Already open", $"{name} is the database open now.");
			return;
		}
		if (!await app.Dialogs.ConfirmAsync($"Open {name}?",
			"SDE switches to this database. The one open now stays as it is, and File, then Open Recent brings it back.", "Open Database"))
		{
			return;
		}
		main.Editor?.Save();
		main.Settings.OpenRecentCommand.Execute(path);
	}

	/// <summary>A spreadsheet: asks what it holds, then opens that import with the file.</summary>
	private async Task ImportSpreadsheetAsync(string path)
	{
		AssessmentEditorViewModel editor = main.Editor;
		SpreadsheetDropSheetViewModel chooser = new SpreadsheetDropSheetViewModel(path, includeCoordinates: string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase),
			includeHighWaterMarks: editor != null && string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase));
		SpreadsheetUse use = await app.Sheets.ShowAsync<SpreadsheetUse>(chooser);
		switch (use)
		{
			case SpreadsheetUse.InspectionsOrParcels:
				// Import & Export's own drop: a spreadsheet opens Import Inspections or Parcels with that file.
				await main.ImportExport.DropAsync(path);
				break;
			case SpreadsheetUse.StructuresFromParcels:
				main.ShowCommand.Execute(Page.ImportExport);
				main.ImportExport.Tab = ImportExportTab.Import;
				main.ImportExport.Enterprise.OpenFile(path);
				break;
			case SpreadsheetUse.Coordinates:
				main.ShowCommand.Execute(Page.ImportExport);
				await main.ImportExport.LoadLatLongAsync(path);
				break;
			case SpreadsheetUse.HighWaterMarks when editor != null:
				await editor.FloodDepthAsync(path);
				break;
		}
	}
}
