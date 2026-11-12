using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Sde.App.Controls;
using Sde.App.Services;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Editor;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;
using Sde.Data;
using Page = Sde.App.ViewModels.Page;

namespace Sde.App.Tests;

/// <summary>
/// Opens every page and every assessment step in the real UI (off-screen), saves screenshots to docs/screenshots,
/// and fails on any binding error. The screenshots are how the UI gets reviewed without driving the Mac's screen.
/// </summary>
public class ScreenTourTests
{
	/// <summary>Three made-up structures (see <see cref="SampleDatabase.AddSamples"/>), shared by most UI tests.</summary>
	internal static void AddSamples(Sde.Data.SdeStore store) => SampleDatabase.AddSamples(store);

	/// <summary>
	/// Forty more made-up structures for the Summary's screenshots, so its charts look like an event's: a spread of
	/// percent damaged from 4 to 99 and inspections over two weeks, by three inspectors on made-up streets.
	/// </summary>
	internal static void AddEventSpread(SdeStore store)
	{
		double[] targets =
		{
			4, 8, 12, 15, 18, 22, 24, 27, 29, 31, 33, 34, 36, 38, 41, 43, 44, 46, 47, 48,
			49, 52, 54, 55, 57, 58, 61, 63, 64, 67, 69, 72, 74, 78, 81, 85, 88, 91, 96, 99,
		};
		// How many inspections each day, over the two weeks after the flood (40 in all)
		int[] perDay = { 2, 4, 5, 6, 5, 4, 3, 3, 2, 2, 1, 1, 1, 1 };
		string[] streets = { "Alder", "Birch", "Cedar", "Dogwood", "Elm", "Fir", "Willow" };
		string[] inspectors = { SampleDatabase.Inspector, "Inspector B", "Inspector C" };
		// The three samples were inspected on the day they were made: put them in the event's first days too
		List<Guid> samples = store.ListStructures().Where(r => r.AssessmentID != null).Select(r => r.AssessmentID!.Value).ToList();
		foreach (AssessmentFile sample in store.LoadAssessments(samples))
		{
			sample.Assessment.DateofInspection = new DateTime(2026, 9, 15);
			store.SaveAssessment(sample);
		}
		int n = 0;
		for (int day = 0; day < perDay.Length; day++)
		{
			for (int k = 0; k < perDay[day]; k++, n++)
			{
				PropertyRecord p = store.SaveProperty(new PropertyRecord
				{
					OwnerFirstName = "Sample", OwnerLastName = "Owner " + (n + 1),
					StreetNumber = (100 + 12 * n).ToString(), StreetName = streets[n % streets.Length], StreetSuffixID = 172,
					City = SampleDatabase.Town, StateID = 36, NFIPCID = "990001", NFIPCommunityName = "Exampleville, Town of",
					StructureTypeID = 1, ResidenceTypeID = 92, FoundationTypeID = 45, SuperStructureTypeID = 130,
					RoofCoveringTypeID = 95, ExteriorFinishTypeID = 25, HVACTypeID = 49, StoryTypeID = 104,
					YearOfConstruction = 1962, StructureQualityID = 54, SquareFootage = 1200 + 20 * n,
				});
				AssessmentFile f = store.CreateAssessment(p.PropertyID, applyDefaults: false);
				AssessmentRecord a = f.Assessment;
				a.InspectorName = inspectors[n % inspectors.Length];
				a.DateDamageOccurred = new DateTime(2026, 9, 14);
				a.DateofInspection = new DateTime(2026, 9, 15).AddDays(day);
				a.CauseofDamageID = LookupIds.CauseFlood;
				a.BaseCost = 118.75m;
				a.GeographicAdjustment = 0.93m;
				store.Calculator.NormalizeRows(f);
				store.Calculator.ApplyDepreciationRating(f, 386);
				// The same damage on every element gives that share of the replacement cost; the depreciation lifts it
				// to the target share of the building's value
				decimal damage = (decimal)Math.Round(targets[n * 17 % targets.Length] * 0.758);
				foreach (ElementPercentageRecord e in f.ElementPercentages)
				{
					e.UserDamage = Math.Min(100, damage);
				}
				store.SaveAssessment(f);
			}
		}
	}

	[AvaloniaFact]
	public void Tour_AllScreens_RenderWithoutBindingErrors()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = new UiSession(ThemeVariant.Light);
		ui.Snapshot("00-structures-empty");
		AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		ui.Main.Structures.Selected = ui.Main.Structures.Rows[0];
		ui.Pump();
		Assert.True(ui.Main.Structures.HasSelectedDetail);
		ui.Snapshot("01-structures");

		Guid ranch = ui.Main.Structures.Rows.First(r => r.Address.StartsWith("123")).AssessmentID!.Value;
		ui.Main.OpenAssessment(ranch);
		AssessmentEditorViewModel editor = ui.Main.Editor;
		Assert.NotNull(editor);
		for (int i = 0; i < editor.Sections.Count; i++)
		{
			editor.SelectedSection = editor.Sections[i];
			ui.Snapshot($"{10 + i:00}-editor-{Slug(editor.Sections[i].Title)}");
		}

		AddEventSpread(ui.Store);
		ui.Services.RaiseDataChanged();
		ui.Main.ShowCommand.Execute(Page.Dashboard);
		ui.Snapshot("03-summary");
		ui.Main.ShowCommand.Execute(Page.ImportExport);
		ui.Snapshot("30-import-export");
		// A made-up name: without one the field shows the Mac account's full name, which must never reach the docs.
		ui.Services.SetYourName("Reviewer A");
		ui.Main.ShowCommand.Execute(Page.Settings);
		ui.Snapshot("31-settings");
		ui.Main.Settings.Show(SettingsCategory.Defaults);
		ui.Snapshot("31b-settings-defaults");
		ui.Main.Settings.Show(SettingsCategory.ReportsAndLetters);
		ui.Snapshot("31c-settings-reports");
		ui.Main.Settings.Show(SettingsCategory.PhoneForm);
		ui.Snapshot("31d-settings-phone-form");
		ui.Main.Settings.Show(SettingsCategory.AiAssistants);
		ui.Snapshot("31e-settings-ai-assistants");
		ui.Main.Settings.Show(SettingsCategory.General);

		// Empty states
		ui.Main.ShowCommand.Execute(Page.Trash);
		ui.Snapshot("52b-recently-deleted-empty");

		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));
	}

	[AvaloniaFact]
	public void Tour_DarkMode()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = new UiSession(ThemeVariant.Dark);
		AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		ui.Main.Structures.Selected = ui.Main.Structures.Rows[0];
		ui.Pump();
		ui.Snapshot("40-dark-structures");
		ui.Main.OpenAssessment(ui.Main.Structures.Rows.First(r => r.Address.StartsWith("48")).AssessmentID!.Value);
		ui.Main.Editor.SelectedSection = ui.Main.Editor.Sections.OfType<DamageSectionViewModel>().Single();
		ui.Snapshot("41-dark-editor-damage");
		ui.Main.Editor.SelectedSection = ui.Main.Editor.Sections[1];
		ui.Snapshot("42-dark-editor-building");
		AddEventSpread(ui.Store);
		ui.Services.RaiseDataChanged();
		ui.Main.ShowCommand.Execute(Page.Dashboard);
		ui.Snapshot("43-dark-dashboard");
		ui.Main.ShowCommand.Execute(Page.ImportExport);
		ui.Snapshot("44-dark-import-export");
		ui.Main.ShowCommand.Execute(Page.Settings);
		ui.Snapshot("45-dark-settings");
		ui.Main.Settings.Show(SettingsCategory.PhoneForm);
		ui.Snapshot("45b-dark-settings-phone-form");
		ui.Main.ShowCommand.Execute(Page.Trash);
		ui.Snapshot("46-dark-recently-deleted-empty");
		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));
	}

	[AvaloniaFact]
	public void ThemeSwitch_RecoloursResultDots_TheStaffGauge_AndLegendDots()
	{
		BindingErrors.Instance.Messages.Clear();
		using UiSession ui = new UiSession(ThemeVariant.Light);
		AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		ui.Pump();

		// Each check keeps the same control across the switch: a control made after it would be coloured right anyway.
		Ellipse dot = ui.Window.GetVisualDescendants().OfType<Ellipse>().First(e => e.IsEffectivelyVisible && e.Classes.Contains("dot") && e.DataContext is StructureRowViewModel);
		string state = ((StructureRowViewModel)dot.DataContext).State.ToString();
		AssertThemeColour("Status" + state + "Dot", dot.Fill);
		SwitchTheme(ui, ThemeVariant.Dark);
		AssertThemeColour("Status" + state + "Dot", dot.Fill);

		ui.Main.OpenAssessment(ui.Main.Structures.Rows.First(r => r.Address.StartsWith("123")).AssessmentID!.Value);
		ui.Pump();
		StaffGauge gauge = ui.Window.GetVisualDescendants().OfType<StaffGauge>().First(g => g.IsEffectivelyVisible && !g.IsMini);
		Assert.Equal(ui.Main.Editor.PercentValue, gauge.Value, 1);
		// The result hero draws the gauge on its dark card, so the water is painted with the "OnDark" version of its colour
		Assert.Equal(gauge.FillKey + "OnDark", gauge.PaintKey);
		AssertThemeColour(gauge.PaintKey, gauge.FillBrush);
		SwitchTheme(ui, ThemeVariant.Light);
		AssertThemeColour(gauge.PaintKey, gauge.FillBrush);

		ui.Main.ShowCommand.Execute(Page.Map);
		ui.Pump();
		Ellipse legendDot = ui.Window.GetVisualDescendants().OfType<Ellipse>().First(d => d.IsEffectivelyVisible && d.DataContext is MapClassViewModel);
		string legendKey = ((MapClassViewModel)legendDot.DataContext).ColourKey;
		AssertThemeColour(legendKey, legendDot.Fill);
		SwitchTheme(ui, ThemeVariant.Dark);
		AssertThemeColour(legendKey, legendDot.Fill);
		Assert.True(BindingErrors.Instance.Messages.IsEmpty, string.Join("\n", BindingErrors.Instance.Messages.Distinct().Take(20)));
	}

	private static void SwitchTheme(UiSession ui, ThemeVariant theme)
	{
		Application.Current!.RequestedThemeVariant = theme;
		ui.Pump();
		Assert.Equal(theme, Application.Current.ActualThemeVariant);
	}

	/// <summary>The brush has the colour of the named resource in the theme now showing.</summary>
	private static void AssertThemeColour(string key, IBrush actual)
	{
		Assert.True(Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out object expected), key);
		Assert.Equal(((ISolidColorBrush)expected).Color, Assert.IsAssignableFrom<ISolidColorBrush>(actual).Color);
	}

	private static string Slug(string s) => new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Replace("--", "-").Trim('-');
}
