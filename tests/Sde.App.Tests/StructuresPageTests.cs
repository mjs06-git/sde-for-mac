using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using ClosedXML.Excel;
using Newtonsoft.Json;
using Sde.App.Services;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Sheets;
using Sde.App.ViewModels.Shell;
using Sde.Data;
using Sde.Reports;

namespace Sde.App.Tests;

/// <summary>
/// The Structures page's 3.5 features: the filter bar with chips, tags (column, inspector, menus, Manage Tags, the
/// sidebar's Tags and the palette), Export This List, the Submission Folder sheet and the empty state's sample.
/// </summary>
public class StructuresPageTests
{
	private static UiSession WithSamples(ThemeVariant theme = null, int width = 1440)
	{
		UiSession ui = new UiSession(theme, width);
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		ui.Pump();
		return ui;
	}

	private static StructureRowViewModel Row(StructuresViewModel s, string start) => s.Rows.First(r => r.Address.StartsWith(start));

	private static TagViewModel MakeTag(StructuresViewModel s, string name, string colour)
	{
		TagViewModel tag = s.CreateTag(name, colour, out string problem);
		Assert.Null(problem);
		return tag;
	}

	/// <summary>The fields of ListViewState as SDE 3.0 wrote and read them (to check a 3.5 view doesn't trouble 3.0).</summary>
	private sealed class ListViewState30
	{
		public string Search { get; set; }
		public StructureFilter Filter { get; set; }
		public int TypeFilter { get; set; }
		public int SourceFilter { get; set; }
		public DateTime? InspectedFrom { get; set; }
		public DateTime? InspectedTo { get; set; }
		public decimal? MinPercent { get; set; }
		public decimal? MaxPercent { get; set; }
		public string Street { get; set; }
		public string Zip { get; set; }
		public string Parcel { get; set; }
		public string Status { get; set; }
		public string SortKey { get; set; }
		public bool SortDescending { get; set; }
		public List<string> Columns { get; set; }
	}

	[AvaloniaFact]
	public void FilterBar_AddsChangesAndRemovesChips()
	{
		using UiSession ui = WithSamples();
		StructuresViewModel s = ui.Main.Structures;
		Assert.Empty(s.FilterChips);
		Assert.False(s.HasActiveFilters);
		// Source is offered once something was imported, Tags once a tag exists.
		Assert.Equal(new[] { "Type", "Status", "Inspector", "Inspected", "Damaged", "Street", "Zip", "Parcel" }, s.FilterFields.Select(f => f.Key));

		// Add Filter, then Type: a chip with no value yet, which filters nothing.
		s.AddFilterCommand.Execute("Type");
		ui.Pump();
		ChoiceFilterChipViewModel type = Assert.IsType<ChoiceFilterChipViewModel>(Assert.Single(s.FilterChips));
		Assert.Equal("Type: Any", type.Label);
		Assert.Equal(3, s.Rows.Count);
		Assert.False(s.HasActiveFilters);

		// Choosing a value filters the list; choosing another changes it.
		type.SelectedIndex = 0;
		Assert.Equal("Type: Residential", type.Label);
		Assert.Equal(1, s.TypeFilter);
		Assert.Equal(2, s.Rows.Count);
		Assert.True(s.HasActiveFilters);
		type.SelectedIndex = 1;
		Assert.Equal("Type: Non-residential", type.Label);
		Assert.Equal(new[] { "700 Riverside" }, s.Rows.Select(r => r.Address));
		type.SelectedIndex = 0;

		// The inspector filter offers the inspectors in the list.
		s.AddFilterCommand.Execute("Inspector");
		ChoiceFilterChipViewModel inspector = Assert.IsType<ChoiceFilterChipViewModel>(s.FilterChips.Single(c => c.Key == "Inspector"));
		Assert.Contains("Inspector A", inspector.Options);
		inspector.SelectedIndex = inspector.Options.ToList().IndexOf("Inspector A");
		Assert.Equal("Inspector: Inspector A", inspector.Label);
		Assert.Equal("Inspector A", s.InspectorFilter);

		// A text filter keeps its chip while it is edited, even when emptied to type again.
		s.AddFilterCommand.Execute("Street");
		TextFilterChipViewModel street = Assert.IsType<TextFilterChipViewModel>(s.FilterChips.Single(c => c.Key == "Street"));
		street.Text = "river";
		Assert.Equal("Street: river", street.Label);
		Assert.Equal(new[] { "123 River Street" }, s.Rows.Select(r => r.Address));
		street.Text = "";
		Assert.Contains(street, s.FilterChips);
		// Its editor closing with no value takes the chip away.
		s.DropPendingFilter("Street");
		Assert.DoesNotContain(street, s.FilterChips);
		Assert.Equal(new[] { "Type", "Inspector" }, s.FilterChips.Select(c => c.Key));

		// The chips are on screen, neutral outlined chips in the bar.
		ui.Pump();
		List<Border> chips = ui.Window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("filterChip") && b.IsEffectivelyVisible).ToList();
		Assert.Equal(2, chips.Count);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));

		// The percent and date filters say their range in words.
		s.MinPercent = 50;
		Assert.Equal("Damaged: at least 50%", s.FilterChips.Single(c => c.Key == "Damaged").Label);
		s.MaxPercent = 70;
		Assert.Equal("Damaged: 50% to 70%", s.FilterChips.Single(c => c.Key == "Damaged").Label);
		s.InspectedFrom = new DateTime(2026, 9, 1);
		Assert.Equal("Inspected: from Sep 1, 2026", s.FilterChips.Single(c => c.Key == "Inspected").Label);

		// A chip's × takes its filter off; Clear takes them all off.
		type.RemoveCommand.Execute(null);
		Assert.Equal(0, s.TypeFilter);
		Assert.DoesNotContain(type, s.FilterChips);
		s.ClearFiltersCommand.Execute(null);
		Assert.Empty(s.FilterChips);
		Assert.Null(s.InspectorFilter);
		Assert.Null(s.MinPercent);
		Assert.Equal(3, s.Rows.Count);
	}

	[AvaloniaFact]
	public void FilterBar_IsRemembered_AndSavedViewsFrom30And35Load()
	{
		using UiSession ui = WithSamples();
		StructuresViewModel s = ui.Main.Structures;
		TagViewModel revisit = MakeTag(s, "Revisit", TagColours.Amber);
		s.AddTag(revisit.Id, new[] { Row(s, "123").Summary.PropertyID });
		s.InspectorFilter = "Inspector A";
		s.TagFilter = new[] { revisit.Id };
		s.TagsMatchAll = true;
		Assert.Equal(new[] { "123 River Street" }, s.Rows.Select(r => r.Address));

		// Remembered for this database.
		StructuresViewModel again = new StructuresViewModel(ui.Services, ui.Main);
		Assert.Equal("Inspector A", again.InspectorFilter);
		Assert.Equal(new[] { revisit.Id }, again.TagFilter);
		Assert.True(again.TagsMatchAll);
		Assert.Equal(new[] { "Inspector: Inspector A", "Tags: Revisit" }, again.FilterChips.Select(c => c.Label));

		// A view saved by 3.5 keeps the new filters.
		string json = JsonConvert.SerializeObject(s.CurrentState());
		ui.Store.SaveView("Revisits", json);
		s.ClearFiltersCommand.Execute(null);
		s.ApplyViewCommand.Execute("Revisits");
		Assert.Equal(new[] { revisit.Id }, s.TagFilter);
		Assert.Equal("Inspector A", s.InspectorFilter);
		Assert.Equal("Revisits", s.AppliedView);

		// ...and SDE 3.0 reads it without trouble (it ignores what it doesn't know).
		ListViewState30 old = JsonConvert.DeserializeObject<ListViewState30>(json);
		Assert.NotNull(old);
		Assert.Equal(s.SortKey, old.SortKey);

		// A view saved by 3.0 loads, with the new filters off.
		const string from30 = """{"Search":null,"Filter":0,"TypeFilter":1,"SourceFilter":0,"InspectedFrom":null,"InspectedTo":null,"MinPercent":null,"MaxPercent":null,"Street":"river","Zip":null,"Parcel":null,"Status":null,"SortKey":"Damaged","SortDescending":true,"Columns":["Owner","Type","Inspected","Status"]}""";
		ui.Store.SaveView("Old houses", from30);
		s.ApplyViewCommand.Execute("Old houses");
		Assert.Equal(1, s.TypeFilter);
		Assert.Equal("river", s.StreetFilter);
		Assert.Null(s.InspectorFilter);
		Assert.Empty(s.TagFilter);
		Assert.False(s.IsColumnVisible("Tags"));
		Assert.Equal(new[] { "Type: Residential", "Street: river" }, s.FilterChips.Select(c => c.Label));
		Assert.Equal(new[] { "123 River Street" }, s.Rows.Select(r => r.Address));
	}

	[AvaloniaFact]
	public async Task Tags_FromTheMenusAndInspector_ToTheSidebarFilterAndPalette()
	{
		using UiSession ui = WithSamples();
		StructuresViewModel s = ui.Main.Structures;
		SourceSectionViewModel section = ui.Main.SourceSections.Single(x => x.Title == "Tags");
		Assert.Empty(section.Rows);
		Assert.False(s.IsColumnVisible("Tags"));

		// Change ▾ › Tags › New Tag, with two structures checked: the tag goes on both, and the Tags column comes on.
		Row(s, "123").IsChecked = true;
		Row(s, "48").IsChecked = true;
		Task making = s.NewTagCommand.ExecuteAsync(null);
		TagSheetViewModel sheet = Assert.IsType<TagSheetViewModel>(ui.Main.Sheets.Current);
		Assert.Equal("It goes on 2 structures.", sheet.Intro);
		sheet.Name = "Revisit";
		sheet.ChooseColourCommand.Execute(sheet.Colours.Single(c => c.Colour == TagColours.Amber));
		ui.Pump();
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		sheet.SaveCommand.Execute(null);
		await making;
		TagViewModel revisit = Assert.Single(s.AllTags);
		Assert.Equal(TagColours.Amber, revisit.Colour);
		Assert.Equal("TagAmberDot", revisit.DotKey);
		Assert.Equal(new[] { "Revisit" }, Row(s, "123").Tags.Select(t => t.Name));
		Assert.Equal(new[] { "Revisit" }, Row(s, "48").Tags.Select(t => t.Name));
		Assert.Empty(Row(s, "700").Tags);
		Assert.True(s.IsColumnVisible("Tags"));
		TagRowViewModel sidebarRow = Assert.IsType<TagRowViewModel>(Assert.Single(section.Rows));
		Assert.Equal(2, sidebarRow.Count);
		Assert.Contains(TagsFilterKey(), s.FilterFields.Select(f => f.Key));

		// The inspector: type a new name, pick a colour, create; then add an existing tag by typing part of it.
		s.ClearCheckedCommand.Execute(null);
		s.Selected = Row(s, "700");
		Assert.False(s.HasSelectedTags);
		s.TagQuery = "Owner called";
		Assert.True(s.CanCreateTag);
		s.ChooseNewTagColourCommand.Execute(s.NewTagColours.Single(c => c.Colour == TagColours.Green));
		s.CreateTagForSelectedCommand.Execute(null);
		Assert.Equal("700 Riverside", s.Selected.Address);
		Assert.Equal(new[] { "Owner called" }, s.SelectedTags.Select(t => t.Name));
		Assert.Equal(TagColours.Green, s.SelectedTags[0].Colour);
		s.TagQuery = "rev";
		TagViewModel suggestion = Assert.Single(s.TagSuggestions);
		s.AddTagToSelectedCommand.Execute(suggestion);
		Assert.Equal(new[] { "Revisit", "Owner called" }, s.SelectedTags.Select(t => t.Name));
		Assert.Equal(3, ((TagRowViewModel)section.Rows[0]).Count);
		TagViewModel owner = s.AllTags.Single(t => t.Name == "Owner called");

		// The Tags menu ticks the tags the selected structure has.
		List<MenuItem> menu = s.TagMenuItems.OfType<MenuItem>().ToList();
		Assert.Equal(new[] { "Revisit", "Owner called", "New Tag…", "Manage Tags…" }, menu.Select(m => (string)m.Header));
		Assert.True(menu[0].IsChecked);
		Assert.True(menu[1].IsChecked);
		Assert.Equal(MenuItemToggleType.CheckBox, menu[0].ToggleType);

		// A batch: two checked structures that both have Revisit lose it; choosing it again puts it back on both.
		Row(s, "123").IsChecked = true;
		Row(s, "48").IsChecked = true;
		Assert.True(s.AllHaveTag(revisit.Id, s.TagTargets()));
		s.ToggleTagCommand.Execute(revisit);
		Assert.Empty(Row(s, "123").Tags);
		Assert.Empty(Row(s, "48").Tags);
		Assert.Equal(1, ((TagRowViewModel)section.Rows[0]).Count);
		s.ToggleTagCommand.Execute(revisit);
		Assert.Equal(3, ((TagRowViewModel)section.Rows[0]).Count);
		// Owner called goes on the two as well, so it is on all three.
		s.ToggleTagCommand.Execute(owner);
		Assert.Equal(3, ((TagRowViewModel)section.Rows[1]).Count);
		s.ToggleTagCommand.Execute(owner);
		Assert.Equal(1, ((TagRowViewModel)section.Rows[1]).Count);
		s.ClearCheckedCommand.Execute(null);

		// The tag filter: any of, or all of.
		s.TagFilter = new[] { revisit.Id, owner.Id };
		Assert.Equal(3, s.Rows.Count);
		Assert.Equal("Tags: Revisit or Owner called", s.FilterChips.Single().Label);
		s.TagsMatchAll = true;
		Assert.Equal(new[] { "700 Riverside" }, s.Rows.Select(r => r.Address));
		Assert.Equal("Tags: Revisit and Owner called", s.FilterChips.Single().Label);
		TagsFilterChipViewModel chip = Assert.IsType<TagsFilterChipViewModel>(s.FilterChips.Single());
		Assert.Equal(new[] { true, true }, chip.Choices.Select(c => c.IsSelected));
		chip.Choices[0].IsSelected = false;
		Assert.Equal(new[] { owner.Id }, s.TagFilter);
		s.ClearFiltersCommand.Execute(null);

		// The sidebar's tag row shows only that tag's structures, and is the row chosen.
		sidebarRow = (TagRowViewModel)section.Rows[1];
		sidebarRow.ShowCommand.Execute(null);
		Assert.Equal(new[] { owner.Id }, s.EffectiveTagFilter);
		Assert.Equal(new[] { "700 Riverside" }, s.Rows.Select(r => r.Address));
		Assert.True(sidebarRow.IsActive);
		SourceRowViewModel all = ui.Main.SourceSections[0].Rows.OfType<SourceRowViewModel>().First();
		Assert.False(all.IsActive);
		// All Structures leaves the tag.
		all.ShowCommand.Execute(null);
		Assert.Empty(s.EffectiveTagFilter);
		Assert.False(sidebarRow.IsActive);
		Assert.True(all.IsActive);
		Assert.Equal(3, s.Rows.Count);

		// ⌘K finds a tag as "Tag: Name" and shows its structures.
		ui.Main.ShowCommand.Execute(ViewModels.Page.Dashboard);
		ui.Main.Palette.Open();
		ui.Main.Palette.Query = "revisit";
		PaletteItem found = ui.Main.Palette.Results.Single(r => r.Title == "Tag: Revisit");
		ui.Main.Palette.Run(found);
		Assert.Equal(ViewModels.Page.Structures, ui.Main.ActivePage);
		Assert.Equal(new[] { revisit.Id }, s.EffectiveTagFilter);
		Assert.Equal(3, s.Rows.Count);
	}

	private static string TagsFilterKey() => StructuresViewModel.TagsFilterKey;

	[AvaloniaFact]
	public async Task ManageTags_RenamesRecoloursReordersAndDeletes()
	{
		using UiSession ui = WithSamples();
		StructuresViewModel s = ui.Main.Structures;
		TagViewModel revisit = MakeTag(s, "Revisit", TagColours.Amber);
		TagViewModel owner = MakeTag(s, "Owner called", TagColours.Green);
		TagViewModel elevate = MakeTag(s, "Elevation candidate", TagColours.Blue);
		s.AddTag(revisit.Id, new[] { Row(s, "123").Summary.PropertyID, Row(s, "48").Summary.PropertyID });
		s.AddTag(owner.Id, new[] { Row(s, "700").Summary.PropertyID });

		Task managing = s.ManageTagsCommand.ExecuteAsync(null);
		ManageTagsSheetViewModel sheet = Assert.IsType<ManageTagsSheetViewModel>(ui.Main.Sheets.Current);
		Assert.Equal(new[] { "Revisit", "Owner called", "Elevation candidate" }, sheet.Rows.Select(r => r.Name));
		Assert.Equal(new[] { "2 structures", "1 structure", "0 structures" }, sheet.Rows.Select(r => r.CountText));
		ui.Pump();
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("123-manage-tags");

		// Rename: a good name is saved as it is typed; a blank or taken one says why and isn't.
		ManageTagRowViewModel row = sheet.Rows[0];
		row.Name = "Revisit soon";
		Assert.Null(row.Problem);
		Assert.Equal("Revisit soon", ui.Store.GetTags().Single(t => t.TagID == revisit.Id).Name);
		row.Name = " ";
		Assert.Equal("Give the tag a name.", row.Problem);
		row.Name = "owner called";
		Assert.Equal("There's already a tag called Owner called.", row.Problem);
		Assert.Equal("Revisit soon", ui.Store.GetTags().Single(t => t.TagID == revisit.Id).Name);
		row.Name = "Revisit soon";

		// Recolour and reorder.
		row.ChooseColourCommand.Execute(row.Colours.Single(c => c.Colour == TagColours.Violet));
		Assert.Equal(TagColours.Violet, ui.Store.GetTags().Single(t => t.TagID == revisit.Id).Colour);
		Assert.Equal("TagVioletDot", row.DotKey);
		sheet.Rows[2].MoveUpCommand.Execute(null);
		Assert.Equal(new[] { "Revisit soon", "Elevation candidate", "Owner called" }, ui.Store.GetTags().Select(t => t.Name));

		// Delete asks first, saying how many structures lose the tag; No keeps it.
		ui.Dialogs.Answers.Enqueue(false);
		await sheet.Rows[0].DeleteCommand.ExecuteAsync(null);
		Assert.Contains("2 structures lose this tag.", ui.Dialogs.Confirmations.Last());
		Assert.Equal(3, ui.Store.GetTags().Count);
		await sheet.Rows[0].DeleteCommand.ExecuteAsync(null);
		Assert.Equal(2, ui.Store.GetTags().Count);
		Assert.Equal(2, sheet.Rows.Count);

		sheet.DoneCommand.Execute(null);
		await managing;
		Assert.Equal(new[] { "Elevation candidate", "Owner called" }, s.AllTags.Select(t => t.Name));
		Assert.Empty(Row(s, "123").Tags);
		Assert.Equal(new[] { "Elevation candidate", "Owner called" }, ui.Main.SourceSections.Single(x => x.Title == "Tags").Rows.OfType<TagRowViewModel>().Select(r => r.Title));
	}

	[AvaloniaFact]
	public async Task ExportThisList_WritesTheShownRowsAndColumns_WithRealNumbersAndDates()
	{
		using UiSession ui = WithSamples();
		StructuresViewModel s = ui.Main.Structures;
		TagViewModel revisit = MakeTag(s, "Revisit", TagColours.Amber);
		TagViewModel owner = MakeTag(s, "Owner called", TagColours.Green);
		s.AddTag(revisit.Id, new[] { Row(s, "123").Summary.PropertyID });
		s.AddTag(owner.Id, new[] { Row(s, "123").Summary.PropertyID });
		s.Columns.Single(c => c.Key == "Tags").IsVisible = true;
		s.Columns.Single(c => c.Key == "Zip").IsVisible = true;
		s.Columns.Single(c => c.Key == "Owner").IsVisible = false;
		s.SortBy("Damaged");

		string csv = Path.Combine(ui.Folder, "list.csv");
		ui.Dialogs.Answers.Enqueue(csv);
		await s.ExportListToCsvCommand.ExecuteAsync(null);
		string[] lines = File.ReadAllLines(csv);
		Assert.Equal("Structure,Type,Inspected,Status,Tags,ZIP,Damaged (%),Result", lines[0]);
		Assert.Equal(4, lines.Length);
		Assert.StartsWith("123 River Street,Residential,", lines[1]);
		Assert.Contains(",Draft,Revisit; Owner called,", lines[1]);
		Assert.EndsWith(",75.0,Substantially Damaged", lines[1]);
		Assert.StartsWith("48 Mill Creek Street,", lines[2]);
		Assert.StartsWith("700 Riverside,", lines[3]);
		Assert.EndsWith(",,Data Entry Incomplete", lines[3]);
		Assert.Contains(ui.Dialogs.Notifications, n => n.StartsWith("List exported: 3 structures"));

		// The cells are real numbers and dates, not text.
		IReadOnlyList<object> first = s.ListExportCells()[0];
		Assert.IsType<decimal>(first[6]);
		Assert.IsType<DateTime>(first[2]);

		// Checked rows only, to Excel; the search narrows it too.
		Row(s, "48").IsChecked = true;
		string xlsx = Path.Combine(ui.Folder, "list.xlsx");
		ui.Dialogs.Answers.Enqueue(xlsx);
		await s.ExportListToExcelCommand.ExecuteAsync(null);
		using (XLWorkbook wb = new XLWorkbook(xlsx))
		{
			IXLWorksheet ws = wb.Worksheets.First();
			Assert.Equal("Damaged (%)", ws.Cell(1, 7).GetString());
			Assert.Equal("48 Mill Creek Street", ws.Cell(2, 1).GetString());
			Assert.Equal(XLDataType.Number, ws.Cell(2, 7).DataType);
			Assert.Equal(67.5, ws.Cell(2, 7).GetDouble(), 3);
			Assert.Equal(XLDataType.DateTime, ws.Cell(2, 3).DataType);
			Assert.True(ws.Cell(3, 1).IsEmpty());
		}
		s.ClearCheckedCommand.Execute(null);
		s.Search = "river";
		Assert.Equal(2, s.ListExportRows().Count);
		Assert.Equal(new[] { "123 River Street", "700 Riverside" }, s.ListExportRows().Select(r => r.Address));

		// Cancelling the save writes nothing.
		File.Delete(csv);
		await s.ExportListToCsvCommand.ExecuteAsync(null);
		Assert.False(File.Exists(csv));
	}

	[AvaloniaFact]
	public async Task SubmissionSheet_PassesTheAlsoIncludeChoices_AndShowsWarnings()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = WithSamples();
		StructuresViewModel s = ui.Main.Structures;
		// A document whose file has gone: the folder is written, with a warning.
		string source = Path.Combine(ui.Folder, "bid.pdf");
		File.WriteAllText(source, "%PDF-1.4 made-up");
		StructureRowViewModel row = Row(s, "123");
		DocumentRecord doc = ui.Store.AddDocument(row.Summary.PropertyID, source, "Other", "Contractor bid");
		File.Delete(ui.Store.DocumentPath(doc));

		s.OpenSubmissionCommand.Execute(null);
		SubmissionSheetViewModel sheet = Assert.IsType<SubmissionSheetViewModel>(ui.Main.Sheets.Current);
		SubmissionViewModel sub = sheet.Submission;
		Assert.False(sub.IncludeLetters || sub.IncludeDocuments || sub.IncludeComplianceReport || sub.IncludeEventSummary || sub.IncludeCaseFiles);
		ui.Pump();
		Assert.Contains(ui.Window.GetVisualDescendants().OfType<CheckBox>(), c => c.Content as string == "Event summary" && c.IsEffectivelyVisible);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		sub.IncludeDocuments = true;
		sub.IncludeEventSummary = true;
		ui.Snapshot("125-submission-sheet");

		string parent = Path.Combine(ui.Folder, "out");
		Directory.CreateDirectory(parent);
		ui.Dialogs.Answers.Enqueue(parent);
		await sub.WriteCommand.ExecuteAsync(null);
		string folder = Assert.Single(Directory.GetDirectories(parent));
		Assert.True(File.Exists(Path.Combine(folder, SubmissionFolder.EventSummaryFile)));
		Assert.False(File.Exists(Path.Combine(folder, SubmissionFolder.ComplianceFile)));
		string warning = Assert.Single(sub.Warnings);
		Assert.Contains("Contractor bid", warning);
		// With warnings the sheet stays open to show them.
		Assert.True(sub.IsOpen);
		Assert.Same(sheet, ui.Main.Sheets.Current);
		ui.Pump();
		Assert.True(ui.Window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SubmissionWarnings").IsEffectivelyVisible);
		ui.Snapshot("125b-submission-sheet-warnings");
		sub.CancelCommand.Execute(null);
		Assert.Null(ui.Main.Sheets.Current);

		// Esc closes it too, and opening it again starts with no warnings.
		s.OpenSubmissionCommand.Execute(null);
		Assert.Empty(sub.Warnings);
		ui.Main.Sheets.CancelCurrentCommand.Execute(null);
		Assert.False(sub.IsOpen);
		Assert.Null(ui.Main.Sheets.Current);
		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));
	}

	[AvaloniaFact]
	public async Task EmptyState_OffersTheSampleDatabase()
	{
		using UiSession ui = new UiSession();
		ui.Services.SampleFolder = Path.Combine(ui.Folder, SampleDatabase.FolderName);
		StructuresViewModel s = ui.Main.Structures;
		Assert.True(s.IsEmpty);
		Assert.True(s.CanOpenSample);
		ui.Pump();
		Button sample = ui.Window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Open Sample Database");
		Assert.True(sample.IsEffectivelyVisible);
		Assert.Same(ui.Main.OpenSampleDatabaseCommand, sample.Command);

		await ui.Main.OpenSampleDatabaseCommand.ExecuteAsync(null);
		ui.Pump();
		Assert.True(ui.Main.IsSampleDatabase);
		Assert.False(s.IsEmpty);
		Assert.False(s.CanOpenSample);
		ui.Main.LeaveSampleDatabaseCommand.Execute(null);
	}

	/// <summary>The screenshots of the page's new parts, light and dark.</summary>
	[AvaloniaFact]
	public void Screens_FilterChipsTagsInspectorAndExportMenu()
	{
		foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
		{
			string suffix = theme == ThemeVariant.Dark ? "-dark" : "";
			using UiSession ui = WithSamples(theme);
			StructuresViewModel s = ui.Main.Structures;
			TagViewModel revisit = MakeTag(s, "Revisit", TagColours.Amber);
			TagViewModel owner = MakeTag(s, "Owner called", TagColours.Green);
			TagViewModel elevate = MakeTag(s, "Elevation candidate", TagColours.Blue);
			s.AddTag(revisit.Id, new[] { Row(s, "123").Summary.PropertyID, Row(s, "700").Summary.PropertyID });
			s.AddTag(owner.Id, new[] { Row(s, "123").Summary.PropertyID });
			s.AddTag(elevate.Id, new[] { Row(s, "48").Summary.PropertyID });
			s.Columns.Single(c => c.Key == "Tags").IsVisible = true;
			s.TypeFilter = 1;
			s.InspectorFilter = "Inspector A";
			s.Selected = Row(s, "123");
			// The list with the inspector hidden (⌥⌘I): at 1440 the default columns and Tags need the inspector's room to
			// show tag names (beside it the Tags column shows each tag's dot, or the table scrolls sideways a little).
			s.IsInspectorVisible = false;
			ui.Pump();
			Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
			ui.Snapshot("120-structures-filters-tags" + suffix);
			s.IsInspectorVisible = true;
			ui.Pump();

			if (theme == ThemeVariant.Light)
			{
				// A chip's editor, open under it.
				Button label = ui.Window.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("filterChipLabel"));
				PopupFlyoutBase editor = Assert.IsAssignableFrom<PopupFlyoutBase>(label.Flyout);
				editor.Popup.ShouldUseOverlayLayer = true;
				editor.ShowAt(label);
				ui.Pump();
				ui.Snapshot("121-structures-filter-editor");
				editor.Hide();

				// The inspector with its tags and Add Tag open (a 14-inch MacBook Pro's default width, where the default
				// columns and Tags fit beside the inspector).
				ui.Window.Width = 1512;
				DropDownButton addTag = ui.Window.GetVisualDescendants().OfType<DropDownButton>().Single(b => b.Name == "AddTagButton");
				PopupFlyoutBase picker = Assert.IsAssignableFrom<PopupFlyoutBase>(addTag.Flyout);
				picker.Popup.ShouldUseOverlayLayer = true;
				s.TagQuery = "Elev";
				Assert.Single(s.TagSuggestions);
				Assert.True(s.CanCreateTag);
				picker.ShowAt(addTag);
				ui.Pump();
				ui.Snapshot("122-structures-inspector-tags");
				picker.Hide();
				s.TagQuery = null;
				ui.Window.Width = 1440;

				// The Export menu.
				DropDownButton export = ui.Window.GetVisualDescendants().OfType<DropDownButton>().Single(b => b.Name == "ExportButton");
				MenuFlyout menu = Assert.IsType<MenuFlyout>(export.Flyout);
				menu.Popup.ShouldUseOverlayLayer = true;
				menu.ShowAt(export);
				ui.Pump();
				List<string> headers = menu.Items.OfType<MenuItem>().Select(m => m.Header as string).ToList();
				Assert.Contains("FEMA Excel Workbook…", headers);
				Assert.Contains("This List to CSV…", headers);
				Assert.Contains("This List to Excel…", headers);
				ui.Snapshot("124-structures-export-menu");
				menu.Hide();
			}
		}
	}
}
