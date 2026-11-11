using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Sheets;
using Sde.Core.Assessments;
using Sde.Core.Records;

namespace Sde.App.Tests;

/// <summary>
/// Compare Assessments (3.5): two assessments of one structure side by side, only the fields that differ (labelled as
/// the editor labels them), the difference in points, and unavailable with one assessment. Writes the screenshots.
/// </summary>
public class CompareAssessmentsUiTests
{
	/// <summary>The first sample, with a re-inspection a week later at a lower damage, open in the editor.</summary>
	private static (UiSession Ui, AssessmentFile First, AssessmentFile Second) WithReinspection(ThemeVariant theme = null)
	{
		UiSession ui = new UiSession(theme);
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		ui.Pump();
		Guid firstId = ui.Main.Structures.Rows.First(r => r.Address.StartsWith("123", StringComparison.Ordinal)).AssessmentID!.Value;
		AssessmentFile first = ui.Store.LoadAssessment(firstId);
		// A copy of every field of the first, a week later, by another inspector, with less damage to one element.
		AssessmentFile second = ui.Store.NewDraft(ui.Store.GetProperty(first.Property.PropertyID), false);
		Guid secondId = second.Assessment.AssessmentID;
		foreach (FieldInfo f in FieldCatalog.All.Where(f => f.Model == FieldModel.Assessment))
		{
			FieldCatalog.SetValue(second, f, FieldCatalog.GetValue(first, f));
		}
		second.ElementPercentages = first.ElementPercentages.Select(e => new ElementPercentageRecord
		{
			AssessmentID = secondId, CalcID = e.CalcID, ElementID = Guid.NewGuid(), ElementName = e.ElementName,
			ElementPercentage = e.ElementPercentage, UserDamage = e.UserDamage, ItemCost = e.ItemCost, DamageValues = e.DamageValues,
		}).ToList();
		second.CostAdjustments = first.CostAdjustments.Select(c => new CostAdjustmentResultRecord
		{
			ResultID = Guid.NewGuid(), AssessmentID = secondId, CostAdjustmentLookupID = c.CostAdjustmentLookupID, Quantity = c.Quantity, UnitCost = c.UnitCost,
		}).ToList();
		second.Assessment.DateofInspection = first.Assessment.DateofInspection?.AddDays(7);
		second.Assessment.InspectorName = "Inspector B";
		ElementPercentageRecord damaged = second.ElementPercentages.OrderByDescending(e => e.UserDamage).First();
		damaged.UserDamage = Math.Max(0, damaged.UserDamage - 40);
		ui.Store.SaveAssessment(second);
		ui.Services.RaiseDataChanged();
		ui.Main.OpenAssessment(second.Assessment.AssessmentID);
		ui.Pump();
		return (ui, ui.Store.LoadAssessment(firstId), ui.Store.LoadAssessment(second.Assessment.AssessmentID));
	}

	private static CompareAssessmentsSheetViewModel Open(UiSession ui)
	{
		_ = ui.Main.Editor.CompareAssessmentsCommand.ExecuteAsync(null);
		ui.Pump();
		return Assert.IsType<CompareAssessmentsSheetViewModel>(ui.Main.Sheets.Current);
	}

	[AvaloniaFact]
	public void OnlyTheFieldsThatDiffer_AreShown_WithTheDifferenceInPoints()
	{
		(UiSession ui, AssessmentFile first, AssessmentFile second) = WithReinspection();
		using UiSession session = ui;
		Assert.True(ui.Main.Editor.CanCompareAssessments);
		CompareAssessmentsSheetViewModel sheet = Open(ui);

		// This one and the one before it, the older on the left.
		Assert.Equal(first.Assessment.AssessmentID, sheet.Left.Id);
		Assert.Equal(second.Assessment.AssessmentID, sheet.Right.Id);
		Assert.Equal($"{first.Assessment.PercentDamage:0.0}%", sheet.LeftSide.PercentText);
		Assert.Equal($"{second.Assessment.PercentDamage:0.0}%", sheet.RightSide.PercentText);
		decimal gap = Math.Round(second.Assessment.PercentDamage!.Value - first.Assessment.PercentDamage!.Value, 1);
		Assert.True(gap < 0);
		Assert.Equal("−" + Math.Abs(gap).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), sheet.DifferenceText);
		Assert.Equal("points lower", sheet.DifferenceNote);

		// Only what differs: the date, the inspector and the element, under the editor's step names and labels.
		List<CompareRowViewModel> rows = sheet.Groups.SelectMany(g => g.Rows).ToList();
		Assert.All(rows, r => Assert.True(r.Differs));
		FieldInfo inspector = FieldCatalog.Find(FieldModel.Assessment, nameof(AssessmentRecord.InspectorName));
		CompareRowViewModel name = rows.Single(r => r.Label == inspector.Label);
		Assert.Equal("Inspector B", name.Right);
		Assert.Contains(sheet.Groups, g => g.Title == FieldCatalog.Steps[inspector.Step - 1]);
		FieldInfo date = FieldCatalog.Find(FieldModel.Assessment, nameof(AssessmentRecord.DateofInspection));
		Assert.Equal(FieldCatalog.Display(date, second.Assessment.DateofInspection, ui.Store.Reference), rows.Single(r => r.Label == date.Label).Right);
		CompareRowViewModel element = rows.Single(r => r.HasGauges);
		ElementPercentageRecord before = first.ElementPercentages.Single(e => e.ElementName + " damage" == element.Label);
		ElementPercentageRecord after = second.ElementPercentages.Single(e => e.ElementName == before.ElementName);
		Assert.True(after.UserDamage < before.UserDamage);
		Assert.Equal((double)before.UserDamage, element.LeftPercent);
		Assert.Equal((double)after.UserDamage, element.RightPercent);
		Assert.Equal($"{before.UserDamage:0.##}%", element.Left);
		Assert.Contains(sheet.Groups, g => g.Title == FieldCatalog.Steps[FieldCatalog.DamageStep - 1]);
		Assert.Equal(rows.Count, sheet.DifferentCount);
		Assert.DoesNotContain(rows, r => r.Label == FieldCatalog.Find(FieldModel.Property, nameof(PropertyRecord.StreetName)).Label);

		// Show unchanged: every field, the same ones quiet.
		sheet.ShowUnchanged = true;
		List<CompareRowViewModel> all = sheet.Groups.SelectMany(g => g.Rows).ToList();
		Assert.True(all.Count > rows.Count + 20);
		Assert.Contains(all, r => !r.Differs);
		int steps = FieldCatalog.All.Where(f => f.AppliesTo(first) || f.AppliesTo(second)).Select(f => f.Step).Append(FieldCatalog.DamageStep).Distinct().Count();
		Assert.Equal(steps, sheet.Groups.Count);

		// The pickers: the same one on both sides differs nowhere.
		sheet.Left = sheet.Right;
		sheet.ShowUnchanged = false;
		Assert.Equal(0, sheet.DifferentCount);
		Assert.False(sheet.HasRows);
		Assert.Equal("0", sheet.DifferenceText);
		sheet.Cancel();
	}

	[AvaloniaFact]
	public void WithOneAssessment_TheCommandIsUnavailable_AndSaysWhy()
	{
		using UiSession ui = new UiSession();
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		ui.Pump();
		ui.Main.OpenAssessment(ui.Main.Structures.Rows.First(r => r.Address.StartsWith("123", StringComparison.Ordinal)).AssessmentID!.Value);
		ui.Pump();
		AssessmentEditorViewModel editor = ui.Main.Editor;
		Assert.False(editor.CanCompareAssessments);
		Assert.Contains("only one assessment", editor.CompareAssessmentsTip);
		Button moreButton = ui.Window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "MoreButton");
		MenuFlyout more = Assert.IsType<MenuFlyout>(moreButton.Flyout);
		more.Popup.ShouldUseOverlayLayer = true;
		more.ShowAt(moreButton);
		ui.Pump();
		MenuItem item = more.Items.OfType<MenuItem>().Single(i => (string)i.Header == "Compare Assessments…");
		Assert.False(item.IsEnabled);
		Assert.Equal(editor.CompareAssessmentsTip, ToolTip.GetTip(item));
		more.Hide();
		editor.CompareAssessmentsCommand.Execute(null);
		ui.Pump();
		Assert.False(ui.Main.Sheets.HasSheet);
	}

	[AvaloniaFact]
	public void TheCaseFile_OffersCompare_WithTwoAssessments()
	{
		(UiSession ui, AssessmentFile first, AssessmentFile second) = WithReinspection();
		using UiSession session = ui;
		ui.Main.Editor.ShowCaseFileCommand.Execute(null);
		ui.Pump();
		Assert.True(ui.Main.CaseFilePage.CanCompare);
		_ = ui.Main.CaseFilePage.CompareCommand.ExecuteAsync(null);
		ui.Pump();
		CompareAssessmentsSheetViewModel sheet = Assert.IsType<CompareAssessmentsSheetViewModel>(ui.Main.Sheets.Current);
		Assert.Equal(first.Assessment.AssessmentID, sheet.Left.Id);
		Assert.Equal(second.Assessment.AssessmentID, sheet.Right.Id);
		sheet.Cancel();
	}

	[AvaloniaFact]
	public void Screenshots_LightAndDark()
	{
		foreach ((ThemeVariant theme, string name) in new[] { (ThemeVariant.Light, "87-compare-assessments"), (ThemeVariant.Dark, "88-dark-compare-assessments") })
		{
			(UiSession ui, _, _) = WithReinspection(theme);
			using UiSession session = ui;
			Open(ui);
			ui.Snapshot(name);
		}
	}
}
