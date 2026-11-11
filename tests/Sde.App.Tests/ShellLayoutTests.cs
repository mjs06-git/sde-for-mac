using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Sde.App.Controls;
using Sde.App.Services;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Shell;
using Sde.App.ViewModels.Sheets;
using Sde.App.Views;
using Page = Sde.App.ViewModels.Page;

namespace Sde.App.Tests;

/// <summary>
/// The 3.5 window shell: the sidebar's sections (Pinned, Saved Views and Data, with Settings at the foot), pinned and
/// recent structures, the resizable and remembered layout, the toast's offset and the editor's grouped More menu.
/// </summary>
public class ShellLayoutTests
{
	private static UiSession WithSamples()
	{
		UiSession ui = new UiSession();
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		ui.Pump();
		return ui;
	}

	private static StructureRowViewModel Row(UiSession ui, string start) => ui.Main.Structures.Rows.First(r => r.Address.StartsWith(start));

	private static T Named<T>(UiSession ui, string name) where T : Control =>
		ui.Window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

	/// <summary>The sidebar's sections, top to bottom as the window shows them (only those with rows).</summary>
	private static List<string> ShownSections(UiSession ui) =>
		ui.Window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("sourceHeader") && t.IsEffectivelyVisible)
			.OrderBy(t => t.TranslatePoint(new Point(0, 0), ui.Window)?.Y ?? 0).Select(t => t.Text).ToList();

	[AvaloniaFact]
	public void Sidebar_HasItsSectionsInOrder_AndImportExportAndSettingsRowsSelect()
	{
		using UiSession ui = WithSamples();
		Assert.Equal(new[] { "Library", "Workflow", "Data" }, ShownSections(ui));

		Button importExport = ui.Window.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && AutomationProperties.GetName(b) == "Import & Export");
		Button settings = Named<Button>(ui, "SettingsRow");
		importExport.Command.Execute(null);
		ui.Pump();
		Assert.Equal(Page.ImportExport, ui.Main.ActivePage);
		Assert.Contains("active", importExport.Classes);
		Assert.DoesNotContain("active", settings.Classes);

		// The import page belongs to Import & Export.
		ui.Main.ShowCommand.Execute(Page.SourceImport);
		ui.Pump();
		Assert.Contains("active", importExport.Classes);

		settings.Command.Execute(null);
		ui.Pump();
		Assert.Equal(Page.Settings, ui.Main.ActivePage);
		Assert.Contains("active", settings.Classes);
		Assert.DoesNotContain("active", importExport.Classes);
		Assert.Single(ui.Main.SidebarItems, i => i.IsActive);

		// Settings stays at the foot below the other rows.
		double settingsY = settings.TranslatePoint(new Point(0, 0), ui.Window)!.Value.Y;
		Assert.True(settingsY > importExport.TranslatePoint(new Point(0, 0), ui.Window)!.Value.Y);
		Assert.True(settingsY + settings.Bounds.Height > ui.Window.Bounds.Height - 40);
	}

	[AvaloniaFact]
	public void Pinning_AddsASidebarRow_ThatOpensTheStructure_AndIsRememberedForTheDatabase()
	{
		using UiSession ui = WithSamples();
		StructuresViewModel list = ui.Main.Structures;
		StructureRowViewModel ranch = Row(ui, "123");
		list.Selected = ranch;
		Assert.False(list.SelectedIsPinned);
		list.PinCommand.Execute(ranch);
		ui.Pump();
		Assert.True(list.SelectedIsPinned);

		SourceSectionViewModel pinned = ui.Main.SourceSections.Single(s => s.Title == "Pinned");
		PinnedRowViewModel row = Assert.IsType<PinnedRowViewModel>(Assert.Single(pinned.Rows));
		Assert.Equal("123 River Street", row.Title);
		Assert.Equal(ranch.State, row.State);
		Assert.Equal(new[] { "Library", "Pinned", "Workflow", "Data" }, ShownSections(ui));
		Assert.Equal(new List<Guid> { ranch.Summary.PropertyID }, ui.Store.GetSetting<List<Guid>>(MainViewModel.PinnedSettingKey));
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("02-structures-pinned");

		// The row opens the structure's latest assessment.
		row.ShowCommand.Execute(null);
		ui.Pump();
		Assert.Equal(Page.Editor, ui.Main.ActivePage);
		Assert.Equal(ranch.AssessmentID, ui.Main.Editor.Assessment.AssessmentID);
		Assert.True(ui.Main.Editor.IsPinned);

		// The editor's More menu pins a second structure, and unpins it again.
		ui.Main.ShowStructures();
		ui.Main.OpenAssessment(Row(ui, "48").AssessmentID!.Value);
		ui.Main.Editor.PinCommand.Execute(null);
		Assert.Equal(2, pinned.Rows.Count);
		ui.Main.Editor.UnpinCommand.Execute(null);
		Assert.Single(pinned.Rows);
		ui.Main.ShowStructures();

		// Opening the same database again finds the pin.
		string path = ui.Store.Database.FilePath;
		ui.Services.Open(Path.Combine(ui.Folder, "other.sqlite"));
		ui.Pump();
		Assert.Empty(ui.Main.SourceSections.Single(s => s.Title == "Pinned").Rows);
		ui.Services.Open(path);
		ui.Pump();
		pinned = ui.Main.SourceSections.Single(s => s.Title == "Pinned");
		Assert.Equal("123 River Street", Assert.IsType<PinnedRowViewModel>(Assert.Single(pinned.Rows)).Title);

		// A deleted structure leaves the sidebar; Unpin from the row's menu forgets it.
		ui.Main.Structures.TrashCommand.Execute(Row(ui, "123"));
		ui.Services.RaiseDataChanged();
		ui.Pump();
		Assert.Empty(pinned.Rows);
		ui.Store.Restore(ui.Store.GetDeletedItems().Single().ItemKey);
		ui.Services.RaiseDataChanged();
		PinnedRowViewModel back = Assert.IsType<PinnedRowViewModel>(Assert.Single(pinned.Rows));
		back.UnpinCommand.Execute(null);
		Assert.Empty(pinned.Rows);
		Assert.Null(ui.Store.GetSetting<List<Guid>>(MainViewModel.PinnedSettingKey));
	}

	[AvaloniaFact]
	public void RecentStructures_AreThePalettesFirstGroup_WhenNothingIsTyped()
	{
		using UiSession ui = WithSamples();
		ui.Main.OpenAssessment(Row(ui, "123").AssessmentID!.Value);
		ui.Main.ShowStructures();
		ui.Main.OpenAssessment(Row(ui, "48").AssessmentID!.Value);
		ui.Main.ShowStructures();
		ui.Pump();

		CommandPaletteViewModel palette = ui.Main.Palette;
		palette.Open();
		ui.Pump();
		List<PaletteItem> recent = palette.Results.Where(r => r.Group == "Recent").ToList();
		Assert.Equal(new[] { "48 Mill Creek Street", "123 River Street" }, recent.Select(r => r.Title));
		Assert.Same(recent[0], palette.Results[0]);
		Assert.True(recent[0].IsFirstInGroup);
		ui.Snapshot("81b-command-palette-recent");

		// Typing searches instead; Recent goes.
		palette.Query = "river";
		Assert.DoesNotContain(palette.Results, r => r.Group == "Recent");
		palette.Close();

		// Kept per database, newest first, at most ten.
		AppSettings saved = AppSettings.Load(Path.Combine(ui.Folder, "app-settings.json"));
		Assert.Equal(2, saved.RecentStructuresIn(ui.Store.Database.FilePath).Count);
		AppSettings bounded = new AppSettings();
		for (int i = 0; i < 15; i++)
		{
			bounded.AddRecentStructure("/a.sqlite", Guid.NewGuid());
		}
		for (int i = 0; i < AppSettings.MaxRecentStructureDatabases + 5; i++)
		{
			bounded.AddRecentStructure($"/db{i}.sqlite", Guid.NewGuid());
		}
		Assert.Equal(AppSettings.MaxRecentStructureDatabases, bounded.RecentStructures.Count);
		Assert.All(bounded.RecentStructures.Values, ids => Assert.True(ids.Count <= AppSettings.MaxRecentStructures));
	}

	[AvaloniaFact]
	public async Task SavedViews_AreSidebarRows_ThatApplyTheView()
	{
		using UiSession ui = WithSamples();
		StructuresViewModel list = ui.Main.Structures;
		list.Filter = StructureFilter.Substantial;
		Task saving = list.SaveViewCommand.ExecuteAsync(null);
		NameSheetViewModel name = Assert.IsType<NameSheetViewModel>(ui.Main.Sheets.Current);
		name.Name = "Substantial only";
		name.OkCommand.Execute(null);
		await saving;
		list.Filter = StructureFilter.All;
		ui.Pump();

		SourceSectionViewModel views = ui.Main.SourceSections.Single(s => s.Title == "Saved Views");
		SavedViewRowViewModel row = Assert.IsType<SavedViewRowViewModel>(Assert.Single(views.Rows));
		Assert.Equal("Substantial only", row.Title);
		Assert.Equal(new[] { "Library", "Saved Views", "Workflow", "Data" }, ShownSections(ui));

		ui.Main.ShowCommand.Execute(Page.Review);
		row.ShowCommand.Execute(null);
		ui.Pump();
		Assert.Equal(Page.Structures, ui.Main.ActivePage);
		Assert.Equal(StructureFilter.Substantial, list.Filter);
		Assert.True(row.IsActive);
		Assert.Equal("Substantial only", ui.Main.PageTitle);
		Assert.DoesNotContain(ui.Main.SourceSections[0].Rows.OfType<SourceRowViewModel>(), r => r.IsActive);
		ui.Snapshot("02b-structures-saved-view");

		// Changing a filter leaves the view; a smart group is chosen again.
		list.Filter = StructureFilter.All;
		Assert.False(row.IsActive);
		Assert.True(ui.Main.SourceSections[0].Rows.OfType<SourceRowViewModel>().First().IsActive);

		// Rename and Delete from the row's menu.
		list.RenameSavedView("Substantial only", "Over the line");
		row = Assert.IsType<SavedViewRowViewModel>(Assert.Single(views.Rows));
		Assert.Equal("Over the line", row.Title);
		row.DeleteCommand.Execute(null);
		Assert.Empty(views.Rows);
		Assert.Empty(ui.Store.GetSavedViews());
	}

	[AvaloniaFact]
	public void Layout_IsClamped_AndRoundTripsThroughTheAppSettings()
	{
		using UiSession ui = WithSamples();
		MainViewModel main = ui.Main;
		Assert.Equal(MainViewModel.DefaultSidebarWidth, main.SidebarWidth);
		Assert.Equal(232, MainViewModel.DefaultSidebarWidth);
		main.SidebarWidth = 9999;
		Assert.Equal(MainViewModel.MaxSidebarWidth, main.SidebarWidth);
		main.SidebarWidth = 10;
		Assert.Equal(MainViewModel.MinSidebarWidth, main.SidebarWidth);
		main.InspectorWidth = 1000;
		Assert.Equal(MainViewModel.MaxInspectorWidth, main.InspectorWidth);

		// The sidebar and inspector panes follow the view model.
		main.SidebarWidth = 300;
		main.InspectorWidth = 320;
		ui.Pump();
		Assert.Equal(300, Named<Border>(ui, "Sidebar").Bounds.Width);
		Assert.Equal(320, Named<Border>(ui, "Inspector").Bounds.Width);

		// Dragging an edge: right widens the sidebar, left widens the inspector; a double-click resets.
		ResizeHandle sidebarEdge = Named<ResizeHandle>(ui, "SidebarResizer");
		sidebarEdge.Drag(20);
		Assert.Equal(320, main.SidebarWidth);
		ResizeHandle inspectorEdge = Named<ResizeHandle>(ui, "InspectorResizer");
		inspectorEdge.Drag(-20);
		Assert.Equal(340, main.InspectorWidth);
		inspectorEdge.ResetCommand.Execute(null);
		Assert.Equal(MainViewModel.DefaultInspectorWidth, main.InspectorWidth);
		inspectorEdge.Drag(-40);

		// The editor's result panel shares the inspector's width.
		ui.Main.OpenAssessment(Row(ui, "123").AssessmentID!.Value);
		ui.Pump();
		Assert.Equal(main.InspectorWidth, Named<Border>(ui, "ResultPanel").Bounds.Width);
		ui.Main.ShowStructures();

		main.IsSidebarVisible = false;
		main.Structures.IsInspectorVisible = false;
		main.SaveLayout();
		AppSettings saved = AppSettings.Load(Path.Combine(ui.Folder, "app-settings.json"));
		Assert.False(saved.Layout.SidebarVisible);
		Assert.Equal(320, saved.Layout.SidebarWidth);
		Assert.False(saved.Layout.InspectorVisible);
		Assert.Equal(MainViewModel.DefaultInspectorWidth + 40, saved.Layout.InspectorWidth);

		// Put back: hidden stays hidden, widths come back, and out-of-range widths are clamped.
		main.IsSidebarVisible = true;
		main.SidebarWidth = 200;
		main.Structures.IsInspectorVisible = true;
		main.RestoreLayout(saved.Layout);
		Assert.False(main.IsSidebarVisible);
		Assert.Equal(320, main.SidebarWidth);
		Assert.False(main.Structures.IsInspectorVisible);
		main.RestoreLayout(new LayoutState { SidebarWidth = 5000, InspectorWidth = 1 });
		Assert.True(main.IsSidebarVisible);
		Assert.Equal(MainViewModel.MaxSidebarWidth, main.SidebarWidth);
		Assert.Equal(MainViewModel.MinInspectorWidth, main.InspectorWidth);
		main.RestoreLayout(new LayoutState());
		Assert.Equal(MainViewModel.DefaultSidebarWidth, main.SidebarWidth);
	}

	[AvaloniaFact]
	public void ColumnWidths_AreRemembered_WithoutTouchingTheListsSortOrColumns()
	{
		using UiSession ui = WithSamples();
		ui.Main.Structures.SortBy("Owner");
		ui.Main.Structures.Columns.Single(c => c.Key == "Zip").IsVisible = true;
		DataGrid grid = Named<DataGrid>(ui, "Grid");
		Assert.Equal("Structures", TableLayout.GetKey(grid));
		DataGridColumn owner = grid.Columns.Single(c => c.SortMemberPath == "Owner");
		DataGridLength ownerDefault = owner.Width;
		owner.Width = new DataGridLength(210);
		TableLayout.Remember(grid);
		Assert.Equal("210", Assert.Single(ui.Main.ColumnWidths("Structures")).Value);

		ui.Main.SaveLayout();
		AppSettings saved = AppSettings.Load(Path.Combine(ui.Folder, "app-settings.json"));
		Assert.Equal("210", saved.Layout.ColumnWidths["Structures"]["Owner"]);

		// A new copy of the page (as after visiting another one) gets the width back.
		ui.Main.ShowCommand.Execute(Page.Review);
		ui.Pump();
		ui.Main.ShowCommand.Execute(Page.Structures);
		ui.Pump();
		grid = Named<DataGrid>(ui, "Grid");
		owner = grid.Columns.Single(c => c.SortMemberPath == "Owner");
		Assert.Equal(DataGridLengthUnitType.Pixel, owner.Width.UnitType);
		Assert.Equal(210, owner.Width.Value);
		Assert.Equal(ownerDefault.UnitType, grid.Columns.Single(c => c.SortMemberPath == "Result").Width.UnitType);
		Assert.Equal("Owner", ui.Main.Structures.SortKey);
		Assert.True(grid.Columns.Single(c => c.SortMemberPath == "Zip").IsVisible);

		// Back to the view's width: nothing is kept.
		owner.Width = ownerDefault;
		TableLayout.Remember(grid);
		Assert.Empty(ui.Main.ColumnWidths("Structures"));
		Assert.Equal("1.5*", TableLayout.Format(new DataGridLength(1.5, DataGridLengthUnitType.Star)));
		DataGridLength parsed = TableLayout.Parse("1.5*")!.Value;
		Assert.Equal((1.5, DataGridLengthUnitType.Star), (parsed.Value, parsed.UnitType));
		Assert.Null(TableLayout.Parse("wide"));
	}

	[AvaloniaFact]
	public void Toasts_CentreOnThePage_FollowingTheSidebarsWidth()
	{
		using UiSession ui = WithSamples();
		Panel host = Named<Panel>(ui, "ToastHost");
		Assert.Equal(ui.Main.SidebarWidth, host.Margin.Left);
		ui.Main.SidebarWidth = 300;
		ui.Pump();
		Assert.Equal(300, host.Margin.Left);
		ui.Main.IsSidebarVisible = false;
		ui.Pump();
		Assert.Equal(0, host.Margin.Left);
		Assert.False(Named<Panel>(ui, "SidebarHost").IsVisible);
		Button show = ui.Window.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Show sidebar");
		Assert.True(show.IsEffectivelyVisible);
		Assert.Equal(ui.Window.FindResource("TrafficLightInset"), show.Margin);
		ui.Main.IsSidebarVisible = true;
		ui.Pump();
		Assert.Equal(300, host.Margin.Left);
	}

	/// <summary>The More menu's commands before 3.5 grouped it; none may go missing (Pin and Unpin are new).</summary>
	[AvaloniaFact]
	public void EditorMoreMenu_IsGrouped_AndKeepsEveryCommand()
	{
		using UiSession ui = WithSamples();
		ui.Main.OpenAssessment(Row(ui, "123").AssessmentID!.Value);
		ui.Pump();
		Button more = Named<Button>(ui, "MoreButton");
		MenuFlyout flyout = Assert.IsType<MenuFlyout>(more.Flyout);
		flyout.Popup.ShouldUseOverlayLayer = true;
		flyout.ShowAt(more);
		ui.Pump();
		ui.Snapshot("16b-editor-more-menu");
		List<MenuItem> items = flyout.Items.OfType<MenuItem>().ToList();
		string[] before =
		{
			"Save Now", "Undo", "Redo", "History…", "Comments…", "Copy from Another Structure…", "Set Event…", "Elevation Certificate…",
			"Flood Depth from High-Water Marks…", "Copy from a Work Package…", "Improvements and SI…", "Appeal…", "Compliance Case…",
			"Mark Letter as Sent…", "Move to Recently Deleted",
		};
		List<MenuItem> commands = items.Where(i => !i.Classes.Contains("menuGroup")).ToList();
		Assert.All(before, h => Assert.Single(commands, i => (string)i.Header == h && i.Command != null));
		Assert.Equal(before.Length + 2, commands.Count);                        // and Pin, Unpin
		Assert.Equal(Avalonia.Input.KeyGesture.Parse("Cmd+S"), commands.Single(i => (string)i.Header == "Save Now").InputGesture);
		Assert.Equal(Avalonia.Input.KeyGesture.Parse("Cmd+Shift+Z"), commands.Single(i => (string)i.Header == "Redo").InputGesture);
		Assert.Equal(new[] { "This Assessment", "Structure", "Fill In", "Letters and Determinations" },
			items.Where(i => i.Classes.Contains("menuGroup")).Select(i => (string)i.Header));
		MenuItem delete = items.Last();
		Assert.Equal("Move to Recently Deleted", delete.Header);
		Assert.Contains("destructive", delete.Classes);
		flyout.Hide();
	}
}
