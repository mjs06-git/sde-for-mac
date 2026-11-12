using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Sde.App.Controls;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Sheets;
using Sde.App.ViewModels.Shell;
using Sde.Data;
using Sde.Reports;
using Sde.TestSupport;
using Page = Sde.App.ViewModels.Page;

namespace Sde.App.Tests;

/// <summary>
/// The case file page (3.5): one structure across every event, as a page in the window with a back button. Checked
/// against the synthetic structure that touches every part of a case file. Writes the case file screenshots.
/// </summary>
public class CaseFileUiTests
{
	/// <summary>The three made-up structures, plus the rich case file sample; the list showing.</summary>
	private static (UiSession Ui, CaseFileSample Sample) WithSample(ThemeVariant theme = null)
	{
		UiSession ui = new UiSession(theme);
		ScreenTourTests.AddSamples(ui.Store);
		CaseFileSample sample = CaseFileSample.Create(ui.Store, Path.Combine(ui.Folder, "source"));
		ui.Services.RaiseDataChanged();
		ui.Pump();
		return (ui, sample);
	}

	private static T Named<T>(UiSession ui, string name) where T : Control =>
		ui.Window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

	private static TextBlock Text(UiSession ui, string name) => Named<TextBlock>(ui, name);

	[AvaloniaFact]
	public void ThePage_ShowsTheStructure_ItsResult_AndEveryCard()
	{
		(UiSession ui, CaseFileSample sample) = WithSample();
		using UiSession session = ui;
		ui.Main.ShowCaseFile(sample.PropertyId);
		ui.Pump();

		Assert.Equal(Page.CaseFile, ui.Main.ActivePage);
		CaseFileViewModel page = Assert.IsType<CaseFileViewModel>(ui.Main.CurrentPage);
		Assert.Equal("123 River Street", ui.Main.PageTitle);
		Assert.Equal("Structures", ui.Main.PageParent);
		Assert.True(Named<Button>(ui, "BackButton").IsEffectivelyVisible);
		Assert.True(ui.Main.SidebarItems.Single(i => i.Page == Page.Structures).IsActive);

		// The hero band: the structure, and its current result on the hero's figure and gauge.
		Assert.Equal("123 River Street", Text(ui, "CaseFileAddress").Text);
		Assert.Equal("Pat Example", Text(ui, "CaseFileOwner").Text);
		Assert.Equal("092-0004-0118-00", Text(ui, "CaseFileParcel").Text);
		ResultHero hero = Named<ResultHero>(ui, "CaseFileResult");
		Assert.Equal("75.0%", hero.PercentText);
		Assert.Equal("Substantially Damaged", hero.Verdict);
		Assert.True(hero.IsSubstantial);
		Assert.Contains("Letter sent", hero.Hint);
		Assert.False(Text(ui, "CumulativeLine").IsEffectivelyVisible);     // cumulative damage is off in Settings

		// Every card, each with its record.
		foreach (string card in new[] { "AssessmentsCard", "ComplianceCard", "LettersCard", "AppealsCard", "ImprovementsCard", "DocumentsCard", "PhotosCard" })
		{
			Assert.True(Named<Border>(ui, card).IsEffectivelyVisible, card);
		}
		Assert.Equal(3, page.Assessments.Count);
		Assert.True(page.Assessments[0].IsTrashed);
		Assert.False(page.Assessments[0].CanOpen);
		Assert.Equal("In Recently deleted", page.Assessments[0].StatusText);
		Assert.True(page.Assessments[1].IsLatest);
		Assert.Equal("75.0%", page.Assessments[1].PercentText);
		Assert.StartsWith("September flood", page.Assessments[1].EventText);
		Assert.Equal("Substantial damage", Assert.Single(page.Letters).Title);
		Assert.Equal("Sent Sep 28, 2026", page.Letters[0].Trailing);
		Assert.Contains(page.Appeals, l => l.Title == "Appeal" && l.Trailing == AppealOutcomes.Upheld);
		Assert.Contains(page.Appeals, l => l.Title == "Bid: Example Builders" && l.Trailing == "$61,000");
		Assert.Equal("Elevation certificate 2026", Assert.Single(page.Documents).Title);
		Assert.Equal("1 photo", Text(ui, "PhotosCount").Text);
		Assert.Equal(2, page.Improvements.Count);
		Assert.Contains("Elevate", Assert.Single(page.Compliance).Title);
		Assert.Empty(BindingErrors.Instance.Messages.Where(m => m.Contains("CaseFile", StringComparison.Ordinal)));
	}

	[AvaloniaFact]
	public void AStructureWithLittleOnRecord_LeavesTheEmptyCardsOut()
	{
		using UiSession ui = new UiSession();
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		ui.Pump();
		StructureRowViewModel row = ui.Main.Structures.Rows.First(r => r.Address.StartsWith("48", StringComparison.Ordinal));
		ui.Main.ShowCaseFile(row.Summary.PropertyID);
		ui.Pump();
		Assert.True(Named<Border>(ui, "AssessmentsCard").IsEffectivelyVisible);
		foreach (string card in new[] { "ComplianceCard", "LettersCard", "AppealsCard", "ImprovementsCard", "DocumentsCard", "PhotosCard" })
		{
			Assert.False(Named<Border>(ui, card).IsEffectivelyVisible, card);
		}
		Assert.False(Named<Button>(ui, "CompareButton").IsEffectivelyVisible);
	}

	[AvaloniaFact]
	public void TheTimeline_HidesEditsUntilTheSwitchIsOn_AndDateOnlyEntriesHaveNoTime()
	{
		(UiSession ui, CaseFileSample sample) = WithSample();
		using UiSession session = ui;
		ui.Main.ShowCaseFile(sample.PropertyId);
		ui.Pump();
		CaseFileViewModel page = ui.Main.CaseFilePage;

		List<CaseFileEntryViewModel> entries = page.Timeline.OfType<CaseFileEntryViewModel>().ToList();
		Assert.NotEmpty(entries);
		Assert.DoesNotContain(entries, e => e.IsEdit);
		Assert.True(page.HasEdits);
		Assert.IsType<CaseFileMonthViewModel>(page.Timeline[0]);
		// Newest first, under one heading per month.
		Assert.Equal(entries.Select(e => e.Entry.When).OrderByDescending(w => w), entries.Select(e => e.Entry.When));
		Assert.Equal(page.Timeline.OfType<CaseFileMonthViewModel>().Count(), entries.Select(e => e.Entry.When.ToString("yyyy-MM")).Distinct().Count());
		CaseFileEntryViewModel inspection = entries.First(e => e.Entry.Kind == CaseFileEntryKind.Inspection);
		Assert.True(inspection.Entry.IsDateOnly);
		Assert.Equal("", inspection.TimeText);
		Assert.False(inspection.HasTime);
		Assert.Contains(entries, e => e.HasTime);

		ToggleSwitch edits = Named<ToggleSwitch>(ui, "ShowEditsSwitch");
		Assert.True(edits.IsEffectivelyVisible);
		Assert.False(edits.IsChecked);
		edits.IsChecked = true;
		ui.Pump();
		List<CaseFileEntryViewModel> withEdits = page.Timeline.OfType<CaseFileEntryViewModel>().ToList();
		Assert.Equal(entries.Count + page.EditCount, withEdits.Count);
		Assert.Contains(withEdits, e => e.IsEdit && e.Icon == "Edit");
		edits.IsChecked = false;
		ui.Pump();
		Assert.Equal(entries.Count, page.Timeline.OfType<CaseFileEntryViewModel>().Count());
	}

	[AvaloniaFact]
	public void Rows_OpenTheirAssessment_AndBackLeadsToTheCaseFile_ThenToTheList()
	{
		(UiSession ui, CaseFileSample sample) = WithSample();
		using UiSession session = ui;
		ui.Main.ShowCaseFile(sample.PropertyId);
		ui.Pump();
		CaseFileViewModel page = ui.Main.CaseFilePage;

		// A trashed assessment opens nothing; the earlier one opens in the editor, with the case file as its parent.
		page.OpenAssessmentCommand.Execute(page.Assessments[0]);
		Assert.Equal(Page.CaseFile, ui.Main.ActivePage);
		page.OpenAssessmentCommand.Execute(page.Assessments[2]);
		ui.Pump();
		Assert.Equal(Page.Editor, ui.Main.ActivePage);
		Assert.Equal(sample.Earlier.Assessment.AssessmentID, ui.Main.Editor.Assessment.AssessmentID);
		Assert.Equal("Case File", ui.Main.PageParent);
		Assert.True(ui.Main.SidebarItems.Single(i => i.Page == Page.Structures).IsActive);

		ui.Main.GoToParentCommand.Execute(null);
		ui.Pump();
		Assert.Equal(Page.CaseFile, ui.Main.ActivePage);
		Assert.Null(ui.Main.Editor);
		Assert.Equal("Structures", ui.Main.PageParent);

		// A timeline entry of an assessment opens it too.
		CaseFileEntryViewModel entry = ui.Main.CaseFilePage.Timeline.OfType<CaseFileEntryViewModel>()
			.First(e => e.CanOpen && e.Entry.AssessmentId == sample.Current.Assessment.AssessmentID);
		entry.Open.Execute(entry);
		ui.Pump();
		Assert.Equal(sample.Current.Assessment.AssessmentID, ui.Main.Editor.Assessment.AssessmentID);
		ui.Main.GoToParentCommand.Execute(null);
		ui.Main.GoToParentCommand.Execute(null);
		ui.Pump();
		Assert.Equal(Page.Structures, ui.Main.ActivePage);
		Assert.Null(ui.Main.CaseFilePage);
	}

	[AvaloniaFact]
	public void FromTheEditor_BackReturnsToTheSameAssessment()
	{
		(UiSession ui, CaseFileSample sample) = WithSample();
		using UiSession session = ui;
		ui.Main.OpenAssessment(sample.Current.Assessment.AssessmentID);
		ui.Pump();
		AssessmentEditorViewModel editor = ui.Main.Editor;
		editor.ShowCaseFileCommand.Execute(null);
		ui.Pump();
		Assert.Equal(Page.CaseFile, ui.Main.ActivePage);
		Assert.Equal("123 River Street", ui.Main.PageParent);
		Assert.False(ui.Main.IsEditorPage);

		ui.Main.CaseFilePage.OpenLatestCommand.Execute(null);
		ui.Pump();
		Assert.Equal(Page.Editor, ui.Main.ActivePage);
		Assert.Equal(sample.Current.Assessment.AssessmentID, ui.Main.Editor.Assessment.AssessmentID);
		ui.Main.GoToParentCommand.Execute(null);
		ui.Pump();
		Assert.Equal(Page.CaseFile, ui.Main.ActivePage);

		// Opened from the editor again: back leads to it, not to the list.
		ui.Main.OpenAssessment(sample.Current.Assessment.AssessmentID);
		ui.Main.Editor.ShowCaseFileCommand.Execute(null);
		ui.Main.GoToParentCommand.Execute(null);
		ui.Pump();
		Assert.Equal(Page.Editor, ui.Main.ActivePage);
		Assert.Equal(sample.Current.Assessment.AssessmentID, ui.Main.Editor.Assessment.AssessmentID);
	}

	[AvaloniaFact]
	public void EntryPoints_TheInspector_TheRowMenu_TheEditorMenu_AndThePalette()
	{
		(UiSession ui, CaseFileSample sample) = WithSample();
		using UiSession session = ui;
		StructureRowViewModel row = ui.Main.Structures.Rows.First(r => r.Summary.PropertyID == sample.PropertyId);
		ui.Main.Structures.Selected = row;
		ui.Pump();

		// The inspector's button.
		Button inspector = Named<Button>(ui, "InspectorCaseFile");
		Assert.Equal("Case file of the selected structure", Avalonia.Automation.AutomationProperties.GetName(inspector));
		inspector.Command.Execute(inspector.CommandParameter);
		ui.Pump();
		Assert.Equal(Page.CaseFile, ui.Main.ActivePage);
		Assert.Equal(sample.PropertyId, ui.Main.CaseFilePage.PropertyId);
		ui.Main.GoToParentCommand.Execute(null);
		ui.Pump();

		// The row's context menu.
		DataGrid grid = Named<DataGrid>(ui, "Grid");
		grid.ContextMenu!.Open(grid);
		ui.Pump();
		MenuItem item = grid.ContextMenu.Items.OfType<MenuItem>().Single(i => (string)i.Header == "Case File");
		Assert.Same(ui.Main.ShowCaseFileCommand, item.Command);
		grid.ContextMenu.Close();

		// The ⌘K palette, for the selected structure.
		ui.Main.Structures.Selected = ui.Main.Structures.Rows.First(r => r.Summary.PropertyID == sample.PropertyId);
		ui.Main.Palette.Open();
		ui.Main.Palette.Query = "case file";
		PaletteItem command = ui.Main.Palette.Results.Single(r => r.Title == "Case file of this structure");
		ui.Main.Palette.Run(command);
		ui.Pump();
		Assert.Equal(Page.CaseFile, ui.Main.ActivePage);
		ui.Main.GoToParentCommand.Execute(null);

		// The editor's More menu, in the Structure group.
		ui.Main.OpenAssessment(sample.Current.Assessment.AssessmentID);
		ui.Pump();
		Button moreButton = Named<Button>(ui, "MoreButton");
		MenuFlyout more = Assert.IsType<MenuFlyout>(moreButton.Flyout);
		more.Popup.ShouldUseOverlayLayer = true;
		more.ShowAt(moreButton);
		ui.Pump();
		List<MenuItem> items = more.Items.OfType<MenuItem>().ToList();
		int structure = items.FindIndex(i => (string)i.Header == "Structure");
		int fillIn = items.FindIndex(i => (string)i.Header == "Fill In");
		int caseFile = items.FindIndex(i => (string)i.Header == "Case File");
		Assert.InRange(caseFile, structure + 1, fillIn - 1);
		Assert.NotNull(items[caseFile].Command);
		Assert.NotNull(items.Single(i => (string)i.Header == "Documents…").Command);
		more.Hide();
	}

	[AvaloniaFact]
	public async Task Print_AsksWhatToInclude_AndWritesThePdf()
	{
		(UiSession ui, CaseFileSample sample) = WithSample();
		using UiSession session = ui;
		ui.Main.ShowCaseFile(sample.PropertyId);
		ui.Pump();
		string path = Path.Combine(ui.Folder, "case file.pdf");
		ui.Dialogs.Answers.Enqueue(path);

		Task printing = ui.Main.CaseFilePage.PrintCommand.ExecuteAsync(null);
		ui.Pump();
		PrintCaseFileSheetViewModel sheet = Assert.IsType<PrintCaseFileSheetViewModel>(ui.Main.Sheets.Current);
		Assert.True(sheet.IncludeTimeline);
		Assert.False(sheet.IncludeEdits);
		Assert.Contains($"({ui.Main.CaseFilePage.EditCount})", sheet.EditsLabel);
		ui.Snapshot("82-print-case-file");
		sheet.IncludeEdits = true;
		sheet.PrintCommand.Execute(null);
		await printing;

		Assert.True(File.Exists(path));
		Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 4));
		Assert.Contains(path, ui.Dialogs.Opened);
		Assert.False(ui.Main.Sheets.HasSheet);
	}

	[AvaloniaFact]
	public void PrintOptions_LeaveEditsOut_WithoutTheTimeline()
	{
		PrintCaseFileSheetViewModel sheet = new PrintCaseFileSheetViewModel(4) { IncludeEdits = true, IncludeTimeline = false };
		sheet.PrintCommand.Execute(null);
		CaseFileReportOptions options = Assert.IsType<CaseFileReportOptions>(sheet.Closed.Result);
		Assert.False(options.IncludeTimeline);
		Assert.False(options.IncludeEdits);
	}

	[AvaloniaFact]
	public void AStructureInRecentlyDeleted_HasNoCaseFile()
	{
		(UiSession ui, CaseFileSample sample) = WithSample();
		using UiSession session = ui;
		Guid draft = ui.Main.Structures.Rows.First(r => r.Address.StartsWith("48", StringComparison.Ordinal)).Summary.PropertyID;
		ui.Store.TrashProperty(draft);
		ui.Main.ShowCaseFile(draft);
		ui.Pump();
		Assert.Equal(Page.Structures, ui.Main.ActivePage);
		Assert.Contains(ui.Dialogs.Notifications, n => n.Contains("Recently deleted", StringComparison.Ordinal));
	}

	[AvaloniaFact]
	public void Screenshots_LightAndDark()
	{
		foreach ((ThemeVariant theme, string name) in new[] { (ThemeVariant.Light, "80-case-file"), (ThemeVariant.Dark, "81-dark-case-file") })
		{
			(UiSession ui, CaseFileSample sample) = WithSample(theme);
			using UiSession session = ui;
			ui.Main.ShowCaseFile(sample.PropertyId);
			ui.Pump();
			ui.Snapshot(name);
			ui.Main.CaseFilePage.ShowEdits = true;
			ui.Pump();
			ui.Snapshot(name + "-edits");
		}
	}
}
