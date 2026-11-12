using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Sde.App.Controls;
using Sde.App.Services;
using Sde.App.ViewModels;
using Page = Sde.App.ViewModels.Page;

namespace Sde.App.Tests;

/// <summary>
/// How Increase Contrast and the empty Summary look and behave in the real window: the stronger look of Structures and
/// the editor in both themes, the switch in Settings, Appearance, and the Summary's first steps when there is nothing to
/// sum up.
/// </summary>
public class AccessibilityLookTests
{
	/// <summary>The colour a border shows now, to compare with the tokens.</summary>
	private static Color Showing(IBrush brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

	private static Color Token(string key, ThemeVariant theme)
	{
		Assert.True(Application.Current!.TryGetResource(key, theme, out object value), key);
		return ((ISolidColorBrush)value!).Color;
	}

	[AvaloniaFact]
	public void IncreaseContrast_StrengthensStructuresAndTheEditor_InBothThemes()
	{
		Application app = Application.Current!;
		using UiSession ui = new UiSession(ThemeVariant.Light);
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		ui.Main.Structures.Selected = ui.Main.Structures.Rows[0];
		ui.Pump();
		Border card = ui.Window.GetVisualDescendants().OfType<Border>().First(b => b.IsEffectivelyVisible && b.Classes.Contains("sideCard"));
		Color normalEdge = Showing(card.BorderBrush);
		try
		{
			HighContrast.Set(app, true);
			ui.Pump();
			// The same card, redrawn at once: a clear border, no shadow
			Assert.NotEqual(normalEdge, Showing(card.BorderBrush));
			Assert.True(ContrastTests.Ratio(Showing(card.BorderBrush), Token("BgSurface", ThemeVariant.Light)) >= 3);
			ui.Snapshot("65-high-contrast-structures");
			ui.Main.OpenAssessment(ui.Main.Structures.Rows.First(r => r.Address.StartsWith("123", StringComparison.Ordinal)).AssessmentID!.Value);
			ui.Main.Editor.SelectedSection = ui.Main.Editor.Sections.First(s => s.Title == "Inspection");
			ui.Snapshot("66-high-contrast-editor");

			app.RequestedThemeVariant = ThemeVariant.Dark;
			ui.Pump();
			ui.Snapshot("67-dark-high-contrast-editor");
			ui.Main.ShowCommand.Execute(Page.Structures);
			ui.Main.Structures.Selected = ui.Main.Structures.Rows[0];
			ui.Snapshot("68-dark-high-contrast-structures");
		}
		finally
		{
			HighContrast.Set(app, false);
			app.RequestedThemeVariant = ThemeVariant.Light;
		}
		ui.Pump();
		Assert.False(HighContrast.IsOn);
	}

	/// <summary>Settings, Appearance has an Increase Contrast switch, like the density's row; it changes the look at once and is kept.</summary>
	[AvaloniaFact]
	public void TheIncreaseContrastSwitch_ChangesTheLookAtOnce_AndIsKept()
	{
		Application app = Application.Current!;
		using UiSession ui = new UiSession();
		ui.Main.ShowCommand.Execute(Page.Settings);
		ui.Main.Settings.Show(SettingsCategory.Appearance);
		ui.Pump();
		ToggleSwitch toggle = ui.Window.GetVisualDescendants().OfType<ToggleSwitch>().Single(t => t.Name == "IncreaseContrastSwitch");
		Assert.True(toggle.IsEffectivelyVisible);
		Assert.Equal("Increase contrast", AccessibilityTests.NameOf(toggle));
		Assert.NotNull(ToolTip.GetTip(toggle));
		Assert.False(toggle.IsChecked);
		bool? was = MacNative.IncreaseContrastOverride;
		try
		{
			MacNative.IncreaseContrastOverride = false;
			ui.Main.Settings.IncreaseContrast = true;
			ui.Pump();
			Assert.True(HighContrast.IsOn);
			Assert.True(toggle.IsChecked);
			Assert.True(AppSettings.Load().IncreaseContrast);
			ui.Snapshot("69-settings-increase-contrast");
			ui.Main.Settings.IncreaseContrast = false;
			ui.Pump();
			Assert.False(HighContrast.IsOn);
			Assert.False(AppSettings.Load().IncreaseContrast);
		}
		finally
		{
			MacNative.IncreaseContrastOverride = was;
			HighContrast.Set(app, false);
			app.RequestedThemeVariant = ThemeVariant.Light;
		}
	}

	/// <summary>
	/// The empty Summary: one grey line and the same first steps as an empty Structures list, New Structure and, when the
	/// whole database is empty, Open Sample Database.
	/// </summary>
	[AvaloniaFact]
	public void TheEmptySummary_OffersNewStructure_AndTheSampleDatabase()
	{
		using UiSession ui = new UiSession();
		ui.Main.ShowCommand.Execute(Page.Dashboard);
		ui.Pump();
		EmptyState empty = ui.Window.GetVisualDescendants().OfType<EmptyState>().Single(e => e.Name == "SummaryEmptyState");
		Assert.True(empty.IsEffectivelyVisible);
		Assert.Equal("No Structures", empty.Title);
		List<Button> buttons = empty.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
		Assert.Equal(new[] { "New Structure", "Open Sample Database" }, buttons.Select(b => b.Content as string));
		Assert.Contains("primary", buttons[0].Classes);
		Assert.Same(ui.Main.Structures.NewStructureCommand, buttons[0].Command);
		Assert.Same(ui.Main.OpenSampleDatabaseCommand, buttons[1].Command);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("70-summary-empty");

		// New Structure starts a residential structure, as the Structures empty state's does
		buttons[0].Command!.Execute(buttons[0].CommandParameter);
		ui.Pump();
		Assert.IsAssignableFrom<ViewModels.Editor.AssessmentEditorViewModel>(ui.Main.CurrentPage);
		Assert.Equal(1, ui.Main.Editor.Property.StructureTypeID);
	}
}
