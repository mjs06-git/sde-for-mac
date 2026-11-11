using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Sde.App.ViewModels.Editor;
using Page = Sde.App.ViewModels.Page;

namespace Sde.App.Tests;

/// <summary>
/// Public Sans is wider and taller than the Mac's system font, so every button, text box, pop-up menu and check box in
/// the main window is checked to hold its text: each line of text inside it must lie within the control's own height.
/// Run it at both densities (once as it is, once with SDE_TEST_COMPACT=1) so neither Comfortable's nor Compact's
/// sizes clip a label.
/// </summary>
public class FitTests
{
	[AvaloniaFact]
	public void TheTextInEveryControl_FitsInsideTheControl()
	{
		using UiSession ui = new UiSession();
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		ui.Main.Structures.Selected = ui.Main.Structures.Rows[0];
		List<string> problems = new();
		int checkedControls = 0;
		void Check(string page)
		{
			ui.Pump();
			foreach (string problem in Clipped(ui.Window, ref checkedControls))
			{
				problems.Add(page + ": " + problem);
			}
		}

		Check("Structures");
		ui.Main.OpenAssessment(ui.Main.Structures.Rows.First(r => r.Address.StartsWith("123")).AssessmentID!.Value);
		AssessmentEditorViewModel editor = ui.Main.Editor;
		foreach (SectionViewModel section in editor.Sections)
		{
			editor.SelectedSection = section;
			Check("Editor, " + section.Title);
		}
		ui.Main.ShowCommand.Execute(Page.Dashboard);
		Check("Summary");
		ui.Main.ShowCommand.Execute(Page.ImportExport);
		Check("Import & Export");
		ui.Main.ShowCommand.Execute(Page.Settings);
		Check("Settings");

		Assert.True(checkedControls > 100, $"Only {checkedControls} controls were checked");
		Assert.True(problems.Count == 0, string.Join("\n", problems.Distinct()));
	}

	/// <summary>The check itself: a button too short for its label is caught, one tall enough is not.</summary>
	[AvaloniaFact]
	public void TheCheck_CatchesALabelTooTallForItsButton()
	{
		Button tooShort = new Button { Content = "Save", Height = 10, MinHeight = 0, Padding = new Thickness(0) };
		Button fine = new Button { Content = "Save", Height = 40 };
		Window window = new Window { Width = 300, Height = 200, Content = new StackPanel { Children = { tooShort, fine } } };
		window.Show();
		try
		{
			int count = 0;
			List<string> found = Clipped(window, ref count).ToList();
			Assert.Equal(2, count);
			Assert.Single(found);
			Assert.Contains("10 high", found[0]);
		}
		finally
		{
			window.Close();
		}
	}

	/// <summary>Each visible control whose text runs past its top or bottom edge, with the numbers.</summary>
	private static IEnumerable<string> Clipped(Window window, ref int count)
	{
		List<string> found = new();
		List<Control> controls = window.GetVisualDescendants().OfType<Control>()
			.Where(c => c is Button or TextBox or ComboBox or CheckBox && c.IsEffectivelyVisible && c.Bounds.Height > 0)
			.ToList();
		foreach (Control control in controls)
		{
			count++;
			foreach (Visual text in control.GetVisualDescendants().Where(v => v is TextBlock or TextPresenter))
			{
				// Text that belongs to a control inside this one (a check box in a button) is checked with that control
				if (!((Control)text).IsEffectivelyVisible || text.GetVisualAncestors().OfType<Control>().First(IsChecked) != control)
				{
					continue;
				}
				double height = text switch
				{
					TextBlock block => block.TextLayout.Height + block.Padding.Top + block.Padding.Bottom,
					TextPresenter presenter => presenter.TextLayout.Height,
					_ => 0,
				};
				Point? origin = text.TranslatePoint(new Point(0, 0), control);
				if (origin == null)
				{
					continue;
				}
				double top = origin.Value.Y;
				if (top < -0.5 || top + height > control.Bounds.Height + 0.5)
				{
					string what = text is TextBlock tb ? tb.Text : ((TextPresenter)text).Text;
					found.Add($"{control.GetType().Name} \"{what}\" ({string.Join(" ", control.Classes)}): text {top:0.#} to {top + height:0.#} in a control {control.Bounds.Height:0.#} high");
				}
			}
		}
		return found;
	}

	private static bool IsChecked(Control c) => c is Button or TextBox or ComboBox or CheckBox;
}
