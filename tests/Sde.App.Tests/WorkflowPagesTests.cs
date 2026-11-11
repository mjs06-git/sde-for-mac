using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Sde.App.Controls;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Shell;
using Sde.App.ViewModels.Sheets;
using Sde.Core.Assessments;
using Sde.Core.Quality;
using Sde.Core.Records;
using Sde.Data;
using Page = Sde.App.ViewModels.Page;

namespace Sde.App.Tests;

/// <summary>
/// The Workflow pages in the 3.5 look: the shared list-and-inspector layout (Review, Compliance, Assignments), merging
/// duplicate structures from Review (refused, undone), the checks that could not run, the Compliance inspector and its
/// Due soon filter, Recently Deleted as a table (Restore, Delete Now, the Delete key, several rows at once) and the new
/// Agenda page (grouping by week with a set "today", opening a row, the calendar file, the sidebar badge). Writes their
/// screenshots.
/// </summary>
public class WorkflowPagesTests
{
	/// <summary>A Wednesday, so "this week" and "next week" are easy to reason about.</summary>
	private static readonly DateTime Today = new DateTime(2026, 11, 11);

	private static UiSession WithSamples(ThemeVariant theme = null)
	{
		UiSession ui = new UiSession(theme);
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		ui.Pump();
		return ui;
	}

	private static StructureRowViewModel Row(UiSession ui, string addressStart) =>
		ui.Main.Structures.Rows.First(r => r.Address.StartsWith(addressStart, StringComparison.Ordinal));

	private static NavItemViewModel Sidebar(UiSession ui, Page page) => ui.Main.SidebarItems.Single(i => i.Page == page);

	/// <summary>
	/// A second record of the ranch: every structure field copied (so the two describe the same building and a merge
	/// has nothing to refuse), with an empty assessment of its own. The checks then find the same parcel, address and
	/// location on both.
	/// </summary>
	private static (Guid Original, Guid Copy, Guid CopyAssessment) DuplicateTheRanch(UiSession ui)
	{
		StructureRowViewModel ranch = Row(ui, "123");
		Guid original = ranch.Summary.PropertyID;
		ui.Store.Apply(ranch.AssessmentID!.Value, f => f.Property.ParcelNumber = "12-345-678");
		PropertyRecord source = ui.Store.GetProperty(original);
		PropertyRecord copy = new PropertyRecord();
		foreach (PropertyInfo p in typeof(PropertyRecord).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite))
		{
			p.SetValue(copy, p.GetValue(source));
		}
		copy.PropertyID = Guid.Empty;
		copy = ui.Store.SaveProperty(copy);
		AssessmentFile f = ui.Store.CreateAssessment(copy.PropertyID, applyDefaults: false);
		ui.Services.RaiseDataChanged();
		ui.Pump();
		return (original, copy.PropertyID, f.Assessment.AssessmentID);
	}

	private static ReviewViewModel ShowReview(UiSession ui)
	{
		ui.Main.ShowCommand.Execute(Page.Review);
		ui.Pump();
		return ui.Main.Review;
	}

	// ───────────── The shared layout ─────────────

	[AvaloniaFact]
	public void ReviewComplianceAndAssignments_ShareOneInspector_WidthAndSwitch()
	{
		using UiSession ui = WithSamples();
		ui.Main.InspectorWidth = 320;
		foreach (Page page in new[] { Page.Review, Page.Compliance })
		{
			ui.Main.ShowCommand.Execute(page);
			ui.Pump();
			ListDetailPage layout = ui.Window.GetVisualDescendants().OfType<ListDetailPage>().Single(l => l.IsEffectivelyVisible);
			Border inspector = layout.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Inspector");
			Assert.True(inspector.IsEffectivelyVisible, page.ToString());
			Assert.Equal(320, inspector.Bounds.Width, 0.5);
			ResizeHandle handle = layout.GetVisualDescendants().OfType<ResizeHandle>().Single();
			handle.Value = 360;
			Assert.Equal(360, ui.Main.InspectorWidth);
			ui.Main.InspectorWidth = 320;

			// ⌥⌘I (the window's one switch) hides it on every page, and brings it back.
			ui.Window.KeyPressQwerty(PhysicalKey.I, RawInputModifiers.Alt | RawInputModifiers.Meta);
			ui.Pump();
			Assert.False(inspector.IsEffectivelyVisible);
			Assert.False(ui.Main.Structures.IsInspectorVisible);
			ui.Window.KeyPressQwerty(PhysicalKey.I, RawInputModifiers.Alt | RawInputModifiers.Meta);
			ui.Pump();
			Assert.True(inspector.IsEffectivelyVisible);
		}
	}

	// ───────────── Review: merging duplicates ─────────────

	[AvaloniaFact]
	public async Task Merge_FromADuplicateFinding_RefusedWhenStale_ThenMerged_AndUndone()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = WithSamples();
		(Guid original, Guid copy, Guid copyAssessment) = DuplicateTheRanch(ui);
		int structures = ui.Store.Counts().Properties;
		ReviewViewModel r = ShowReview(ui);
		r.ShowAllFindingsCommand.Execute(null);
		ui.Pump();

		// Each duplicate check offers Merge… on its findings; other checks don't.
		QaGroupViewModel parcel = r.Groups.Single(g => g.Rule.Id == QaRuleIds.DuplicateParcel);
		QaFindingViewModel finding = parcel.Findings.Single(f => f.Finding.PropertyId == original);
		Assert.True(finding.CanMerge);
		Assert.Equal(copy, finding.OtherPropertyId);
		Assert.All(r.Groups.Where(g => g.Rule.Id == QaRuleIds.MissingPhotos).SelectMany(g => g.Findings), f => Assert.False(f.CanMerge));
		Assert.Contains(ui.Window.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && Equals(b.Content, "Merge…"));

		Task merging = r.MergeFindingCommand.ExecuteAsync(finding);
		ui.Pump();
		MergeStructuresSheetViewModel sheet = Assert.IsType<MergeStructuresSheetViewModel>(ui.Main.Sheets.Current);
		Assert.Equal(original, sheet.Keep.PropertyId);
		Assert.Equal(copy, sheet.Other.PropertyId);
		Assert.False(sheet.HasConflicts);
		Assert.Contains(sheet.Moving, m => m.StartsWith("Assessments", StringComparison.Ordinal));
		Assert.True(sheet.MergeCommand.CanExecute(null));
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));

		// Choosing the other one to keep previews the merge the other way round.
		sheet.KeepSecond = true;
		Assert.Equal(copy, sheet.Keep.PropertyId);
		sheet.KeepFirst = true;

		// Changed since the preview: refused, with the store's reason, and nothing merged.
		ui.Store.Apply(copyAssessment, f => f.Property.OwnerFirstName = "Sam");
		sheet.MergeCommand.Execute(null);
		Assert.Same(sheet, ui.Main.Sheets.Current);
		Assert.True(sheet.HasConflicts);
		Assert.Contains(sheet.Conflicts, c => c.Contains("changed since", StringComparison.Ordinal));
		Assert.Equal(structures, ui.Store.Counts().Properties);

		// The preview was read again, so Merge now works.
		Assert.True(sheet.MergeCommand.CanExecute(null));
		sheet.MergeCommand.Execute(null);
		await merging;
		Assert.Null(ui.Main.Sheets.Current);
		Assert.Equal(structures - 1, ui.Store.Counts().Properties);
		Assert.Contains(ui.Dialogs.Notifications, n => n.StartsWith("Structures merged:", StringComparison.Ordinal));
		Assert.NotNull(r.LastMergeBatchId);
		Assert.Equal(original, ui.Store.LoadAssessment(copyAssessment).Property.PropertyID);
		Assert.DoesNotContain(r.Findings, f => f.RuleId == QaRuleIds.DuplicateParcel && !f.IsDismissed);
		Assert.Equal(1, Sidebar(ui, Page.Trash).Count);

		// Undo puts both structures back.
		r.UndoMergeCommand.Execute(r.LastMergeBatchId);
		Assert.Equal(structures, ui.Store.Counts().Properties);
		Assert.Equal(copy, ui.Store.LoadAssessment(copyAssessment).Property.PropertyID);
		Assert.Contains(ui.Dialogs.Notifications, n => n.StartsWith("Merge undone:", StringComparison.Ordinal));
		Assert.False(Sidebar(ui, Page.Trash).HasCount);
		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));
	}

	[AvaloniaFact]
	public async Task Merge_ThatCantBeDone_ShowsWhy_AndAnUndoAfterAnEdit_IsRefused()
	{
		using UiSession ui = WithSamples();
		(Guid original, Guid copy, Guid copyAssessment) = DuplicateTheRanch(ui);
		ReviewViewModel r = ShowReview(ui);

		// Two structures that differ in what their assessments were calculated with: the conflicts show, Merge is off.
		Guid mill = Row(ui, "48").Summary.PropertyID;
		MergeStructuresSheetViewModel refused = new MergeStructuresSheetViewModel(ui.Services, original, mill);
		Assert.True(refused.HasConflicts);
		Assert.False(refused.MergeCommand.CanExecute(null));

		// A real merge, then an edit to the moved assessment: Undo is refused with the store's reason, and nothing changes.
		QaFindingViewModel finding = r.Groups.Single(g => g.Rule.Id == QaRuleIds.DuplicateAddress).Findings.Single(f => f.Finding.PropertyId == original);
		Task merging = r.MergeFindingCommand.ExecuteAsync(finding);
		ui.Pump();
		((MergeStructuresSheetViewModel)ui.Main.Sheets.Current).MergeCommand.Execute(null);
		await merging;
		string batch = r.LastMergeBatchId;
		ui.Store.Apply(copyAssessment, f => f.Assessment.InspectorName = "Inspector Z");
		r.UndoMergeCommand.Execute(batch);
		Assert.Contains(ui.Dialogs.Notifications, n => n.StartsWith("ERROR Couldn't undo the merge:", StringComparison.Ordinal));
		Assert.Equal(original, ui.Store.LoadAssessment(copyAssessment).Property.PropertyID);
		Assert.Equal(3, ui.Store.Counts().Properties);
		Assert.NotEqual(copy, original);
	}

	// ───────────── Review: checks that could not run ─────────────

	[AvaloniaFact]
	public async Task NotChecked_IsAQuietGroup_WithCheckOnline()
	{
		using UiSession ui = WithSamples();
		ReviewViewModel r = ShowReview(ui);
		r.ShowAllFindingsCommand.Execute(null);
		ui.Pump();

		Assert.True(r.HasNotChecked);
		QaNotCheckedViewModel zone = r.NotChecked.Single(n => n.Item.RuleId == QaRuleIds.ZoneDiffersFromNfhl);
		Assert.Equal("Not checked: no stored flood lookup.", zone.Message);
		Assert.True(zone.CanCheckOnline);
		QaNotCheckedViewModel marks = r.NotChecked.Single(n => n.Item.RuleId == QaRuleIds.DepthVsHighWaterMarks);
		Assert.Equal("Not checked: no high-water marks stored.", marks.Message);
		Assert.Contains(ui.Window.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "Not checked");
		Assert.Contains(ui.Window.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && Equals(b.Content, "Check Online"));
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));

		// Check Online for the high-water marks opens the flood depth sheet for the assessments here.
		Task checking = r.CheckOnlineCommand.ExecuteAsync(marks);
		ui.Pump();
		Assert.IsType<DepthSheetViewModel>(ui.Main.Sheets.Current);
		ui.Main.Sheets.CancelCurrentCommand.Execute(null);
		await checking;

		// Check Online for the flood map keeps what the map says, even with nothing applied, so the check then runs.
		ui.Services.HttpClientFactory = () => new HttpClient(FakeOnline.Recorded());
		checking = r.CheckOnlineCommand.ExecuteAsync(zone);
		System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
		while (ui.Main.Sheets.Current is not BulkAutoFillSheetViewModel)
		{
			Assert.True(clock.ElapsedMilliseconds < 15000, "Timed out waiting for the look-up.");
			ui.Pump();
			await Task.Delay(10);
		}
		ui.Main.Sheets.CancelCurrentCommand.Execute(null);
		await checking;
		ui.Pump();
		Assert.NotEmpty(ui.Store.GetFloodLookups(ui.Store.LoadAllAssessments().Select(f => f.Property.PropertyID)));
		Assert.DoesNotContain(r.NotChecked, n => n.Item.RuleId == QaRuleIds.ZoneDiffersFromNfhl && n.Item.Count == 0);

		// With no findings left to list, one grey line says so above the group (no empty state over it).
		r.SeverityFilter = 2;
		ui.Pump();
		Assert.True(r.ShowsNoFindingsLine);
		Assert.False(r.ShowsChecksEmptyState);
	}

	// ───────────── Compliance ─────────────

	private static (ComplianceCaseRecord Ranch, ComplianceCaseRecord Mill) TwoCases(UiSession ui, DateTime today, int millDays)
	{
		ComplianceCaseRecord ranch = ui.Store.OpenComplianceCase(Row(ui, "123").AssessmentID!.Value);
		ranch.Path = CompliancePaths.Elevate;
		ranch.Deadline = today.AddDays(-12);
		ranch.PermitNumber = "BP-0001";
		ranch.EcReceivedOn = today.AddDays(-20);
		ranch = ui.Store.SaveComplianceCase(ranch);
		ui.Store.AddComplianceEntry(ranch.CaseID, "Call", "Owner called: getting bids from contractors.");
		ui.Store.AddComplianceEntry(ranch.CaseID, "Note", "Elevation certificate on file.");
		ComplianceCaseRecord mill = ui.Store.OpenComplianceCase(Row(ui, "48").AssessmentID!.Value);
		mill.Path = CompliancePaths.Relocate;
		mill.Deadline = today.AddDays(millDays);
		mill = ui.Store.SaveComplianceCase(mill);
		ui.Services.RaiseDataChanged();
		ui.Pump();
		return (ranch, mill);
	}

	[AvaloniaFact]
	public async Task Compliance_Inspector_ShowsTheCase_AndDueSoonFilters()
	{
		using UiSession ui = WithSamples();
		DateTime today = DateTime.Today;
		(ComplianceCaseRecord ranch, ComplianceCaseRecord mill) = TwoCases(ui, today, 10);
		ui.Main.ShowCommand.Execute(Page.Compliance);
		ui.Pump();
		ComplianceViewModel page = ui.Main.Compliance;

		// The deadline column shares the width; it isn't fixed any more.
		DataGrid grid = ui.Window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "Cases");
		Assert.True(grid.Columns.Single(c => Equals(c.Header, "Deadline")).Width.IsStar);

		// The inspector: the case's dates and its timeline, newest first.
		Assert.False(page.HasSelection);
		page.Selected = page.Rows.Single(r => r.Case.CaseID == ranch.CaseID);
		ui.Pump();
		Assert.True(page.HasSelection);
		Assert.Equal("Substantial damage", page.Selected.KindText);
		Assert.Equal("12 days overdue", page.Selected.OverdueText);
		Assert.Equal("Not done", page.Selected.FinalOrNone);
		Assert.Equal(today.AddDays(-20).ToString("MMM d, yyyy", System.Globalization.CultureInfo.GetCultureInfo("en-US")), page.Selected.EcOrNone);
		Assert.Equal(new[] { "Elevation certificate on file.", "Owner called: getting bids from contractors." }, page.SelectedTimeline.Take(2).Select(e => e.Text));
		Border card = ui.Window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "CaseCard");
		Assert.True(card.IsEffectivelyVisible);
		Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "12 days overdue" && t.Classes.Contains("danger"));
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("118-compliance-inspector");

		// A letter from the inspector, as from the case sheet: written and added to the timeline.
		string letter = Path.Combine(ui.Folder, "reminder.docx");
		ui.Dialogs.Answers.Enqueue(true);
		ui.Dialogs.Answers.Enqueue(letter);
		await page.WriteLetterCommand.ExecuteAsync(Sde.Reports.Letters.LetterKinds.DeadlineReminder);
		Assert.True(File.Exists(letter));
		Assert.Equal("Letter", page.SelectedTimeline[0].Kind);

		// Due soon: the counts agree with the store's (and so with the Agenda and the sidebar).
		Assert.Equal(1, page.DueSoonCount);
		Assert.Equal(ui.Store.GetDueSoonCount(14, today), page.DueSoonCount);
		Assert.Equal("Due soon 1", page.DueSoonChipText);
		Assert.Equal("Overdue 1", page.OverdueChipText);
		page.DueSoonOnly = true;
		Assert.Equal(mill.CaseID, Assert.Single(page.Rows).Case.CaseID);
		Assert.Equal("Due soon", page.FilterSummary);
		page.OverdueOnly = true;
		Assert.False(page.DueSoonOnly);
		Assert.Equal(ranch.CaseID, Assert.Single(page.Rows).Case.CaseID);
		page.ToggleDueSoonCommand.Execute(null);
		Assert.True(page.DueSoonOnly);
		Assert.False(page.OverdueOnly);
		page.ClearFiltersCommand.Execute(null);
		Assert.Equal(2, page.Rows.Count);

		// A deadline 20 days out is not due soon.
		mill.Deadline = today.AddDays(20);
		ui.Store.SaveComplianceCase(mill);
		ui.Services.RaiseDataChanged();
		ui.Pump();
		Assert.Equal(0, page.DueSoonCount);
	}

	// ───────────── Recently Deleted ─────────────

	[AvaloniaFact]
	public async Task RecentlyDeleted_IsATable_WithRestore_DeleteNow_AndTheDeleteKey()
	{
		using UiSession ui = WithSamples();
		ui.Main.Structures.TrashCommand.Execute(Row(ui, "123"));
		ui.Main.Structures.TrashCommand.Execute(Row(ui, "48"));
		ui.Main.ShowCommand.Execute(Page.Trash);
		ui.Pump();
		TrashViewModel trash = ui.Main.Trash;
		Assert.Equal(2, trash.Items.Count);
		DataGrid grid = ui.Window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "Deleted");
		Assert.Equal(new object[] { "What", "Kind", "Deleted", "Deleted by", "Removed in" }, grid.Columns.Select(c => c.Header));
		Assert.Equal(DataGridSelectionMode.Extended, grid.SelectionMode);
		Assert.Equal(new[] { "Restore", "Delete Now…" }, grid.ContextMenu.Items.OfType<MenuItem>().Select(m => (string)m.Header));
		Assert.All(trash.Items, i => Assert.Equal("Reviewer A", i.DeletedBy));
		Assert.All(trash.Items, i => Assert.Equal("30 days", i.DaysLeftText));
		Assert.False(trash.RestoreCommand.CanExecute(null));
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("53-recently-deleted-table");

		// Both selected: Restore puts both back.
		grid.SelectAll();
		ui.Pump();
		Assert.Equal(2, trash.SelectedItems.Count);
		Assert.EndsWith("2 selected", trash.StatusText);
		trash.RestoreCommand.Execute(null);
		Assert.Empty(trash.Items);
		Assert.Equal(3, ui.Store.Counts().Properties);

		// The Delete key asks first: no keeps them, yes removes the selected one for good.
		ui.Main.Structures.TrashCommand.Execute(Row(ui, "700"));
		ui.Main.ShowCommand.Execute(Page.Trash);
		ui.Pump();
		grid.SelectedItem = trash.Items[0];
		ui.Pump();
		ui.Dialogs.Answers.Enqueue(false);
		grid.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete });
		ui.Pump();
		Assert.Single(trash.Items);
		grid.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete });
		ui.Pump();
		Assert.Empty(trash.Items);
		Assert.Equal(2, ui.Store.Counts().Properties);

		// A row's own Delete Now still works for the one item it is given.
		ui.Main.Structures.TrashCommand.Execute(Row(ui, "48"));
		ui.Main.ShowCommand.Execute(Page.Trash);
		ui.Pump();
		await trash.DeleteNowCommand.ExecuteAsync(trash.Items[0]);
		Assert.Empty(trash.Items);
	}

	// ───────────── Agenda ─────────────

	/// <summary>
	/// Around <see cref="Today"/>: the ranch's deadline overdue, Mill Creek's due this week with its final inspection in
	/// December, and an event that starts next week and ends in December.
	/// </summary>
	private static void AgendaCases(UiSession ui)
	{
		ui.Store.Clock = () => Today.AddHours(10);
		(_, ComplianceCaseRecord mill) = TwoCases(ui, Today, 3);
		mill.FinalInspectionOn = Today.AddDays(40);
		ui.Store.SaveComplianceCase(mill);
		ui.Store.SaveEvent(new EventRecord { Name = "Spring storm", StartDate = Today.AddDays(8), EndDate = Today.AddDays(50) });
		ui.Services.RaiseDataChanged();
		ui.Pump();
	}

	private static AgendaViewModel ShowAgenda(UiSession ui)
	{
		ui.Main.ShowCommand.Execute(Page.Agenda);
		ui.Pump();
		return ui.Main.Agenda;
	}

	[AvaloniaFact]
	public void Agenda_GroupsByWeek_WithAGivenToday()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = WithSamples();
		AgendaCases(ui);
		AgendaViewModel agenda = ShowAgenda(ui);
		Assert.Equal("Agenda", ui.Main.PageTitle);
		Assert.Same(agenda, ui.Main.CurrentPage);

		// The next 30 days: what is overdue, then this week and next week. Past dates (the certificate received) are history.
		Assert.Equal(new[] { "Overdue", "This week", "Next week" }, agenda.Groups.Select(g => g.Title));
		AgendaEntryViewModel late = Assert.Single(agenda.Groups[0].Entries);
		Assert.Equal("12 days late", late.LateText);
		Assert.Equal("Compliance deadline (SD)", late.Heading);
		Assert.StartsWith("123 River Street", late.Where);
		Assert.Equal("Flag", late.Icon);
		Assert.Equal("Sat, Nov 14", Assert.Single(agenda.Groups[1].Entries).DateText);
		Assert.Equal("Thu, Nov 19", Assert.Single(agenda.Groups[2].Entries).DateText);
		Assert.Equal(AgendaEntryKind.EventStart, agenda.Groups[2].Entries[0].Entry.Kind);
		Assert.Equal(1, agenda.OverdueCount);
		Assert.Equal(1, agenda.DueSoonCount);
		Assert.Equal("2 dates in the next 30 days", agenda.Headline);
		Assert.Equal("3 dates · 1 overdue", agenda.StatusText);

		// 90 days adds December: the final inspection and the event's last day, under the month.
		agenda.RangeIndex = 1;
		Assert.Equal(new[] { "Overdue", "This week", "Next week", "December 2026" }, agenda.Groups.Select(g => g.Title));
		Assert.Equal(new[] { AgendaEntryKind.FinalInspection, AgendaEntryKind.EventEnd }, agenda.Groups[3].Entries.Select(e => e.Entry.Kind));
		agenda.RangeIndex = 2;
		Assert.Equal(5, agenda.Shown.Count);
		Assert.Equal("4 dates ahead", agenda.Headline);

		// The grouping itself, with another "today": a Sunday's next week starts the next day.
		List<AgendaGroupViewModel> fromSunday = AgendaViewModel.Group(agenda.Shown, new DateTime(2026, 11, 15), e => new AgendaEntryViewModel(e));
		Assert.Equal("Next week", fromSunday.Single(g => g.Entries.Any(x => x.Entry.Date == Today.AddDays(8))).Title);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));
	}

	[AvaloniaFact]
	public async Task Agenda_Rows_OpenTheirCaseOrAssessment_AndTheSidebarCounts()
	{
		using UiSession ui = WithSamples();
		AgendaCases(ui);
		AgendaViewModel agenda = ShowAgenda(ui);

		// The badge: overdue plus due in 14 days, red while one is overdue, the same numbers as the store's.
		NavItemViewModel item = Sidebar(ui, Page.Agenda);
		Assert.Equal(ui.Store.GetOverdueCount(Today) + ui.Store.GetDueSoonCount(14, Today), item.Count);
		Assert.Equal(2, item.Count);
		Assert.Equal(Tone.Danger, item.CountTone);
		Assert.Equal(new[] { "Review", "Compliance", "Agenda", "Assignments" }, ui.Main.SourceSections.Single(s => s.Title == "Workflow").Rows.OfType<NavItemViewModel>().Select(r => r.Title));
		Assert.True(item.IsActive);

		// A case's date opens the case sheet.
		AgendaEntryViewModel deadline = agenda.Groups[0].Entries[0];
		Task opening = agenda.OpenEntryCommand.ExecuteAsync(deadline);
		ui.Pump();
		ComplianceCaseSheetViewModel sheet = Assert.IsType<ComplianceCaseSheetViewModel>(ui.Main.Sheets.Current);
		Assert.Equal(deadline.Entry.CaseId, sheet.CaseId);
		ui.Main.Sheets.CancelCurrentCommand.Execute(null);
		await opening;

		// An appeal's or a letter's date opens the assessment, and closing it comes back here.
		Guid ranch = Row(ui, "123").AssessmentID!.Value;
		AgendaEntryViewModel letter = new AgendaEntryViewModel(new AgendaEntry { Kind = AgendaEntryKind.LetterSent, Date = Today, Title = "Letter", AssessmentId = ranch });
		await agenda.OpenEntryCommand.ExecuteAsync(letter);
		Assert.Equal(Page.Editor, ui.Main.ActivePage);
		Assert.Equal(ranch, ui.Main.Editor.Assessment.AssessmentID);
		Assert.Equal("Agenda", ui.Main.PageParent);
		ui.Main.GoToParentCommand.Execute(null);
		ui.Pump();
		Assert.Equal(Page.Agenda, ui.Main.ActivePage);

		// In an event, only its dates and counts.
		EventRecord e = ui.Store.SaveEvent(new EventRecord { Name = "September flood", StartDate = Today.AddDays(5), EndDate = Today.AddDays(60) });
		ui.Store.SetEvent(new[] { ranch }, e.EventID);
		ui.Services.RaiseDataChanged();
		ui.Main.Events.ChooseCommand.Execute(ui.Main.Events.Events.Single(c => c.Id == e.EventID));
		ui.Pump();
		Assert.All(agenda.Shown, x => Assert.True(x.EventId == e.EventID || x.PropertyId == Row(ui, "123").Summary.PropertyID));
		Assert.Contains(agenda.Shown, x => x.Kind == AgendaEntryKind.EventStart);
		Assert.Equal(1, item.Count);
	}

	[AvaloniaFact]
	public async Task Agenda_AddToCalendar_WritesTheShownDates()
	{
		using UiSession ui = WithSamples();
		AgendaCases(ui);
		AgendaViewModel agenda = ShowAgenda(ui);
		string path = Path.Combine(ui.Folder, "agenda.ics");
		ui.Dialogs.Answers.Enqueue(path);
		await agenda.AddToCalendarCommand.ExecuteAsync(null);
		string ics = File.ReadAllText(path);
		Assert.StartsWith("BEGIN:VCALENDAR", ics);
		Assert.Equal(agenda.Shown.Count, ics.Split("BEGIN:VEVENT").Length - 1);
		Assert.Contains("reveal:" + path, ui.Dialogs.Opened);
		Assert.Equal("Saved agenda.ics (3 dates).", agenda.LastResult);

		// Cancelled: nothing written.
		await agenda.AddToCalendarCommand.ExecuteAsync(null);
		Assert.Equal("Saved agenda.ics (3 dates).", agenda.LastResult);
	}

	// ───────────── Fit ─────────────

	/// <summary>
	/// The same check as <see cref="FitTests"/> (no label taller than its control, at either density) on the Workflow
	/// pages and Recently Deleted, with a row selected so the inspectors show.
	/// </summary>
	[AvaloniaFact]
	public void WorkflowPages_TextFitsInEveryControl()
	{
		using UiSession ui = WithSamples();
		AgendaCases(ui);
		MethodInfo clipped = typeof(FitTests).GetMethod("Clipped", BindingFlags.NonPublic | BindingFlags.Static);
		List<string> problems = new();
		void Check(string page)
		{
			ui.Pump();
			object[] args = { ui.Window, 0 };
			problems.AddRange(((IEnumerable<string>)clipped.Invoke(null, args)).Select(p => page + ": " + p));
		}

		ReviewViewModel r = ShowReview(ui);
		r.Selected = r.Rows.First();
		Check("Review");
		r.ShowAllFindingsCommand.Execute(null);
		Check("Review checks");
		ui.Main.ShowCommand.Execute(Page.Compliance);
		ui.Main.Compliance.Selected = ui.Main.Compliance.Rows.First();
		Check("Compliance");
		ShowAgenda(ui);
		Check("Agenda");
		ui.Main.Structures.TrashCommand.Execute(Row(ui, "700"));
		ui.Main.ShowCommand.Execute(Page.Trash);
		Check("Recently Deleted");
		Assert.True(problems.Count == 0, string.Join("\n", problems.Distinct()));
	}

	// ───────────── Screenshots ─────────────

	[AvaloniaTheory]
	[InlineData("light")]
	[InlineData("dark")]
	public void AgendaScreenshots(string themeName)
	{
		using UiSession ui = WithSamples(themeName == "dark" ? ThemeVariant.Dark : null);
		AgendaCases(ui);
		AgendaViewModel agenda = ShowAgenda(ui);
		agenda.RangeIndex = 1;
		ui.Pump();
		ui.Snapshot("120-agenda" + (themeName == "dark" ? "-dark" : ""));
	}

	[AvaloniaFact]
	public async Task ReviewAndMergeScreenshots()
	{
		using UiSession ui = WithSamples();
		(Guid original, _, _) = DuplicateTheRanch(ui);
		ReviewViewModel r = ShowReview(ui);
		r.Selected = r.Rows.First(x => x.Summary.PropertyID == original);
		ui.Pump();
		ui.Snapshot("110b-review-queue");
		r.ShowAllFindingsCommand.Execute(null);
		ui.Pump();
		ui.Snapshot("116-review-checks");

		QaFindingViewModel finding = r.Groups.Single(g => g.Rule.Id == QaRuleIds.DuplicateParcel).Findings.Single(f => f.Finding.PropertyId == original);
		Task merging = r.MergeFindingCommand.ExecuteAsync(finding);
		ui.Pump();
		ui.Snapshot("117-merge-sheet");
		ui.Main.Sheets.CancelCurrentCommand.Execute(null);
		await merging;
	}
}
