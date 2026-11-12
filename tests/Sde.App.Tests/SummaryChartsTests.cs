using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Sde.App.Controls;
using Sde.App.ViewModels;
using Sde.App.Views;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Page = Sde.App.ViewModels.Page;

namespace Sde.App.Tests;

/// <summary>
/// 3.5's Summary charts (the damage distribution and progress over time) and the result hero that opens Structures'
/// inspector and the editor's result panel: the numbers behind them, and what a screen reader hears.
/// </summary>
public class SummaryChartsTests
{
	private static AssessmentRecord Result(decimal percent, string message = null, DateTime? inspected = null) => new AssessmentRecord
	{
		PercentDamage = percent,
		DamageMessage = message ?? (percent >= 50 ? Determinations.SubstantialMessage : Determinations.NotSubstantialMessage),
		DateofInspection = inspected,
	};

	[Fact]
	public void Bins_PlaceEdgeValues_AndLeaveOutAssessmentsWithoutAResult()
	{
		AssessmentRecord[] records =
		{
			Result(0m), Result(49.9m), Result(50m), Result(100m), Result(120m), Result(35m),
			Result(30m, Determinations.IncompleteMessage),               // no result yet: left out
			Result(80m, "Resident Refused Inspection"),                  // undetermined: no percentage to place
			new AssessmentRecord { DamageMessage = Determinations.SubstantialMessage },   // no percent at all
		};
		IReadOnlyList<HistogramBin> bins = DashboardViewModel.BuildBins(records);

		Assert.Equal(10, bins.Count);
		Assert.Equal(Enumerable.Range(0, 10).Select(i => i * 10.0), bins.Select(b => b.From));
		Assert.Equal(new[] { 1, 0, 0, 1, 1, 1, 0, 0, 0, 2 }, bins.Select(b => b.Count));   // 0 | 35 | 49.9 | 50 | 100 and 120
		Assert.Equal(6, bins.Sum(b => b.Count));
		// Blue below the near-threshold band, amber in it, red at or above the 50 percent line
		Assert.Equal("GaugeWater", bins[3].ColourKey);
		Assert.Equal("GaugeNear", bins[4].ColourKey);
		Assert.Equal("GaugeDanger", bins[5].ColourKey);
		Assert.Equal("GaugeDanger", bins[9].ColourKey);
	}

	[Fact]
	public void Bins_FollowTheLinesTheSummaryUses()
	{
		IReadOnlyList<HistogramBin> bins = DashboardViewModel.BuildBins(new[] { Result(45m, Determinations.SubstantialMessage) }, substantialPercent: 40m, closePercent: 30m);
		Assert.Equal(1, bins[4].Count);
		Assert.Equal("GaugeNear", bins[3].ColourKey);
		Assert.Equal("GaugeDanger", bins[4].ColourKey);
	}

	[Fact]
	public void Progress_IsARunningTotalByInspectionDay_WithEveryDayBetween()
	{
		DateTime first = new DateTime(2026, 9, 15, 10, 30, 0);
		AssessmentRecord[] records =
		{
			Result(20m, inspected: first), Result(70m, inspected: first.AddHours(3)),
			Result(55m, inspected: first.AddDays(2)),
			Result(30m, Determinations.IncompleteMessage, first.AddDays(1)),            // incomplete: not completed
			Result(60m, inspected: null),                                              // no inspection date
			Result(80m, "Resident Refused Inspection", first.AddDays(3)),              // undetermined, but inspected
		};
		IReadOnlyList<ProgressPoint> points = DashboardViewModel.BuildProgress(records);

		Assert.Equal(new[] { first.Date, first.Date.AddDays(1), first.Date.AddDays(2), first.Date.AddDays(3) }, points.Select(p => p.Date));
		Assert.Equal(new[] { 2, 2, 3, 4 }, points.Select(p => p.Total));

		Assert.Empty(DashboardViewModel.BuildProgress(Array.Empty<AssessmentRecord>()));
		ProgressPoint only = Assert.Single(DashboardViewModel.BuildProgress(new[] { Result(10m, inspected: first) }));
		Assert.Equal(1, only.Total);
	}

	[AvaloniaFact]
	public void Charts_SayTheirDataInWords()
	{
		DamageHistogram histogram = new DamageHistogram
		{
			Bins = DashboardViewModel.BuildBins(new[] { Result(5m), Result(52m), Result(58m), Result(91m) }),
		};
		Assert.Equal("Damage distribution: 0 to 10 percent, 1; 50 to 60 percent, 2; 90 to 100 percent, 1. 3 at or above the 50 percent line.",
			AutomationProperties.GetName(histogram));
		Assert.Equal("Damage distribution: no results yet", new DamageHistogram { Bins = DashboardViewModel.BuildBins(null) }.SpokenText);

		DateTime day = new DateTime(2026, 9, 15);
		ProgressChart chart = new ProgressChart
		{
			Points = DashboardViewModel.BuildProgress(new[] { Result(5m, inspected: day), Result(52m, inspected: day), Result(58m, inspected: day.AddDays(2)) }),
		};
		Assert.Equal("Progress over time: 3 assessments completed from Sep 15 to Sep 17, 2026. Sep 15, 2; Sep 17, 3.", AutomationProperties.GetName(chart));
		Assert.Equal("Progress over time: 1 assessment completed on Sep 15, 2026.",
			new ProgressChart { Points = DashboardViewModel.BuildProgress(new[] { Result(5m, inspected: day) }) }.SpokenText);
		Assert.StartsWith("Progress over time: no completed", new ProgressChart().SpokenText);
	}

	[AvaloniaFact]
	public void SummaryPage_DrawsBothCharts_FromItsOwnAssessments()
	{
		using UiSession ui = new UiSession();
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		ui.Main.ShowCommand.Execute(Page.Dashboard);
		ui.Pump();
		DashboardViewModel dashboard = ui.Main.Dashboard;

		// The two samples with a result: 75.0 and 67.5 percent; the third is incomplete
		Assert.Equal(new[] { 0, 0, 0, 0, 0, 0, 1, 1, 0, 0 }, dashboard.DamageBins.Select(b => b.Count));
		Assert.Equal("2 with a result", dashboard.DistributionNote);
		Assert.True(dashboard.HasProgressSeries);
		Assert.Equal(2, dashboard.ProgressSeries[^1].Total);

		DamageHistogram histogram = ui.Window.GetVisualDescendants().OfType<DamageHistogram>().Single();
		Assert.True(histogram.IsEffectivelyVisible);
		Assert.Equal("Damage distribution: 60 to 70 percent, 1; 70 to 80 percent, 1. 2 at or above the 50 percent line.", AutomationProperties.GetName(histogram));
		ProgressChart progress = ui.Window.GetVisualDescendants().OfType<ProgressChart>().Single();
		Assert.True(progress.IsEffectivelyVisible);
		Assert.StartsWith("Progress over time: 2 assessments completed on ", AutomationProperties.GetName(progress));
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
	}

	[AvaloniaFact]
	public void Inspector_OpensWithTheResultHero()
	{
		using UiSession ui = new UiSession();
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		ui.Main.Structures.Selected = ui.Main.Structures.Rows.First(r => r.Address.StartsWith("123"));
		ui.Pump();

		ResultHero hero = ui.Window.GetVisualDescendants().OfType<ResultHero>().Single(h => h.Name == "InspectorHero");
		Assert.True(hero.IsEffectivelyVisible);
		Assert.Equal("123 River Street", hero.Title);
		Assert.True(hero.IsSubstantial);
		TextBlock figure = Part<TextBlock>(hero, "HeroFigure");
		Assert.True(figure.IsEffectivelyVisible);
		Assert.Equal("75.0%", figure.Text);
		Assert.Equal("Substantially Damaged", Part<TextBlock>(hero, "HeroVerdict").Text);
		Assert.Contains(hero.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "25 points above the 50% line");
		StaffGauge gauge = Part<StaffGauge>(hero, "HeroGauge");
		Assert.Equal(75.0, gauge.Value, 1);
		Assert.True(gauge.OnDark);
		Assert.Equal("GaugeDanger", gauge.FillKey);
		Assert.Equal("GaugeDangerOnDark", gauge.PaintKey);
		Assert.Equal("75 percent damaged, at or above the 50 percent line", AutomationProperties.GetName(gauge));

		// A structure with no result yet: no figure, the determination in words and a dry staff
		ui.Main.Structures.Selected = ui.Main.Structures.Rows.First(r => r.Address.StartsWith("700"));
		ui.Pump();
		Assert.False(hero.IsSubstantial);
		Assert.False(Part<TextBlock>(hero, "HeroFigure").IsEffectivelyVisible);
		Assert.Equal(Determinations.IncompleteMessage, Part<TextBlock>(hero, "HeroVerdict").Text);
		Assert.True(Part<StaffGauge>(hero, "HeroGauge").IsEmpty);
	}

	[AvaloniaFact]
	public void Editor_UsesTheSameHero()
	{
		using UiSession ui = new UiSession();
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		ui.Pump();
		ui.Main.OpenAssessment(ui.Main.Structures.Rows.First(r => r.Address.StartsWith("48")).AssessmentID!.Value);
		ui.Pump();

		ResultHero hero = ui.Window.GetVisualDescendants().OfType<ResultHero>().Single(h => h.IsEffectivelyVisible);
		Assert.Equal("EditorHero", hero.Name);
		Assert.Equal(ui.Main.Editor.PercentText, Part<TextBlock>(hero, "HeroFigure").Text);
		Assert.Equal(ui.Main.Editor.Determination, Part<TextBlock>(hero, "HeroVerdict").Text);
		Assert.Equal(ui.Main.Editor.PercentValue, Part<StaffGauge>(hero, "HeroGauge").Value, 1);
	}

	/// <summary>A named part of a control's template.</summary>
	private static T Part<T>(Control owner, string name) where T : Control =>
		owner.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
}
