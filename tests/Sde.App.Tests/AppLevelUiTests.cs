using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sde.App.Services;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Sheets;
using Sde.App.Views;
using Sde.Core.Assessments;
using Sde.Data;
using Sde.Field;
using SkiaSharp;
using Page = Sde.App.ViewModels.Page;

namespace Sde.App.Tests;

/// <summary>
/// App-level additions: What's New (once after an update, from Help and About), Help, then Check for Updates (only when
/// chosen, against faked GitHub answers), files dropped anywhere on the window, and the phone form's status change.
/// </summary>
public class AppLevelUiTests
{
	private const string ReleasePage = "https://github.com/mjs06-git/sde-for-mac/releases/tag/v3.5.1";

	private static string SettingsFile(UiSession ui) => Path.Combine(Path.GetDirectoryName(ui.Store.Database.FilePath)!, "app-settings.json");

	private static NativeMenuItem HelpItem(UiSession ui, string header) =>
		NativeMenu.GetMenu(ui.Window).Items.OfType<NativeMenuItem>().Single(i => i.Header == "Help").Menu.Items.OfType<NativeMenuItem>().Single(i => i.Header == header);

	// ───────────── What's New ─────────────

	[AvaloniaFact]
	public void WhatsNew_ShowsOnceAfterAnUpdate_AndRemembersTheVersion()
	{
		using UiSession ui = new UiSession();
		ui.Services.Settings.LastSeenVersion = "3.0.0";
		ui.Services.Settings.SeenWalkthrough = true;

		_ = ui.Main.ShowWhatsNewIfUpdated("3.5.0", isFirstRun: false);
		ui.Pump();
		WhatsNewSheetViewModel sheet = Assert.IsType<WhatsNewSheetViewModel>(ui.Main.Sheets.Current);
		Assert.Equal("What's new in SDE 3.5", sheet.Heading);
		Assert.Equal("3.5.0", ui.Services.Settings.LastSeenVersion);
		Assert.Equal("3.5.0", AppSettings.Load(SettingsFile(ui)).LastSeenVersion);   // saved
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));

		ui.Main.Sheets.CancelCurrentCommand.Execute(null);
		_ = ui.Main.ShowWhatsNewIfUpdated("3.5.0", isFirstRun: false);
		ui.Pump();
		Assert.False(ui.Main.Sheets.HasSheet);   // once only
	}

	[AvaloniaFact]
	public void WhatsNew_IsNotShownOnABrandNewInstall_OnlyRecorded()
	{
		using UiSession ui = new UiSession();
		Assert.Null(ui.Services.Settings.LastSeenVersion);
		_ = ui.Main.ShowWhatsNewIfUpdated("3.5.0", isFirstRun: true);
		ui.Pump();
		Assert.False(ui.Main.Sheets.HasSheet);
		Assert.Equal("3.5.0", AppSettings.Load(SettingsFile(ui)).LastSeenVersion);
	}

	[AvaloniaFact]
	public void WhatsNew_IsShownAfterAnUpdateFromBeforeItExisted()
	{
		using UiSession ui = new UiSession();
		ui.Services.Settings.SeenWalkthrough = true;
		_ = ui.Main.ShowWhatsNewIfUpdated("3.5.0", isFirstRun: !ui.Services.Settings.SeenWalkthrough);
		ui.Pump();
		Assert.IsType<WhatsNewSheetViewModel>(ui.Main.Sheets.Current);
	}

	[AvaloniaFact]
	public void WhatsNew_AtLaunch_NeverInTestsOrTraining()
	{
		using UiSession ui = new UiSession();
		ui.Services.Settings.LastSeenVersion = "1.0.0";
		_ = ui.Main.ShowWhatsNewIfUpdated();   // SDE_DATABASE is set, as for the walkthrough
		ui.Pump();
		Assert.False(ui.Main.Sheets.HasSheet);
		Assert.Equal("1.0.0", ui.Services.Settings.LastSeenVersion);
	}

	[AvaloniaFact]
	public void WhatsNew_FromTheHelpMenu_AndFromAbout_EvenAfterChangingDatabase()
	{
		using UiSession ui = new UiSession();
		NativeMenuItem item = HelpItem(ui, "What's New in SDE");
		item.Command.Execute(null);
		ui.Pump();
		Assert.IsType<WhatsNewSheetViewModel>(ui.Main.Sheets.Current);
		ui.Main.Sheets.CancelCurrentCommand.Execute(null);

		Assert.True(ui.Main.Settings.HasWhatsNew);
		ui.Main.Settings.OpenWhatsNewCommand.Execute(null);
		ui.Pump();
		Assert.IsType<WhatsNewSheetViewModel>(ui.Main.Sheets.Current);
		ui.Main.Sheets.CancelCurrentCommand.Execute(null);

		// Settings is made again for another database; its About button still works.
		ui.Services.Open(Path.Combine(ui.Folder, "other.sqlite"));
		ui.Pump();
		Assert.True(ui.Main.Settings.HasWhatsNew);
		ui.Main.Settings.OpenWhatsNewCommand.Execute(null);
		Assert.IsType<WhatsNewSheetViewModel>(ui.Main.Sheets.Current);
	}

	[Fact]
	public void WhatsNew_IsShortAndInTheHouseStyle()
	{
		Assert.InRange(WhatsNew.Groups.Count, 3, 6);
		foreach (WhatsNewGroup g in WhatsNew.Groups)
		{
			Assert.False(string.IsNullOrEmpty(g.Icon));
			Assert.All(g.Items, line =>
			{
				Assert.DoesNotContain('—', line);
				Assert.InRange(line.Length, 5, 80);
				Assert.False(line.EndsWith('.'), line);
			});
		}
	}

	[AvaloniaTheory]
	[InlineData(false)]
	[InlineData(true)]
	public void WhatsNew_Snapshot(bool dark)
	{
		using UiSession ui = new UiSession(dark ? ThemeVariant.Dark : null);
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		_ = ui.Main.ShowWhatsNew();
		ui.Pump();
		Control panel = ui.Window.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "WhatsNewPanel");
		Assert.True(panel.IsEffectivelyVisible);
		Assert.Equal(WhatsNew.Groups.Count, ui.Window.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("whatsNewCard")));
		Assert.True(panel.Bounds.Height < ui.Window.Bounds.Height - 120, "What's New fits the window");
		ui.Snapshot(dark ? "93-dark-whats-new" : "93-whats-new");
	}

	// ───────────── Check for Updates ─────────────

	/// <summary>Answers every request with <paramref name="answer"/>, counting the requests.</summary>
	private sealed class GitHubFake(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
	{
		public int Requests;
		public string UserAgent;

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Interlocked.Increment(ref Requests);
			UserAgent = string.Join(" ", request.Headers.GetValues("User-Agent"));
			return answer(request);
		}

		public static GitHubFake Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
			new GitHubFake(_ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
	}

	private static string Release(string tag, string url = ReleasePage, bool prerelease = false, bool draft = false) =>
		"{\"tag_name\":\"" + tag + "\",\"html_url\":\"" + url + "\",\"published_at\":\"2026-11-02T15:00:00Z\",\"prerelease\":" + (prerelease ? "true" : "false")
		+ ",\"draft\":" + (draft ? "true" : "false") + "}";

	/// <summary>Chooses Help, then Check for Updates (with a made-up running version) and waits for the answer.</summary>
	private static UpdateSheetViewModel Check(UiSession ui, GitHubFake github, string running = "3.5.0")
	{
		ui.Services.HttpClientFactory = () => new HttpClient(github);
		_ = ui.Main.CheckForUpdatesAsync(running);
		UpdateSheetViewModel sheet = Assert.IsType<UpdateSheetViewModel>(ui.Main.Sheets.Current);
		for (int i = 0; i < 200 && sheet.IsChecking; i++)
		{
			ui.Pump();
			Thread.Sleep(5);
		}
		ui.Pump();
		return sheet;
	}

	[AvaloniaFact]
	public void Update_Newer_OffersTheDownloadPage_InTheBrowser()
	{
		using UiSession ui = new UiSession();
		Assert.NotNull(HelpItem(ui, "Check for Updates…").Command);
		GitHubFake github = GitHubFake.Json(Release("v3.5.1"));
		UpdateSheetViewModel sheet = Check(ui, github);
		Assert.True(sheet.IsNewer);
		Assert.Equal("SDE 3.5.1 is available", sheet.Headline);
		Assert.Contains("Published Nov 2, 2026", sheet.Detail);
		Assert.Contains("3.5.0", sheet.Detail);
		Assert.Equal("Open the Download Page", sheet.PrimaryText);
		Assert.Equal("SDE-for-Mac/3.5.0", github.UserAgent);
		Assert.Equal(1, github.Requests);
		ui.Snapshot("94b-update-available");

		sheet.PrimaryCommand.Execute(null);
		Assert.Equal(ReleasePage, ui.Dialogs.Opened.Last());
		Assert.False(ui.Main.Sheets.HasSheet);
	}

	[AvaloniaTheory]
	[InlineData("v3.5.0", false, false)]
	[InlineData("v3.4.2", false, false)]
	[InlineData("v3.6.0-beta1", true, false)]
	[InlineData("v3.6.0", false, true)]
	public void Update_SameOlderPreReleaseOrDraft_IsUpToDate(string tag, bool prerelease, bool draft)
	{
		using UiSession ui = new UiSession();
		UpdateSheetViewModel sheet = Check(ui, GitHubFake.Json(Release(tag, prerelease: prerelease, draft: draft)));
		Assert.True(sheet.IsUpToDate);
		Assert.Equal("SDE is up to date", sheet.Headline);
		Assert.Empty(ui.Dialogs.Opened);
		if (tag == "v3.5.0")
		{
			ui.Snapshot("94a-update-up-to-date");
		}
	}

	[AvaloniaTheory]
	[InlineData("{ not json", HttpStatusCode.OK)]
	[InlineData("{\"message\":\"API rate limit exceeded\"}", HttpStatusCode.Forbidden)]
	[InlineData("{\"tag_name\":\"v9.0.0\",\"html_url\":\"https://evil.example/download\",\"prerelease\":false,\"draft\":false}", HttpStatusCode.OK)]
	public void Update_BadAnswers_CouldNotCheck_AndNothingOpens(string body, HttpStatusCode status)
	{
		using UiSession ui = new UiSession();
		UpdateSheetViewModel sheet = Check(ui, GitHubFake.Json(body, status));
		Assert.True(sheet.IsFailed);
		Assert.Equal("Couldn't check for updates", sheet.Headline);
		Assert.False(string.IsNullOrEmpty(sheet.Detail));
		Assert.Equal("Try Again", sheet.PrimaryText);
		Assert.Empty(ui.Dialogs.Opened);
		if (status == HttpStatusCode.Forbidden)
		{
			Assert.Contains("limiting", sheet.Detail);
			ui.Snapshot("94c-update-could-not-check");
		}
	}

	[AvaloniaFact]
	public void Update_Offline_CouldNotCheck_AndTryAgainAsksAgain()
	{
		using UiSession ui = new UiSession();
		GitHubFake github = new GitHubFake(_ => throw new HttpRequestException("No route to host"));
		UpdateSheetViewModel sheet = Check(ui, github);
		Assert.True(sheet.IsFailed);
		Assert.Contains("Couldn't reach GitHub", sheet.Detail);
		sheet.PrimaryCommand.Execute(null);
		for (int i = 0; i < 200 && github.Requests < 2; i++)
		{
			ui.Pump();
			Thread.Sleep(5);
		}
		Assert.Equal(2, github.Requests);
	}

	[AvaloniaFact]
	public void Update_WhileChecking_ShowsProgress_AndCancelStopsWaiting()
	{
		using UiSession ui = new UiSession();
		TaskCompletionSource<HttpResponseMessage> never = new TaskCompletionSource<HttpResponseMessage>();
		UpdateSheetViewModel sheet = Check(ui, new GitHubFake(_ => never.Task));
		Assert.True(sheet.IsChecking);
		Assert.Equal("Checking for updates…", sheet.Headline);
		ui.Snapshot("94-update-checking");
		ui.Main.Sheets.CancelCurrentCommand.Execute(null);
		ui.Pump();
		Assert.False(ui.Main.Sheets.HasSheet);
	}

	[AvaloniaFact]
	public void NothingAsksForUpdates_AtStartUp()
	{
		// The real start-up: services, the main view model and window, then What's New and the walkthrough.
		using TempDatabase db = new TempDatabase();
		Environment.SetEnvironmentVariable("SDE_DATABASE", db.Database.FilePath);
		AppServices services = null;
		MainWindow window = null;
		try
		{
			services = new AppServices(new TestDialogs());
			int clients = 0;
			GitHubFake counting = GitHubFake.Json(Release("v9.9.9"));
			services.HttpClientFactory = () =>
			{
				Interlocked.Increment(ref clients);
				return new HttpClient(counting);
			};
			MainViewModel main = new MainViewModel(services);
			window = new MainWindow { DataContext = main, Width = 1440, Height = 900 };
			window.Show();
			for (int i = 0; i < 5; i++)
			{
				Dispatcher.UIThread.RunJobs();
				AvaloniaHeadlessPlatform.ForceRenderTimerTick();
			}
			_ = main.ShowWhatsNewIfUpdated("3.5.0", isFirstRun: false);
			_ = main.ShowWalkthroughIfFirstRun();
			_ = main.ShowWhatsNewIfUpdated();
			Thread.Sleep(50);
			Dispatcher.UIThread.RunJobs();
			Assert.Equal(0, clients);
			Assert.Equal(0, counting.Requests);
		}
		finally
		{
			window?.Close();
			services?.Close();
			Environment.SetEnvironmentVariable("SDE_DATABASE", null);
		}
	}

	// ───────────── Drop files anywhere ─────────────

	/// <summary>Files being dragged, as the window's storage provider makes them from their paths.</summary>
	private static DataTransfer Files(UiSession ui, params string[] paths)
	{
		DataTransfer data = new DataTransfer();
		foreach (string p in paths)
		{
			IStorageItem item = Directory.Exists(p)
				? ui.Window.StorageProvider.TryGetFolderFromPathAsync(p).GetAwaiter().GetResult()
				: ui.Window.StorageProvider.TryGetFileFromPathAsync(p).GetAwaiter().GetResult();
			Assert.NotNull(item);
			data.Add(DataTransferItem.CreateFile(item));
		}
		return data;
	}

	/// <summary>The middle of the page area (right of the sidebar, below the title band).</summary>
	private static readonly Point PageMiddle = new Point(840, 500);

	/// <summary>One step of a drag over the window. A drop comes after the pointer has entered and moved, as it does on the Mac.</summary>
	private static void Drag(UiSession ui, RawDragEventType type, DataTransfer data, Point? at = null)
	{
		if (type == RawDragEventType.Drop)
		{
			ui.Window.DragDrop(at ?? PageMiddle, RawDragEventType.DragEnter, data, DragDropEffects.Copy | DragDropEffects.Move, RawInputModifiers.None);
			ui.Window.DragDrop(at ?? PageMiddle, RawDragEventType.DragOver, data, DragDropEffects.Copy | DragDropEffects.Move, RawInputModifiers.None);
		}
		ui.Window.DragDrop(at ?? PageMiddle, type, data, DragDropEffects.Copy | DragDropEffects.Move, RawInputModifiers.None);
		ui.Pump();
	}

	private static string MakeFile(UiSession ui, string name, string text = "x")
	{
		string path = Path.Combine(ui.Folder, name);
		File.WriteAllText(path, text);
		return path;
	}

	private static string MakePhoto(UiSession ui, string name)
	{
		string path = Path.Combine(ui.Folder, name);
		File.WriteAllBytes(path, FieldPhotos.Jpeg(SKColors.SteelBlue));
		return path;
	}

	private static async Task Settle(UiSession ui, Func<bool> done)
	{
		for (int i = 0; i < 300 && !done(); i++)
		{
			ui.Pump();
			await Task.Delay(10);
		}
		ui.Pump();
	}

	[AvaloniaFact]
	public void DraggingOverTheWindow_ShowsTheOverlay_NamingWhatWasRecognised()
	{
		using UiSession ui = new UiSession();
		DropOverlay overlay = ui.Window.DropOverlayView;
		Assert.False(overlay.IsVisible);

		DataTransfer export = Files(ui, LegacyFixtures.WriteSde2Export(ui.Folder));
		Drag(ui, RawDragEventType.DragEnter, export);
		Drag(ui, RawDragEventType.DragOver, export);
		Assert.True(overlay.IsVisible);
		DropPlan plan = Assert.IsType<DropPlan>(overlay.DataContext);
		Assert.Equal(DropKind.SdeExport, plan.Kind);
		Assert.Equal("Drop to import", plan.Title);
		Assert.StartsWith("An SDE 2.x export folder", plan.Description);
		ui.Snapshot("95-drop-overlay");

		Drag(ui, RawDragEventType.DragLeave, export);
		Assert.False(overlay.IsVisible);

		DataTransfer odd = Files(ui, MakeFile(ui, "notes.docx"));
		Drag(ui, RawDragEventType.DragOver, odd);
		Assert.True(overlay.IsVisible);
		Assert.False(Assert.IsType<DropPlan>(overlay.DataContext).CanDrop);
		Assert.Equal("Can't import this", ((DropPlan)overlay.DataContext).Title);
		Drag(ui, RawDragEventType.DragLeave, odd);
	}

	[AvaloniaFact]
	public async Task DroppingAnSdeExport_OpensItsPreview_OnImportAndExport()
	{
		using UiSession ui = new UiSession();
		Drag(ui, RawDragEventType.Drop, Files(ui, LegacyFixtures.WriteSde2Export(ui.Folder)));
		await Settle(ui, () => ui.Main.ImportExport.HasPending);
		Assert.Equal(Page.ImportExport, ui.Main.ActivePage);
		Assert.Equal(ImportExportTab.Import, ui.Main.ImportExport.Tab);
		Assert.True(ui.Main.ImportExport.HasPending);
		Assert.False(ui.Window.DropOverlayView.IsVisible);
	}

	[AvaloniaFact]
	public async Task DroppingAFemaMdb_GoesToTheSameImport()
	{
		using UiSession ui = new UiSession();
		string mdb = MakeFile(ui, "SDE_1.mdb", "not really an Access file");
		Assert.Equal(DropKind.FemaDatabase, ui.Main.Drops.Plan(new[] { mdb }).Kind);
		await ui.Main.Drops.DropAsync(new[] { mdb });
		ui.Pump();
		Assert.Equal(Page.ImportExport, ui.Main.ActivePage);
		// The import's own reading says what was wrong (here, it isn't a real database); nothing else opened it.
		Assert.Contains(ui.Dialogs.Notifications, n => n.StartsWith("ERROR Can't import that") || n.Contains("mdb"));
		Assert.Empty(ui.Dialogs.Opened);
	}

	[AvaloniaFact]
	public async Task DroppingAnSdeDatabase_AsksThenOpensIt()
	{
		using UiSession ui = new UiSession();
		string other = Path.Combine(ui.Folder, "other.sqlite");
		SdeDatabase.Create(other);
		ui.Dialogs.Answers.Enqueue(false);   // No: nothing changes
		await ui.Main.Drops.DropAsync(new[] { other });
		Assert.NotEqual(Path.GetFullPath(other), Path.GetFullPath(ui.Store.Database.FilePath));

		ui.Dialogs.Answers.Enqueue(true);
		await ui.Main.Drops.DropAsync(new[] { other });
		ui.Pump();
		Assert.Equal(Path.GetFullPath(other), Path.GetFullPath(ui.Store.Database.FilePath));

		await ui.Main.Drops.DropAsync(new[] { other });   // already open: said, not asked
		Assert.Contains(ui.Dialogs.Notifications, n => n.StartsWith("Already open"));
	}

	[AvaloniaFact]
	public async Task DroppingASpreadsheet_AsksWhatItHolds_ThenOpensThatImport()
	{
		using UiSession ui = new UiSession();
		string csv = SourceImportUiTests.ImportFixture("county-parcels.csv");

		// Inspections or parcels: the same routing as a drop on Import & Export.
		Task drop = ui.Main.Drops.DropAsync(new[] { csv });
		ui.Pump();
		SpreadsheetDropSheetViewModel chooser = Assert.IsType<SpreadsheetDropSheetViewModel>(ui.Main.Sheets.Current);
		Assert.Equal("county-parcels.csv", chooser.FileName);
		Assert.Equal(new[] { SpreadsheetUse.InspectionsOrParcels, SpreadsheetUse.StructuresFromParcels }, chooser.Choices.Select(c => c.Use));
		Assert.Equal(SpreadsheetUse.InspectionsOrParcels, chooser.Chosen);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("96-drop-spreadsheet-chooser");
		chooser.ContinueCommand.Execute(null);
		await drop;
		Assert.Equal(Page.SourceImport, ui.Main.ActivePage);
		Assert.Equal("county-parcels.csv", ui.Main.SourceImport.FileName);

		// Structures from a parcel spreadsheet: Enterprise Import.
		drop = ui.Main.Drops.DropAsync(new[] { csv });
		ui.Pump();
		chooser = Assert.IsType<SpreadsheetDropSheetViewModel>(ui.Main.Sheets.Current);
		chooser.Choices[1].IsChosen = true;
		chooser.ContinueCommand.Execute(null);
		await drop;
		Assert.Equal(Page.ImportExport, ui.Main.ActivePage);
		Assert.Equal(ImportExportTab.Import, ui.Main.ImportExport.Tab);
		Assert.Equal(csv, ui.Main.ImportExport.Enterprise.FilePath);

		// Cancel: nothing opens.
		ui.Main.ShowCommand.Execute(Page.Structures);
		drop = ui.Main.Drops.DropAsync(new[] { csv });
		ui.Pump();
		ui.Main.Sheets.CancelCurrentCommand.Execute(null);
		await drop;
		Assert.Equal(Page.Structures, ui.Main.ActivePage);
	}

	[AvaloniaFact]
	public async Task DroppingAWorkbook_OffersCoordinates_AndACsvWithAnAssessmentOpen_OffersHighWaterMarks()
	{
		using UiSession ui = new UiSession();
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		string sheet = Path.Combine(ui.Folder, "coords.xlsx");
		ui.Dialogs.Answers.Enqueue(sheet);
		await ui.Main.ImportExport.ExportLatLongCommand.ExecuteAsync(null);

		Task drop = ui.Main.Drops.DropAsync(new[] { sheet });
		ui.Pump();
		SpreadsheetDropSheetViewModel chooser = Assert.IsType<SpreadsheetDropSheetViewModel>(ui.Main.Sheets.Current);
		SpreadsheetUseChoice coords = Assert.Single(chooser.Choices, c => c.Use == SpreadsheetUse.Coordinates);
		Assert.DoesNotContain(chooser.Choices, c => c.Use == SpreadsheetUse.HighWaterMarks);
		coords.IsChosen = true;
		chooser.ContinueCommand.Execute(null);
		await drop;
		await Settle(ui, () => ui.Main.ImportExport.HasLatLong);
		Assert.Equal(ImportExportTab.FieldWork, ui.Main.ImportExport.Tab);
		Assert.True(ui.Main.ImportExport.HasLatLong);

		// With an assessment open, a .csv can be high-water marks: Flood Depth opens with them read.
		AssessmentFile f = SampleCases.RanchFlood(ui.Store);
		ui.Main.OpenAssessment(f.Assessment.AssessmentID);
		ui.Pump();
		string marks = MakeFile(ui, "marks.csv", "latitude,longitude,elev_ft\n39.7,-84.2,742\n");
		drop = ui.Main.Drops.DropAsync(new[] { marks });
		ui.Pump();
		chooser = Assert.IsType<SpreadsheetDropSheetViewModel>(ui.Main.Sheets.Current);
		Assert.DoesNotContain(chooser.Choices, c => c.Use == SpreadsheetUse.Coordinates);
		chooser.Choices.Single(c => c.Use == SpreadsheetUse.HighWaterMarks).IsChosen = true;
		chooser.ContinueCommand.Execute(null);
		ui.Pump();
		DepthSheetViewModel depth = Assert.IsType<DepthSheetViewModel>(ui.Main.Sheets.Current);
		Assert.StartsWith("marks.csv: 1 mark", depth.MarksSummary);
		depth.CancelCommand.Execute(null);
		await drop;
	}

	[AvaloniaFact]
	public async Task DroppingPhotos_GoIntoTheOpenAssessment_OrAreMatchedToStructures()
	{
		using UiSession ui = new UiSession();
		AssessmentFile f = SampleCases.RanchFlood(ui.Store);
		ui.Services.RaiseDataChanged();
		string a = MakePhoto(ui, "front.jpg"), b = MakePhoto(ui, "side.jpg");

		// No assessment open: matched to structures (the review sheet, nothing attached yet).
		DropPlan plan = ui.Main.Drops.Plan(new[] { a, b });
		Assert.Equal((DropKind.Photos, "2 photos to match to structures"), (plan.Kind, plan.Description));
		Task matching = ui.Main.Drops.DropAsync(new[] { a, b });
		await Settle(ui, () => ui.Main.Sheets.Current is PhotoFolderSheetViewModel);
		Assert.IsType<PhotoFolderSheetViewModel>(ui.Main.Sheets.Current);
		Assert.Equal(ImportExportTab.FieldWork, ui.Main.ImportExport.Tab);
		ui.Main.Sheets.CancelCurrentCommand.Execute(null);
		await matching;
		Assert.Empty(ui.Store.LoadAssessment(f.Assessment.AssessmentID).Attachments);

		// An assessment open in the editor: they are added to it, on its Photos step (the editor's own drop handles it).
		ui.Main.OpenAssessment(f.Assessment.AssessmentID);
		ui.Pump();
		AssessmentEditorViewModel editor = ui.Main.Editor;
		Assert.Contains("for", ui.Main.Drops.Plan(new[] { a }).Description);
		Drag(ui, RawDragEventType.Drop, Files(ui, a, b));
		await Settle(ui, () => editor.Photos.Count == 2);
		Assert.Equal(2, editor.Photos.Count);
		Assert.IsType<PhotosSectionViewModel>(editor.SelectedSection);
		Assert.False(ui.Window.DropOverlayView.IsVisible);
	}

	[AvaloniaFact]
	public async Task DroppingAMapLayer_AddsItOnTheMap()
	{
		using UiSession ui = new UiSession();
		string parcels = MapLayersTests.Town("parcels.geojson");
		Assert.Equal(DropKind.MapLayer, ui.Main.Drops.Plan(new[] { parcels }).Kind);
		await ui.Main.Drops.DropAsync(new[] { parcels });
		ui.Pump();
		Assert.Equal(Page.Map, ui.Main.ActivePage);
		Assert.Contains(ui.Main.Map.Layers, l => l.IsFeatures && l.Record.Source == parcels);
	}

	[AvaloniaFact]
	public async Task DroppingSomethingElse_SaysSDECantImportIt()
	{
		using UiSession ui = new UiSession();
		Drag(ui, RawDragEventType.Drop, Files(ui, MakeFile(ui, "minutes.docx")));
		await Settle(ui, () => ui.Dialogs.Notifications.Count > 0);
		Assert.Equal("ERROR " + DropRouter.CantImport + ": minutes.docx", Assert.Single(ui.Dialogs.Notifications));

		await ui.Main.Drops.DropAsync(new[] { MakeFile(ui, "a.docx"), MakeFile(ui, "b.csv") });
		Assert.StartsWith("ERROR Can't import that: Drop one file at a time", ui.Dialogs.Notifications.Last());
		Assert.Equal(DropKind.Unsupported, ui.Main.Drops.Plan(new[] { MakeFile(ui, "places.kml") }).Kind);
	}

	[AvaloniaFact]
	public async Task DropsAreIgnored_WhileASheetShows()
	{
		using UiSession ui = new UiSession();
		_ = ui.Main.ShowWhatsNew();
		ui.Pump();
		DataTransfer data = Files(ui, MakeFile(ui, "minutes.docx"));
		Drag(ui, RawDragEventType.DragOver, data);
		Assert.False(ui.Window.DropOverlayView.IsVisible);
		Drag(ui, RawDragEventType.Drop, data);
		await ui.Main.Drops.DropAsync(new[] { MakeFile(ui, "other.docx") });
		ui.Pump();
		Assert.Empty(ui.Dialogs.Notifications);
		Assert.IsType<WhatsNewSheetViewModel>(ui.Main.Sheets.Current);
	}

	[AvaloniaFact]
	public async Task ThePagesOwnDropZones_StillTakeTheirDrops()
	{
		using UiSession ui = new UiSession();
		ui.Main.ShowCommand.Execute(Page.ImportExport);
		ui.Pump();
		Control zone = ui.Window.GetVisualDescendants().OfType<Control>().First(c => c.Name == "DropZone" && c.IsEffectivelyVisible);
		Point middle = zone.TranslatePoint(new Point(zone.Bounds.Width / 2, zone.Bounds.Height / 2), ui.Window)!.Value;
		string csv = SourceImportUiTests.ImportFixture("county-parcels.csv");

		// Over Import & Export's drop zone the page tints its zone and the window's overlay stays away.
		DataTransfer data = Files(ui, csv);
		Drag(ui, RawDragEventType.DragEnter, data, middle);
		Drag(ui, RawDragEventType.DragOver, data, middle);
		Assert.True(zone.Classes.Contains("dragOver"));
		Assert.False(ui.Window.DropOverlayView.IsVisible);

		// The page's drop takes it straight to Import Inspections or Parcels: no chooser.
		Drag(ui, RawDragEventType.Drop, data, middle);
		await Settle(ui, () => ui.Main.ActivePage == Page.SourceImport);
		Assert.Equal(Page.SourceImport, ui.Main.ActivePage);
		Assert.False(ui.Main.Sheets.HasSheet);
	}

	// ───────────── The phone form's status change ─────────────

	[AvaloniaFact]
	public async Task PhoneStatusChange_RunsOnceOnTheUIThread_ThroughTheCoordinator_IntoTheOpenEditor()
	{
		using UiSession ui = new UiSession();
		AssessmentFile f = SampleCases.RanchFlood(ui.Store);
		Guid id = f.Assessment.AssessmentID;
		ui.Main.OpenAssessment(id);
		ui.Pump();
		Assert.Equal(AssessmentStatus.Draft, ui.Main.Editor.Status?.Value ?? AssessmentStatus.Draft);
		int dataChanged = 0;
		ui.Services.DataChanged += () => dataChanged++;
		int calls = 0;
		bool onUiThread = false;
		SaveContext context = new SaveContext { User = "Test Inspector", Via = SaveVia.Phone };
		IFieldWriter writer = new AppFieldWriter(ui.Services);

		Task change = Task.Run(() => writer.SetStatusAsync(id, () =>
		{
			calls++;
			onUiThread = Dispatcher.UIThread.CheckAccess();
			ui.Store.SetStatus(id, AssessmentStatus.Reviewed, null, context);
		}, context));
		await Settle(ui, () => change.IsCompleted);
		await change;

		Assert.Equal(1, calls);
		Assert.True(onUiThread);
		Assert.Equal(1, dataChanged);   // one write, not three
		Assert.Equal(AssessmentStatus.Reviewed, ui.Main.Editor.Status.Value);   // the open editor caught up
		Assert.Equal(AssessmentStatus.Reviewed, ui.Store.GetStatus(id).Value);
	}

	[AvaloniaFact]
	public async Task PhoneStatusChange_RefusedInsideTheWrite_ComesBackAsTheError()
	{
		using UiSession ui = new UiSession();
		AssessmentFile f = SampleCases.RanchFlood(ui.Store);
		IFieldWriter writer = new AppFieldWriter(ui.Services);
		Task change = Task.Run(() => writer.SetStatusAsync(f.Assessment.AssessmentID, () => throw new InvalidOperationException("Final on the Mac."), new SaveContext()));
		await Settle(ui, () => change.IsCompleted);
		await Assert.ThrowsAsync<InvalidOperationException>(() => change);
	}
}
