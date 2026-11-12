using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Sde.App.Controls;
using Sde.App.Services;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Sheets;
using Sde.App.ViewModels.Shell;
using Sde.Core.Assessments;

namespace Sde.App.Tests;

/// <summary>
/// Every input the user can reach must have an accessible name, which is what VoiceOver reads and what assistive and
/// automation tools find it by. Avalonia names a button after its text, but an input needs AutomationProperties.Name
/// (a visible label beside it isn't linked to it automatically).
/// </summary>
public class AccessibilityTests
{
	// The controls are listed first: asking a control for its name can apply its template, which adds to the tree, and
	// walking the tree while that happens fails ("Collection was modified").
	internal static IEnumerable<string> Unnamed(Window window) =>
		window.GetVisualDescendants().OfType<Control>().ToList()
			.Where(c => c.IsEffectivelyVisible && c is TextBox or NumericUpDown or ComboBox or Slider or ToggleButton or Button)
			// Inner parts belong to their control, except the text box inside a NumericUpDown: that is the element macOS
			// exposes (and VoiceOver reads), so it must carry the name. A date field is a text box itself.
			.Where(c => c.FindAncestorOfType<Slider>() == null && (c.FindAncestorOfType<NumericUpDown>() == null || c is TextBox))
			.Where(c => IsUnnamed(NameOf(c)))
			.Select(c => $"{c.GetType().Name} \"{NameOf(c)}\" in {c.FindAncestorOfType<UserControl>()?.GetType().Name} (DataContext {c.DataContext?.GetType().Name})");

	/// <summary>What VoiceOver reads for a control.</summary>
	internal static string NameOf(Control c) => ControlAutomationPeer.CreatePeerForElement(c).GetName();

	/// <summary>
	/// No name, or one that is really a type's name: a control whose content is a panel or a view model is read as
	/// "Avalonia.Controls.StackPanel" or "SettingsCategoryItem { Category = … }" unless it is given a name.
	/// </summary>
	internal static bool IsUnnamed(string name) =>
		string.IsNullOrWhiteSpace(name) || name.StartsWith("Avalonia.", StringComparison.Ordinal) || name.StartsWith("Sde.", StringComparison.Ordinal)
		|| name.Contains(" { ", StringComparison.Ordinal) || name.EndsWith("ViewModel", StringComparison.Ordinal);

	[AvaloniaFact]
	public async Task EveryInputHasAnAccessibleName()
	{
		using UiSession ui = new UiSession(height: 1600);
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		List<string> problems = new();
		void Check(string page)
		{
			ui.Pump();
			problems.AddRange(Unnamed(ui.Window).Distinct().Select(p => page + ": " + p));
		}

		Check("Structures");
		ui.Main.Structures.OpenSubmissionCommand.Execute(null);
		Check("Structures (submission folder)");
		ui.Main.Structures.Submission.CancelCommand.Execute(null);
		AssessmentFile f = ui.Store.LoadAllAssessments().First(a => a.Property.StructureTypeID == 1);
		ui.Main.OpenAssessment(f.Assessment.AssessmentID);
		AssessmentEditorViewModel editor = ui.Main.Editor;
		foreach (SectionViewModel section in editor.Sections)
		{
			editor.SelectedSection = section;
			Check("Editor/" + section.Title);
		}
		ui.Main.ShowCommand.Execute(ViewModels.Page.ImportExport);
		Check("Import & export");
		ui.Main.ShowCommand.Execute(ViewModels.Page.Settings);
		foreach (SettingsCategoryItem category in ui.Main.Settings.Categories)
		{
			ui.Main.Settings.SelectedCategory = category;
			Check("Settings/" + category.Title);
		}

		// The import page, empty and then with a file, every field group open, values to match and a preview.
		ui.Main.ShowCommand.Execute(ViewModels.Page.SourceImport);
		Check("Import (no file)");
		SourceImportViewModel acc = ui.Main.SourceImport;
		await SourceImportUiTests.SetUpWideExport(acc);
		foreach (SourceFieldGroupViewModel g in acc.Groups)
		{
			g.IsExpanded = true;
		}
		Assert.NotEmpty(acc.Values);
		Assert.NotEmpty(acc.FilterValues);
		Check("Import");
		acc.LayoutIndex = 1;                                    // shows the checklist item and value columns
		Check("Import (checklist layout)");
		acc.ModeIndex = 1;                                      // structures: source, matching and parcel options
		acc.MatchByIndex = 1;
		Check("Import (structures by parcel)");
		acc.MatchByIndex = 2;
		Check("Import (structures by address)");
		acc.ModeIndex = 0;
		acc.MatchByIndex = 0;
		acc.LayoutIndex = 0;
		await acc.RefreshPreviewAsync();
		await acc.ImportCommand.ExecuteAsync(null);
		Check("Import (imported)");
		acc.ReviewCommand.Execute(null);
		Check("Structures (import review)");

		Assert.True(problems.Count == 0, string.Join("\n", problems));
	}

	/// <summary>
	/// The window's frame: the sidebar's pages and switchers (with their menus open), the title band, the placeholder
	/// pages, the ⌘K palette, a confirmation sheet, the move-or-copy sheet and a toast.
	/// </summary>
	[AvaloniaFact]
	public async Task TheShellHasAccessibleNames()
	{
		using UiSession ui = new UiSession();
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		List<string> problems = new();
		void Check(string part)
		{
			ui.Pump();
			problems.AddRange(Unnamed(ui.Window).Distinct().Select(p => part + ": " + p));
		}

		// Named for VoiceOver: each row of the source list by its title, the toolbar's search, view switch and New.
		List<string> names = ui.Window.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && (c is Button or TextBox or ListBox))
			.Select(c => ControlAutomationPeer.CreatePeerForElement(c).GetName()).ToList();
		// Recently Deleted is now a row of the Data section; Settings sits at the sidebar's foot.
		foreach (string title in ui.Main.SourceSections.SelectMany(s => s.Rows).Select(r => r switch
			{
				SourceRowViewModel row => row.Title,
				NavItemViewModel nav => nav.Title,
				SavedViewRowViewModel view => view.Title,
				TagRowViewModel tag => tag.Title,
				PinnedRowViewModel pin => pin.ToolTip,
				_ => null,
			})
			.Append("Settings"))
		{
			Assert.Contains(title, names);
		}
		Assert.Contains("Hide sidebar", names);
		Assert.Contains("View", names);
		Assert.Contains("Search structures", names);
		Assert.Contains("New", names);
		Check("Shell");

		foreach (ViewModels.Page page in new[] { ViewModels.Page.Review, ViewModels.Page.Compliance, ViewModels.Page.Agenda, ViewModels.Page.Assignments, ViewModels.Page.Dashboard, ViewModels.Page.Trash })
		{
			ui.Main.ShowCommand.Execute(page);
			Check(page.ToString());
		}

		foreach (string switcher in new[] { "NewButton" })
		{
			Button button = ui.Window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == switcher);
			PopupFlyoutBase flyout = (PopupFlyoutBase)button.Flyout;
			flyout.Popup.ShouldUseOverlayLayer = true;
			flyout.ShowAt(button);
			Check(switcher + " menu");
			flyout.Hide();
		}

		ui.Main.Palette.Open();
		ui.Main.Palette.Query = "river";
		Check("Command palette");
		ui.Main.Palette.Close();

		Task<bool> asking = ((IDialogs)ui.Window).ConfirmAsync("Delete for good?", "This can't be undone.", "Delete", destructive: true);
		Check("Confirmation");
		ui.Main.Sheets.CancelCurrentCommand.Execute(null);
		Assert.False(await asking);

		_ = ui.Main.Sheets.ShowAsync(new MoveOrCopySheetViewModel("sde.sqlite"));
		Check("Move or copy sheet");
		ui.Main.Sheets.CancelCurrentCommand.Execute(null);

		((IDialogs)ui.Window).Notify("Moved to Recently deleted", "123 River Street", "Undo", () => { });
		Check("Toast");

		Assert.True(problems.Count == 0, string.Join("\n", problems));
	}

	/// <summary>
	/// The office pages (v2.5): Review's queue with an assessment's checks and comments beside it, its quality checks
	/// with one set aside, and Compliance with cases; and their sheets: set aside, comments, the question before an
	/// export, and a compliance case.
	/// </summary>
	[AvaloniaFact]
	public async Task TheOfficePagesAndTheirSheetsHaveAccessibleNames()
	{
		using UiSession ui = new UiSession();
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		List<string> problems = new();
		void Check(string part)
		{
			ui.Pump();
			problems.AddRange(Unnamed(ui.Window).Distinct().Select(p => part + ": " + p));
		}
		Guid ranch = ui.Store.ListStructures().First(s => s.Address.StartsWith("123", StringComparison.Ordinal)).AssessmentID!.Value;
		Sde.Data.ReviewCommentRecord question = ui.Store.AddReviewComment(ranch, "Can you confirm the HVAC damage?");
		ui.Store.AddReviewComment(ranch, "Confirmed.", parentId: question.CommentID);
		ui.Store.AddReviewComment(ranch, "Is this the right date?", "Assessment.DateofInspection");
		ui.Store.Apply(ranch, f => f.Assessment.DateofInspection = new DateTime(2026, 9, 1));

		ui.Main.ShowCommand.Execute(ViewModels.Page.Review);
		ReviewViewModel review = ui.Main.Review;
		review.Selected = review.Rows.Single(r => r.AssessmentID == ranch);
		review.SelectedComments.Threads[0].StartReplyCommand.Execute(null);
		Check("Review queue");
		review.ShowAllFindingsCommand.Execute(null);
		QaFindingViewModel finding = review.Groups.First(g => g.Rule.Id == Sde.Core.Quality.QaRuleIds.MissingPhotos).Findings[0];
		Task dismissing = review.DismissFindingCommand.ExecuteAsync(finding);
		Check("Set aside sheet");
		((DismissFindingSheetViewModel)ui.Main.Sheets.Current).Reason = "Checked on site.";
		((DismissFindingSheetViewModel)ui.Main.Sheets.Current).DismissCommand.Execute(null);
		await dismissing;
		review.ShowDismissed = true;
		Check("Review checks");

		ui.Main.OpenAssessment(ranch);
		Task comments = ui.Main.Editor.ShowCommentsCommand.ExecuteAsync(null);
		Check("Comments sheet");
		ui.Main.Sheets.CancelCurrentCommand.Execute(null);
		await comments;

		ui.Dialogs.Answers.Enqueue(ui.Folder);
		Task exporting = ui.Main.ImportExport.ExportAllCommand.ExecuteAsync(null);
		Check("Quality errors before an export");
		Assert.IsType<QaBeforeExportSheetViewModel>(ui.Main.Sheets.Current);
		ui.Main.Sheets.CancelCurrentCommand.Execute(null);
		await exporting;

		ui.Store.OpenComplianceCase(ranch);
		ui.Main.ShowCommand.Execute(ViewModels.Page.Compliance);
		Check("Compliance");
		Task open = ui.Main.Compliance.OpenCaseCommand.ExecuteAsync(ui.Main.Compliance.Rows[0]);
		Check("Compliance case sheet");
		Assert.IsType<ComplianceCaseSheetViewModel>(ui.Main.Sheets.Current);
		ui.Main.Sheets.CancelCurrentCommand.Execute(null);
		await open;

		Assert.True(problems.Count == 0, string.Join("\n", problems));
	}

	// ───────────── Keyboard: every page by Tab alone ─────────────

	/// <summary>Where a control sits in the window: the sidebar, the title band, the page, its inspector, or a sheet.</summary>
	internal static string Region(Control c)
	{
		foreach (Control a in c.GetSelfAndVisualAncestors().OfType<Control>())
		{
			switch (a.Name)
			{
				case "Sidebar":
					return "sidebar";
				case "TitleBand":
					return "title band";
				case "SheetCard":
					return "sheet";
				case "PaletteLayer":
					return "palette";
				case "Inspector" or "PART_Inspector" or "ResultPanel":
					return "inspector";
				case "ContentPanel":
					return "page";
			}
		}
		return "elsewhere";
	}

	/// <summary>The visible tab stops: what Tab can land on, and so what VoiceOver users move through.</summary>
	internal static List<Control> TabStops(Window window) =>
		window.GetVisualDescendants().OfType<Control>().ToList()
			.Where(c => c.Focusable && c.IsEffectivelyVisible && KeyboardNavigation.GetIsTabStop(c))
			// A number box's inner text box carries the name (see Unnamed)
			.Where(c => c is not NumericUpDown && (c.FindAncestorOfType<NumericUpDown>() == null || c is TextBox))
			.ToList();

	/// <summary>Each tab stop with no accessible name, or with a type's name in its place.</summary>
	internal static IEnumerable<string> UnnamedTabStops(Window window) =>
		TabStops(window).Where(c => IsUnnamed(NameOf(c)))
			.Select(c => $"{c.GetType().Name} \"{NameOf(c)}\" in {Region(c)} (DataContext {c.DataContext?.GetType().Name})");

	/// <summary>Presses Tab (or Shift-Tab) and returns what has focus then.</summary>
	internal static Control Tab(UiSession ui, bool back = false)
	{
		ui.Window.KeyPressQwerty(PhysicalKey.Tab, back ? RawInputModifiers.Shift : RawInputModifiers.None);
		ui.Pump();
		return ui.Window.FocusManager!.GetFocusedElement() as Control;
	}

	/// <summary>Focuses the sidebar's Hide Sidebar button, where a keyboard user starts.</summary>
	private static Control StartAtTheSidebar(UiSession ui)
	{
		Button start = ui.Window.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && b.Classes.Contains("sidebarToggle"));
		start.Focus(NavigationMethod.Tab);
		ui.Pump();
		return start;
	}

	/// <summary>
	/// Tabs round the whole window from the sidebar and back to it, returning the stops in order. Fails if Tab gets stuck
	/// on a stop (Avalonia's own Tab handling stopped at a disabled button, or the remembered item of a hidden switch).
	/// </summary>
	internal static List<Control> TabRound(UiSession ui, string page, int most = 400)
	{
		Control start = StartAtTheSidebar(ui);
		List<Control> stops = new() { start };
		for (int i = 0; i < most; i++)
		{
			Control focused = Tab(ui);
			Assert.NotNull(focused);
			Assert.False(ReferenceEquals(focused, stops[^1]), $"{page}: Tab is stuck on {focused.GetType().Name} \"{NameOf(focused)}\" in {Region(focused)}");
			if (ReferenceEquals(focused, start))
			{
				return stops;
			}
			stops.Add(focused);
		}
		Assert.Fail(page + ": Tab never came back round to the sidebar");
		return stops;
	}

	/// <summary>Fills the pages so each has something to tab through: samples, a case, a team, an item in Recently Deleted.</summary>
	private static UiSession FilledSession(int height = 900)
	{
		UiSession ui = new UiSession(height: height);
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		Guid ranch = ui.Store.ListStructures().First(s => s.Address.StartsWith("123", StringComparison.Ordinal)).AssessmentID!.Value;
		Sde.Data.ComplianceCaseRecord compliance = ui.Store.OpenComplianceCase(ranch);
		compliance.Deadline = DateTime.Today.AddDays(10);
		ui.Store.SaveComplianceCase(compliance);
		Sde.Data.TeamRecord team = ui.Store.SaveTeam(new Sde.Data.TeamRecord { Name = "Team North" });
		ui.Store.AssignToTeam(ui.Store.GetProperties().Select(p => p.PropertyID).ToList(), team.TeamID);
		ui.Services.RaiseDataChanged();
		ui.Main.Structures.TrashCommand.Execute(ui.Main.Structures.Rows.First(r => r.Address.StartsWith("700", StringComparison.Ordinal)));
		ui.Main.Structures.Selected = ui.Main.Structures.Rows[0];
		ui.Pump();
		return ui;
	}

	/// <summary>Each page, Case File and the editor's steps, shown in turn, with a name for the failure messages.</summary>
	private static IEnumerable<string> EveryPage(UiSession ui)
	{
		foreach (ViewModels.Page page in new[] { ViewModels.Page.Structures, ViewModels.Page.Map, ViewModels.Page.Dashboard, ViewModels.Page.Review, ViewModels.Page.Compliance,
			ViewModels.Page.Agenda, ViewModels.Page.Assignments, ViewModels.Page.Trash, ViewModels.Page.ImportExport, ViewModels.Page.Settings })
		{
			ui.Main.ShowCommand.Execute(page);
			ui.Pump();
			if (page == ViewModels.Page.Review && ui.Main.Review.Rows.Count > 0)
			{
				ui.Main.Review.Selected = ui.Main.Review.Rows[0];
			}
			if (page == ViewModels.Page.Compliance && ui.Main.Compliance.Rows.Count > 0)
			{
				ui.Main.Compliance.Selected = ui.Main.Compliance.Rows[0];
			}
			ui.Pump();
			yield return page.ToString();
		}
		ui.Main.ShowCaseFile(ui.Main.Structures.Rows[0].Summary.PropertyID);
		ui.Pump();
		yield return "Case File";
		ui.Main.OpenAssessment(ui.Main.Structures.Rows.First(r => r.Address.StartsWith("123", StringComparison.Ordinal)).AssessmentID!.Value);
		foreach (SectionViewModel section in ui.Main.Editor.Sections.ToList())
		{
			ui.Main.Editor.SelectedSection = section;
			ui.Pump();
			yield return "Editor, " + section.Title;
		}
	}

	/// <summary>
	/// Every control Tab can land on, on every page (the Workflow pages, Agenda, Case File and each editor step included),
	/// has a name VoiceOver can read: no blank, and no type's name such as a row read as its view model.
	/// </summary>
	[AvaloniaFact]
	public void EveryTabStopOnEveryPage_HasAnAccessibleName()
	{
		using UiSession ui = FilledSession(height: 1400);
		List<string> problems = new();
		int stops = 0;
		foreach (string page in EveryPage(ui))
		{
			stops += TabStops(ui.Window).Count;
			problems.AddRange(UnnamedTabStops(ui.Window).Concat(Unnamed(ui.Window)).Distinct().Select(p => page + ": " + p));
		}
		Assert.True(stops > 300, $"Only {stops} tab stops found");
		Assert.True(problems.Count == 0, string.Join("\n", problems.Distinct()));
	}

	/// <summary>
	/// From the sidebar, Tab goes in reading order (the sidebar with Settings last, then the title band, then the page,
	/// then its inspector) and comes back round to the sidebar, on every page, without getting stuck.
	/// </summary>
	[AvaloniaFact]
	public void Tab_GoesInReadingOrder_OnEveryPage_AndNeverGetsStuck()
	{
		using UiSession ui = FilledSession();
		string[] order = { "sidebar", "title band", "page", "inspector" };
		foreach (string page in EveryPage(ui))
		{
			List<Control> stops = TabRound(ui, page);
			List<string> regions = stops.Select(Region).ToList();
			Assert.DoesNotContain("elsewhere", regions);
			for (int i = 1; i < regions.Count; i++)
			{
				Assert.True(Array.IndexOf(order, regions[i]) >= Array.IndexOf(order, regions[i - 1]),
					$"{page}: Tab went back from the {regions[i - 1]} ({NameOf(stops[i - 1])}) to the {regions[i]} ({NameOf(stops[i])})");
			}
			Assert.Contains("title band", regions);
			Assert.Contains("page", regions);
			// Settings, at the sidebar's foot, is its last stop
			Assert.Equal("Settings", NameOf(stops.Last(s => Region(s) == "sidebar")));
		}
	}

	/// <summary>Tab from the sidebar reaches the page's content and then its inspector (Structures, Review, Compliance, the editor's result).</summary>
	[AvaloniaFact]
	public void TabFromTheSidebar_ReachesThePage_AndTheInspector()
	{
		using UiSession ui = FilledSession();
		foreach (string page in EveryPage(ui).Where(p => p is "Structures" or "Review" or "Compliance" or "Editor, Location & owner"))
		{
			List<Control> stops = TabRound(ui, page);
			int content = stops.FindIndex(s => Region(s) == "page");
			int inspector = stops.FindIndex(s => Region(s) == "inspector");
			Assert.True(content > 0, page + ": the page's content was never reached");
			Assert.True(inspector > content, page + ": the inspector was never reached after the page");
		}
		// Shift-Tab goes the other way: from the inspector back into the page, and from the page back to the title band
		ui.Main.ShowCommand.Execute(ViewModels.Page.Structures);
		ui.Main.Structures.Selected = ui.Main.Structures.Rows[0];
		ui.Pump();
		List<Control> forward = TabRound(ui, "Structures");
		int firstInInspector = forward.FindIndex(s => Region(s) == "inspector");
		int firstInPage = forward.FindIndex(s => Region(s) == "page");
		forward[firstInInspector].Focus(NavigationMethod.Tab);
		Assert.Equal("page", Region(Tab(ui, back: true)));
		forward[firstInPage].Focus(NavigationMethod.Tab);
		Assert.Equal("title band", Region(Tab(ui, back: true)));
	}

	/// <summary>The keyboard focus shows: a control reached by Tab gets the focus ring.</summary>
	[AvaloniaFact]
	public void KeyboardFocus_ShowsTheFocusRing()
	{
		using UiSession ui = FilledSession();
		StartAtTheSidebar(ui);
		Control focused = Tab(ui);
		Assert.Contains(":focus-visible", focused.Classes);
		AdornerLayer layer = AdornerLayer.GetAdornerLayer(focused)!;
		Border ring = layer.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.IsEffectivelyVisible && b.BorderThickness.Left == 3);
		Assert.NotNull(ring);
		Assert.Equal(0.5, ring.Opacity, 2);
	}

	/// <summary>A sheet takes the keyboard: the cursor goes to its first field, Tab stays inside it, Esc closes it and focus goes back.</summary>
	[AvaloniaFact]
	public async Task ASheet_KeepsTabInside_EscClosesIt_AndFocusGoesBack()
	{
		using UiSession ui = FilledSession();
		Control before = StartAtTheSidebar(ui);
		Task adding = ui.Main.NewEventCommand.ExecuteAsync(null);
		ui.Pump();
		Assert.IsType<EventSheetViewModel>(ui.Main.Sheets.Current);
		Control first = ui.Window.FocusManager!.GetFocusedElement() as Control;
		Assert.IsAssignableFrom<TextBox>(first);
		Assert.Equal("sheet", Region(first));
		for (int i = 0; i < 40; i++)
		{
			Assert.Equal("sheet", Region(Tab(ui)));
		}
		for (int i = 0; i < 5; i++)
		{
			Assert.Equal("sheet", Region(Tab(ui, back: true)));
		}
		ui.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
		ui.Pump();
		await adding;
		Assert.False(ui.Main.Sheets.HasSheet);
		Assert.Same(before, ui.Window.FocusManager.GetFocusedElement());

		// A sheet with no text field: the sheet itself takes focus, Tab starts inside it, and Esc still closes it
		ui.Main.ShowShortcutsCommand.Execute(null);
		ui.Pump();
		Control card = ui.Window.FocusManager.GetFocusedElement() as Control;
		Assert.Equal("SheetCard", card?.Name);
		Assert.False(IsUnnamed(NameOf(card)));
		Assert.Equal("sheet", Region(Tab(ui)));
		ui.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
		ui.Pump();
		Assert.False(ui.Main.Sheets.HasSheet);

		// The palette: ⌘K puts the cursor in its box, Tab stays in it, Esc closes it, and focus goes back
		ui.Window.KeyPressQwerty(PhysicalKey.K, RawInputModifiers.Meta);
		ui.Pump();
		Assert.True(ui.Main.Palette.IsOpen);
		Assert.Equal("palette", Region((Control)ui.Window.FocusManager.GetFocusedElement()));
		Assert.Equal("palette", Region(Tab(ui)));
		ui.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
		ui.Pump();
		Assert.False(ui.Main.Palette.IsOpen);
		Assert.Same(before, ui.Window.FocusManager.GetFocusedElement());
	}

	/// <summary>Esc closes a sheet from inside a date field too, even one holding text that is not a date.</summary>
	[AvaloniaFact]
	public async Task Esc_ClosesASheet_FromInsideAField()
	{
		using UiSession ui = FilledSession();
		Task adding = ui.Main.NewEventCommand.ExecuteAsync(null);
		ui.Pump();
		DateField start = ui.Window.GetVisualDescendants().OfType<DateField>().First(d => NameOf(d) == "Date the event started");
		start.Focus();
		start.Text = "not a date";
		start.Commit();
		Assert.True(start.HasError);
		ui.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
		ui.Pump();
		await adding;
		Assert.False(ui.Main.Sheets.HasSheet);
	}

	/// <summary>A table is one Tab stop: its rows' check boxes aren't stops of their own, Space checks the selected row, the arrows move.</summary>
	[AvaloniaFact]
	public void Tables_AreOneTabStop_AndSpaceChecksTheSelectedRow()
	{
		using UiSession ui = FilledSession();
		DataGrid grid = ui.Window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "Grid" && g.IsEffectivelyVisible);
		Assert.DoesNotContain(TabStops(ui.Window), c => c is CheckBox && c.FindAncestorOfType<DataGridRow>() != null);
		StructureRowViewModel row = ui.Main.Structures.Rows[0];
		grid.SelectedItem = row;
		grid.Focus(NavigationMethod.Tab);
		ui.Pump();
		ui.Window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
		ui.Pump();
		Assert.True(row.IsChecked);
		ui.Window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
		ui.Pump();
		Assert.False(row.IsChecked);
		ui.Window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
		ui.Pump();
		Assert.Same(ui.Main.Structures.Rows[1], ui.Main.Structures.Selected);
	}

	/// <summary>Segmented switches and the editor's steps move with the arrow keys.</summary>
	[AvaloniaFact]
	public void SegmentedSwitchesAndSteps_MoveWithTheArrowKeys()
	{
		using UiSession ui = FilledSession();
		ui.Main.ShowCommand.Execute(ViewModels.Page.Review);
		ui.Pump();
		ListBox tabs = ui.Window.GetVisualDescendants().OfType<ListBox>().Single(l => l.IsEffectivelyVisible && NameOf(l) == "Queue or quality checks");
		((ListBoxItem)tabs.ContainerFromIndex(tabs.SelectedIndex)!).Focus(NavigationMethod.Tab);
		ui.Pump();
		ui.Window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
		ui.Pump();
		Assert.Equal(1, tabs.SelectedIndex);
		ui.Window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
		ui.Pump();
		Assert.Equal(0, tabs.SelectedIndex);

		ui.Main.OpenAssessment(ui.Main.Structures.Rows.First(r => r.Address.StartsWith("123", StringComparison.Ordinal)).AssessmentID!.Value);
		ui.Pump();
		ListBox steps = ui.Window.GetVisualDescendants().OfType<ListBox>().Single(l => l.IsEffectivelyVisible && NameOf(l) == "Steps");
		((ListBoxItem)steps.ContainerFromIndex(0)!).Focus(NavigationMethod.Tab);
		ui.Pump();
		Assert.Equal(ui.Main.Editor.Sections[0].Title, NameOf((Control)ui.Window.FocusManager!.GetFocusedElement()));
		ui.Window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
		ui.Pump();
		Assert.Same(ui.Main.Editor.Sections[1], ui.Main.Editor.SelectedSection);
	}

	/// <summary>A button that shows only an icon has a name for VoiceOver and a tooltip for everyone else, on every page.</summary>
	[AvaloniaFact]
	public void IconOnlyButtons_HaveNamesAndTooltips()
	{
		using UiSession ui = FilledSession(height: 1400);
		List<string> problems = new();
		int iconButtons = 0;
		foreach (string page in EveryPage(ui))
		{
			foreach (Button button in ui.Window.GetVisualDescendants().OfType<Button>().ToList().Where(b => b.IsEffectivelyVisible && IsIconOnly(b)))
			{
				iconButtons++;
				string name = NameOf(button);
				if (IsUnnamed(name) || ToolTip.GetTip(button) == null)
				{
					problems.Add($"{page}: {button.GetType().Name} \"{name}\" [{string.Join(" ", button.Classes)}] in {Region(button)} has {(IsUnnamed(name) ? "no name" : "no tooltip")}");
				}
			}
		}
		Assert.True(iconButtons > 20, $"Only {iconButtons} icon buttons found");
		Assert.True(problems.Count == 0, string.Join("\n", problems.Distinct()));
	}

	/// <summary>A button whose content is an icon and no text (parts of other controls, such as a scroll bar's arrows, don't count).</summary>
	private static bool IsIconOnly(Button b) =>
		b is not RepeatButton && b.TemplatedParent is null && b.Content is not string
		&& b.GetLogicalDescendants().OfType<Controls.Icon>().Any()
		&& !b.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text));

	/// <summary>The newer sheets and pages: What's New, the spreadsheet chooser for a dropped file, Agenda and Case File.</summary>
	[AvaloniaFact]
	public async Task TheNewerSheetsAndPages_HaveAccessibleNames()
	{
		using UiSession ui = FilledSession();
		List<string> problems = new();
		void Check(string part)
		{
			ui.Pump();
			problems.AddRange(Unnamed(ui.Window).Concat(UnnamedTabStops(ui.Window)).Distinct().Select(p => part + ": " + p));
		}
		ui.Main.ShowCommand.Execute(ViewModels.Page.Agenda);
		Check("Agenda");
		ui.Main.ShowCaseFile(ui.Main.Structures.Rows[0].Summary.PropertyID);
		Check("Case File");
		_ = ui.Main.ShowWhatsNew();
		Check("What's New");
		ui.Main.Sheets.CancelCurrentCommand.Execute(null);
		Task drop = ui.Main.Drops.DropAsync(new[] { SourceImportUiTests.ImportFixture("county-parcels.csv") });
		ui.Pump();
		Assert.IsType<SpreadsheetDropSheetViewModel>(ui.Main.Sheets.Current);
		Check("Spreadsheet chooser");
		ui.Main.Sheets.CancelCurrentCommand.Execute(null);
		await drop;
		Assert.True(problems.Count == 0, string.Join("\n", problems));
	}
}
