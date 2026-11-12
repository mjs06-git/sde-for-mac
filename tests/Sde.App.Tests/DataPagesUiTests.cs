using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Sde.App.Services;
using Sde.App.ViewModels;
using Sde.App.Views;
using Sde.Interop;
using Sde.Reports;
using Page = Sde.App.ViewModels.Page;

namespace Sde.App.Tests;

/// <summary>
/// The data pages: Import &amp; Export's three tabs, the submission folder's extra options, a source row that covers
/// several buildings on the import page, and Settings' Appearance and About categories.
/// </summary>
public class DataPagesUiTests
{
	private static T Named<T>(UiSession ui, string name) where T : Control =>
		ui.Window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

	private static bool Shows(UiSession ui, string name) => Named<Control>(ui, name).IsEffectivelyVisible;

	// ───────────── Import & Export ─────────────

	[AvaloniaFact]
	public void ImportExport_HasThreeTabs_EachAShortPageOfCards_AndTheTabIsKeptForTheSession()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = new UiSession();
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		ImportExportViewModel ie = ui.Main.ImportExport;
		ui.Main.ShowCommand.Execute(Page.ImportExport);
		ui.Pump();

		// The switch is in the title band, with the three tabs in order; Import shows first, with its drop zone.
		ListBox tabs = Named<ListBox>(ui, "ImportExportTabs");
		Assert.True(tabs.IsEffectivelyVisible);
		Assert.Contains(tabs.GetVisualAncestors().OfType<Control>(), c => c.Classes.Contains("onFrame"));
		Assert.Equal(new[] { "Import", "Export", "Field Work" }, tabs.Items.OfType<ListBoxItem>().Select(i => i.Content as string));
		Assert.Equal(ImportExportTab.Import, ie.Tab);
		Assert.True(Shows(ui, "DropZone"));
		Assert.True(Shows(ui, "OtherSystemsCard"));
		Assert.True(Shows(ui, "SettingsFileImportCard"));
		Assert.False(Shows(ui, "PendingCard"));
		Assert.False(Shows(ui, "ExportTab"));
		Assert.False(Shows(ui, "FieldWorkTab"));
		ui.Snapshot("30-import-export");

		ie.Tab = ImportExportTab.Export;
		ui.Pump();
		Assert.Equal(1, tabs.SelectedIndex);
		Assert.False(Shows(ui, "ImportTab"));
		Assert.True(Shows(ui, "SdeExportCard"));
		Assert.True(Shows(ui, "SpreadsheetsCard"));
		Assert.True(Shows(ui, "SetupCard"));
		ui.Snapshot("30b-import-export-export");

		tabs.SelectedIndex = 2;                                   // the switch drives the tab too
		ui.Pump();
		Assert.Equal(ImportExportTab.FieldWork, ie.Tab);
		Assert.True(Shows(ui, "PhotosCard"));
		Assert.True(Shows(ui, "WorkPackagesCard"));
		Assert.True(Shows(ui, "CoordinatesCard"));
		Assert.False(Shows(ui, "ExportTab"));
		ui.Snapshot("30c-import-export-field-work");

		// Another page and back: the same tab.
		ui.Main.ShowCommand.Execute(Page.Structures);
		ui.Pump();
		ui.Main.ShowCommand.Execute(Page.ImportExport);
		ui.Pump();
		Assert.Equal(ImportExportTab.FieldWork, ie.Tab);
		Assert.True(Shows(ui, "FieldWorkTab"));

		// Every action still has its command.
		Assert.All(new[] { ie.ExportAllCommand, ie.ExportExcelCommand, ie.ExportMapCommand, ie.ExportLatLongCommand, ie.ChooseLatLongCommand,
			ie.ExportSettingsFileCommand, ie.ImportSettingsFileCommand, ie.ChooseSdeFolderCommand, ie.ChooseMdbCommand, ie.MatchPhotoFolderCommand, ie.CheckInCommand },
			c => Assert.NotNull(c));
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));
	}

	[AvaloniaFact]
	public async Task ImportExport_AnSdeFolderOpensItsPreviewOnImport_ASpreadsheetOpensTheImportPage()
	{
		using UiSession ui = new UiSession();
		ImportExportViewModel ie = ui.Main.ImportExport;
		ui.Main.ShowCommand.Execute(Page.ImportExport);
		ie.Tab = ImportExportTab.Export;

		// A dropped SDE 2.x folder: its preview is on the Import tab, which comes forward.
		await ie.DropAsync(LegacyFixtures.WriteSde2Export(ui.Folder));
		ui.Pump();
		Assert.Equal(ImportExportTab.Import, ie.Tab);
		Assert.True(ie.HasPending);
		Assert.True(Shows(ui, "PendingCard"));
		ui.Snapshot("35-import-sde2-preview");
		ie.CancelImportCommand.Execute(null);

		// A dropped spreadsheet: Import Inspections or Parcels, with the file open.
		await ie.DropAsync(SourceImportUiTests.ImportFixture("county-parcels.csv"));
		Assert.Equal(Page.SourceImport, ui.Main.ActivePage);
		Assert.Equal("county-parcels.csv", ui.Main.SourceImport.FileName);
	}

	// ───────────── Submission folder ─────────────

	[AvaloniaFact]
	public async Task Submission_TheFiveExtraOptions_AreOffByDefault_AndReachTheFolder()
	{
		using UiSession ui = new UiSession();
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		StructuresViewModel s = ui.Main.Structures;
		s.OpenSubmissionCommand.Execute(null);
		SubmissionViewModel sub = s.Submission;

		SubmissionOptions off = sub.BuildOptions();
		Assert.Equal((false, false, false, false, false), (off.IncludeLetters, off.IncludeDocuments, off.IncludeComplianceReport, off.IncludeEventSummary, off.IncludeCaseFiles));

		sub.IncludeLetters = true;
		sub.IncludeDocuments = true;
		sub.IncludeComplianceReport = true;
		sub.IncludeEventSummary = true;
		sub.IncludeCaseFiles = true;
		SubmissionOptions on = sub.BuildOptions();
		Assert.Equal((true, true, true, true, true), (on.IncludeLetters, on.IncludeDocuments, on.IncludeComplianceReport, on.IncludeEventSummary, on.IncludeCaseFiles));
		Assert.Equal(("Owner letters", "Case files"), (SubmissionViewModel.LettersLabel, SubmissionViewModel.CaseFilesLabel));

		// Written for real: the event summary is in the folder, and nothing went wrong to report.
		sub.IncludeLetters = sub.IncludeDocuments = sub.IncludeComplianceReport = sub.IncludeCaseFiles = false;
		string parent = Path.Combine(ui.Folder, "out");
		Directory.CreateDirectory(parent);
		ui.Dialogs.Answers.Enqueue(parent);
		await sub.WriteCommand.ExecuteAsync(null);
		string folder = Assert.Single(Directory.GetDirectories(parent));
		Assert.True(File.Exists(Path.Combine(folder, "Event summary.pdf")));
		Assert.False(sub.HasWarnings);
		Assert.Single(ui.Dialogs.Notifications, n => n.StartsWith("Submission folder written"));
	}

	// ───────────── Import inspections or parcels: several buildings per row ─────────────

	/// <summary>The county parcels fixture with a Buildings column: the first parcel has 3 buildings, the second 1.</summary>
	private static string SplitFile(UiSession ui, string firstCount = "3")
	{
		string path = Path.Combine(ui.Folder, "split-parcels.csv");
		File.WriteAllLines(path, new[]
		{
			"Parcel ID,Site Address,Site City,Site ZIP,Owner Name,Property Class,Year Built,Living Area,Stories,Land Value,Building Value,Buildings",
			$"900-01-001,100 SAMPLE AVE,EXAMPLEVILLE,99999,\"EXAMPLE, ALEX\",Single Family,1955,1200,1,\"30,000\",\"95,000\",{firstCount}",
			"900-02-010,15 PLACEHOLDER ST N,EXAMPLEVILLE,99999,TESTCO HOLDINGS,Commercial,1990,5000,1,\"80,000\",\"400,000\",1",
		});
		return path;
	}

	[AvaloniaFact]
	public async Task SplitProfile_RoundTrips_ThePreviewGroupsTheBuildings_AndImportKeysEachBuilding()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = new UiSession(height: 1500);
		SourceImportViewModel page = ui.Main.SourceImport;
		ui.Main.ShowCommand.Execute(Page.SourceImport);
		page.OpenFile(SplitFile(ui));
		page.SelectedExample = "County parcels";
		Assert.Equal(0, page.SplitModeIndex);                     // off unless the profile turns it on
		Assert.False(page.IsSplitting);
		Assert.Contains("Buildings", page.SplitColumnChoices);

		page.SplitModeIndex = 1;
		page.SplitColumn = "Buildings";
		page.SplitLabel = "Shed";
		await page.RefreshPreviewAsync();
		Assert.True(page.IsSplitByCount);
		Assert.Null(page.MappingProblems);
		Assert.Equal(4, page.PreviewRows.Count);
		Assert.Equal(2, page.PreviewGroups.Count);
		SourcePreviewGroupViewModel split = page.PreviewGroups[0];
		Assert.True(split.IsSplit);
		Assert.Equal(new[] { "Shed 1", "Shed 2", "Shed 3" }, split.Rows.Select(r => r.Building));
		Assert.Equal("Row 2 · 900-01-001 · 3 buildings", split.Header);
		Assert.False(page.PreviewGroups[1].IsSplit);
		Assert.Equal("Import 4 structures", page.ImportText);
		ui.Pump();
		Assert.True(Shows(ui, "SplitSettings"));
		ScrollViewer scroller = ui.Window.GetVisualDescendants().OfType<SourceImportView>().Single().GetVisualDescendants().OfType<ScrollViewer>().First();
		scroller.Offset = new Vector(0, scroller.Extent.Height - scroller.Viewport.Height);
		ui.Snapshot("36e-import-split-preview");

		// Leave the second building out. The buildings share their row number, so the choice must follow the building
		// (keying by row number alone threw, and would have mixed up the buildings' choices).
		split.Rows[1].Include = false;
		Assert.Equal("Import 3 structures", page.ImportText);
		await page.ImportCommand.ExecuteAsync(null);
		Assert.Equal("3 created from split-parcels.csv.", page.LastResult);
		Assert.Equal(3, ui.Store.Counts().Properties);
		Assert.DoesNotContain(ui.Store.GetStructureLinks("County parcels"), l => l.RecordId == "900-01-001#2");

		// The profile was saved with the split settings, and comes back with them.
		SourceProfile saved = ui.Services.ImportProfiles.Load("County parcels", out _);
		Assert.Equal((SourceSplitMode.CountColumn, "Buildings", "Shed", (string)null), (saved.SplitMode, saved.SplitColumn, saved.SplitLabel, saved.SplitSuffixField));
		page.CloseCommand.Execute(null);
		Assert.Empty(page.PreviewGroups);
		page.OpenFile(SplitFile(ui, firstCount: "1"));
		Assert.Equal(("County parcels", 1, "Buildings", "Shed"), (page.SelectedProfile, page.SplitModeIndex, page.SplitColumn, page.SplitLabel));

		// Fewer buildings: the structure made earlier is listed, never deleted.
		await page.RefreshPreviewAsync();
		Assert.Equal(new[] { "900-01-001#3" }, ui.Store.GetStructureLinks("County parcels").Select(l => l.RecordId).Where(id => id.EndsWith("#3")));
		Assert.True(page.HasMissingSplitStructures);
		Assert.Contains("900-01-001#3", Assert.Single(page.MissingSplitStructures));
		ui.Pump();
		Assert.True(Shows(ui, "MissingSplitList"));

		// A list of buildings, told apart by the lot number.
		page.SplitModeIndex = 2;
		page.SplitDelimiter = "|";
		page.SplitSuffixFieldIndex = 1;
		page.SaveProfileCommand.Execute(null);
		SourceProfile list = ui.Services.ImportProfiles.Load("County parcels", out _);
		Assert.Equal((SourceSplitMode.DelimitedColumn, "|", SourceProfile.SplitFieldLot), (list.SplitMode, list.SplitDelimiter, list.SplitSuffixField));
		page.SplitModeIndex = 0;
		page.SaveProfileCommand.Execute(null);
		Assert.Equal(SourceSplitMode.None, ui.Services.ImportProfiles.Load("County parcels", out _).SplitMode);
		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));
	}

	// ───────────── Settings ─────────────

	[AvaloniaFact]
	public void Settings_AppearanceIsSecond_AboutIsLast()
	{
		using UiSession ui = new UiSession();
		IReadOnlyList<SettingsCategoryItem> categories = ui.Main.Settings.Categories;
		Assert.Equal(SettingsCategory.Appearance, categories[1].Category);
		Assert.Equal("Appearance", categories[1].Title);
		Assert.Equal(SettingsCategory.About, categories[^1].Category);
		Assert.Equal(Enum.GetValues<SettingsCategory>(), categories.Select(c => c.Category));
	}

	[AvaloniaFact]
	public void Appearance_EachThemeChoice_IsSavedAndAppliedAtOnce()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = new UiSession();
		string settingsFile = Path.Combine(ui.Folder, "app-settings.json");
		try
		{
			ui.Main.ShowCommand.Execute(Page.Settings);
			SettingsViewModel settings = ui.Main.Settings;
			settings.Show(SettingsCategory.Appearance);
			ui.Pump();
			ListBox tiles = Named<ListBox>(ui, "ThemeTiles");
			Assert.True(tiles.IsEffectivelyVisible);
			Assert.Equal(new[] { "Match System", "Light", "Dark" }, tiles.Items.OfType<ListBoxItem>().Select(AutomationName));
			Assert.Equal(0, settings.ThemeIndex);                // no setting: match the Mac
			ui.Snapshot("31f-settings-appearance");

			settings.ThemeIndex = 2;
			Assert.Equal(Appearance.Dark, ui.Services.Settings.Theme);
			Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
			Assert.Equal(Appearance.Dark, AppSettings.Load(settingsFile).Theme);
			ui.Pump();
			Assert.Equal(2, tiles.SelectedIndex);
			ui.Snapshot("45c-dark-settings-appearance");

			tiles.SelectedIndex = 1;                              // choosing a tile does the same
			Assert.Equal(Appearance.Light, ui.Services.Settings.Theme);
			Assert.Equal(ThemeVariant.Light, Application.Current.RequestedThemeVariant);

			settings.ThemeIndex = 0;
			Assert.Null(ui.Services.Settings.Theme);
			Assert.Equal(ThemeVariant.Default, Application.Current.RequestedThemeVariant);
			Assert.Null(AppSettings.Load(settingsFile).Theme);
			Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));
		}
		finally
		{
			// The test session owns the theme: put it back for the next test.
			Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
		}
	}

	[AvaloniaFact]
	public void Appearance_TheAccentChoiceGoesThroughAppearanceApply_AndDensityWaitsForTheNextStart()
	{
		using UiSession ui = new UiSession();
		ui.Main.ShowCommand.Execute(Page.Settings);
		SettingsViewModel settings = ui.Main.Settings;
		settings.Show(SettingsCategory.Appearance);
		ui.Pump();
		try
		{
			// The choice is applied through Appearance.Apply, with the setting already saved.
			List<bool> applied = new();
			settings.ApplyAppearance = s => applied.Add(s.UseMacAccent);
			settings.AccentIndex = 1;
			Assert.True(ui.Services.Settings.UseMacAccent);
			Assert.True(AppSettings.Load(Path.Combine(ui.Folder, "app-settings.json")).UseMacAccent);
			Assert.Equal(new[] { true }, applied);
			settings.AccentIndex = 0;
			Assert.Equal(new[] { true, false }, applied);

			// The Mac's accent can be followed only on a Mac: elsewhere the choice is shown disabled, with why in its tooltip,
			// and the real path keeps SDE's blue.
			ListBoxItem mac = Named<ListBox>(ui, "AccentChoice").Items.OfType<ListBoxItem>().Last();
			Assert.Equal(OperatingSystem.IsMacOS(), mac.IsEnabled);
			if (!OperatingSystem.IsMacOS())
			{
				Assert.Equal("Only on a Mac", ToolTip.GetTip(mac));
				settings.ApplyAppearance = s => Appearance.Apply(Application.Current!, s);
				settings.AccentIndex = 1;
				Assert.True(Application.Current!.TryGetResource("AccentFill", ThemeVariant.Light, out object fill));
				Assert.Equal(SystemAccent.Fallback, ((ISolidColorBrush)fill).Color);
				settings.AccentIndex = 0;
			}

			// Density is saved now and used the next time SDE opens; the note says so.
			TextBlock note = Named<TextBlock>(ui, "DensityNote");
			Assert.True(note.IsEffectivelyVisible);
			Assert.Equal("Takes effect the next time SDE opens.", note.Text);
			Assert.False(settings.IsDensityChangePending);
			settings.DensityIndex = 1;
			Assert.True(ui.Services.Settings.CompactDensity);
			Assert.True(AppSettings.Load(Path.Combine(ui.Folder, "app-settings.json")).CompactDensity);
			Assert.Equal(!Appearance.IsCompact, settings.IsDensityChangePending);
			settings.DensityIndex = 0;
			Assert.False(ui.Services.Settings.CompactDensity);
		}
		finally
		{
			Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
			SystemAccent.UseBrand(Application.Current);
		}
	}

	[AvaloniaFact]
	public void About_ShowsTheBrandMarkAndTheAssemblyVersion_AndWhatsNewOnceTheHookIsSet()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = new UiSession();
		ui.Main.ShowCommand.Execute(Page.Settings);
		SettingsViewModel settings = ui.Main.Settings;
		settings.Show(SettingsCategory.About);
		ui.Pump();
		string version = typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
		Assert.Equal(version, settings.Version);
		TextBlock shown = Named<TextBlock>(ui, "AboutVersion");
		Assert.True(shown.IsEffectivelyVisible);
		Assert.Equal("Version " + version, string.Concat(shown.Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text)));
		Assert.Contains(ui.Window.GetVisualDescendants().OfType<Sde.App.Controls.Icon>(), i => i.IsEffectivelyVisible && i.Kind == "BrandMark" && i.Classes.Contains("aboutMark"));

		// The version left General.
		settings.Show(SettingsCategory.General);
		ui.Pump();
		Assert.False(shown.IsEffectivelyVisible);
		settings.Show(SettingsCategory.About);
		ui.Pump();

		// The main view model connects What's New (AppLevelUiTests checks it opens the sheet); a counting hook here.
		Assert.True(settings.HasWhatsNew);
		Assert.True(settings.OpenWhatsNewCommand.CanExecute(null));
		int shownWhatsNew = 0;
		settings.ShowWhatsNew = () => shownWhatsNew++;
		Assert.True(settings.OpenWhatsNewCommand.CanExecute(null));
		settings.OpenWhatsNewCommand.Execute(null);
		Assert.Equal(1, shownWhatsNew);
		ui.Pump();
		ui.Snapshot("31g-settings-about");
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));
	}

	/// <summary>Every category renders with its cards, light and dark, for the screenshots.</summary>
	[AvaloniaFact]
	public void Settings_EveryCategory_RendersInBothThemes()
	{
		BindingErrors.Instance.Messages.Clear();
		foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
		{
			using UiSession ui = new UiSession(theme);
			ScreenTourTests.AddSamples(ui.Store);
			ui.Services.RaiseDataChanged();
			ui.Main.ShowCommand.Execute(Page.Settings);
			foreach (SettingsCategoryItem category in ui.Main.Settings.Categories)
			{
				ui.Main.Settings.SelectedCategory = category;
				ui.Pump();
				Assert.Equal(category.Title, Named<TextBlock>(ui, "CategoryHeading").Text);
				ui.Snapshot($"32-{(theme == ThemeVariant.Dark ? "dark-" : "")}settings-{category.Category.ToString().ToLowerInvariant()}");
			}
		}
		Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));
	}

	private static string AutomationName(ListBoxItem item) => Avalonia.Automation.AutomationProperties.GetName(item);
}
