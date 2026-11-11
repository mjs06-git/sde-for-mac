using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Controls;
using Sde.App.Services;
using Sde.App.ViewModels;
using Sde.App.ViewModels.Sheets;
using Xunit;

namespace Sde.App.Tests;

/// <summary>
/// The sheets as a family: every sheet has a view, every view ends in the one shared footer, the footer's Return and Esc
/// work as they always have, a destructive action is red, and the dimmed window behind a sheet takes no clicks.
/// </summary>
public class SheetTests
{
	/// <summary>Every sheet view model in the app that can be shown (not the abstract bases).</summary>
	private static IEnumerable<Type> SheetTypes() =>
		typeof(SheetViewModel).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(SheetViewModel)) && !t.IsAbstract);

	[AvaloniaFact]
	public void EverySheet_HasAView_WithExactlyOneFooter()
	{
		using UiSession ui = new UiSession();
		List<string> problems = new();
		List<Type> types = SheetTypes().ToList();
		Assert.True(types.Count >= 40, $"only {types.Count} sheets found");
		foreach (Type type in types)
		{
			// The view is chosen by type, so a sheet that was never constructed is enough to find and build it.
			object sheet = RuntimeHelpers.GetUninitializedObject(type);
			IDataTemplate template = ui.Window.DataTemplates.FirstOrDefault(t => t.Match(sheet));
			if (template == null)
			{
				problems.Add(type.Name + ": no view");
				continue;
			}
			Control view = template.Build(sheet);
			int footers = view.GetLogicalDescendants().OfType<SheetFooter>().Count();
			if (footers != 1)
			{
				problems.Add($"{type.Name}: {view.GetType().Name} has {footers} footers");
			}
		}
		Assert.Empty(problems);
	}

	[AvaloniaFact]
	public void TheWindowHoldsOnlyTheShell_NotSheetTemplates()
	{
		// The sheets' views live in Views/*SheetView.axaml; the window maps each one with a single line.
		string xaml = File.ReadAllText(Path.Combine(TestPaths.Root, "src", "Sde.App", "Views", "MainWindow.axaml"));
		Assert.DoesNotContain("<c:SheetFooter", xaml);
		Assert.DoesNotContain("CancelCommand", xaml);
	}

	[AvaloniaFact]
	public async Task Return_RunsTheDefaultAction_AndEscCancels()
	{
		using UiSession ui = new UiSession();
		NameSheetViewModel named = new NameSheetViewModel("New view", "Name", "My view") { Name = "Flooded blocks" };
		Task<string> asking = ui.Main.Sheets.ShowAsync<string>(named);
		ui.Pump();
		SheetFooter footer = Footer(ui);
		Assert.Contains("primary", footer.PrimaryButton.Classes);
		Assert.True(footer.PrimaryButton.IsDefault);
		Assert.Equal("Save", footer.PrimaryButton.Content);
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("90-sheet-name");
		ui.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
		ui.Pump();
		Assert.Equal("Flooded blocks", await asking);
		Assert.False(ui.Main.Sheets.HasSheet);

		asking = ui.Main.Sheets.ShowAsync<string>(new NameSheetViewModel("New view", "Name", "My view") { Name = "Kept" });
		ui.Pump();
		ui.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
		ui.Pump();
		Assert.Null(await asking);
		Assert.False(ui.Main.Sheets.HasSheet);
	}

	[AvaloniaFact]
	public async Task CancelButton_IsTheSheetsOwnCancel()
	{
		using UiSession ui = new UiSession();
		Task<string> asking = ui.Main.Sheets.ShowAsync<string>(new NameSheetViewModel("New view", "Name", "My view") { Name = "Kept" });
		ui.Pump();
		Button cancel = ui.Window.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && b.Content as string == "Cancel");
		cancel.Command.Execute(null);
		Assert.Null(await asking);
	}

	[AvaloniaFact]
	public async Task ALeadingDestructiveAction_IsRed_AndTheDefaultStaysOnTheRight()
	{
		using UiSession ui = new UiSession();
		SaveEditsSheetViewModel sheet = new SaveEditsSheetViewModel("Parcels", 3);
		Task<object> asking = ui.Main.Sheets.ShowAsync(sheet);
		ui.Pump();
		SheetFooter footer = Footer(ui);
		Assert.Equal("Don't Save", footer.LeadingButton.Content);
		Assert.Contains("destructive", footer.LeadingButton.Classes);
		Assert.False(footer.LeadingButton.IsDefault);
		Assert.Equal("Save", footer.PrimaryButton.Content);
		Assert.True(footer.PrimaryButton.IsDefault);
		// Leading at the far left, then Cancel, then the default action at the far right.
		Point leading = footer.LeadingButton.TranslatePoint(new Point(0, 0), ui.Window)!.Value;
		Button cancel = footer.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PART_Cancel");
		Point cancelAt = cancel.TranslatePoint(new Point(0, 0), ui.Window)!.Value;
		Point primary = footer.PrimaryButton.TranslatePoint(new Point(0, 0), ui.Window)!.Value;
		Assert.True(leading.X < cancelAt.X && cancelAt.X < primary.X);
		ui.Snapshot("91-sheet-save-edits");
		footer.LeadingButton.Command.Execute(null);
		Assert.Equal(SaveEditsChoice.Discard, await asking);
	}

	[AvaloniaFact]
	public async Task ADestructiveDefault_IsSolidRed_AndReturnDoesNotPressIt()
	{
		using UiSession ui = new UiSession(ThemeVariant.Dark);
		Task<bool> asking = ((IDialogs)ui.Window).ConfirmAsync("Delete for good?", "123 River Street\n\nThis can't be undone.", "Delete", destructive: true);
		ui.Pump();
		SheetFooter footer = Footer(ui);
		Assert.Contains("destructive", footer.PrimaryButton.Classes);
		Assert.DoesNotContain("primary", footer.PrimaryButton.Classes);
		Assert.False(footer.PrimaryButton.IsDefault);
		ui.Snapshot("86c-confirm-sheet-dark");
		ui.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
		ui.Pump();
		Assert.True(ui.Main.Sheets.HasSheet);
		footer.PrimaryButton.Command.Execute(null);
		Assert.True(await asking);
	}

	[AvaloniaFact]
	public void Done_IsOneDefaultButton_ThatClosesTheSheet()
	{
		using UiSession ui = new UiSession();
		ui.Main.ShowShortcutsCommand.Execute(null);
		ui.Pump();
		SheetFooter footer = Footer(ui);
		Assert.True(footer.IsDone);
		Assert.False(footer.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PART_Cancel").IsVisible);
		Assert.Equal("Done", footer.PrimaryButton.Content);
		Assert.True(footer.PrimaryButton.IsDefault);
		ui.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
		ui.Pump();
		Assert.False(ui.Main.Sheets.HasSheet);
	}

	[AvaloniaFact]
	public void WhileBusy_TheButtonsAreOff_AndStopTakesCancelsPlace()
	{
		RelayCommand stop = new RelayCommand(() => { });
		RelayCommand cancel = new RelayCommand(() => { });
		SheetFooter footer = new SheetFooter { PrimaryText = "Keep Offline", CancelCommand = cancel, StopCommand = stop, LeadingText = "Clear" };
		Assert.Equal("Cancel", footer.EffectiveCancelText);
		Assert.Same(cancel, footer.EffectiveCancelCommand);
		Assert.True(footer.PrimaryIsOn && footer.LeadingIsOn);

		footer.IsBusy = true;
		footer.BusyText = "Downloading 3 of 40";
		Assert.Equal("Stop", footer.EffectiveCancelText);
		Assert.Same(stop, footer.EffectiveCancelCommand);
		Assert.True(footer.CancelIsOn);
		Assert.False(footer.PrimaryIsOn);
		Assert.False(footer.LeadingIsOn);
		Assert.True(footer.IsProgressIndeterminate);
		footer.Progress = 40;
		Assert.False(footer.IsProgressIndeterminate);

		footer.StopCommand = null;
		Assert.Equal("Cancel", footer.EffectiveCancelText);
		Assert.False(footer.CancelIsOn);
	}

	[AvaloniaFact]
	public void TheScrim_DimsTheWindow_AndTakesItsClicks()
	{
		using UiSession ui = new UiSession();
		Sde.App.ViewModels.Page before = ui.Main.ActivePage;
		ui.Main.ShowShortcutsCommand.Execute(null);
		ui.Pump();
		Border scrim = ui.Window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SheetLayer");
		Assert.True(scrim.IsVisible);
		Assert.NotNull(scrim.Background);
		// With motion off (as in every test) the scrim and the sheet are there at once, not part-way through a fade.
		Assert.Equal(1, scrim.Opacity);

		// The Settings row in the sidebar is under the scrim: the scrim is what the pointer finds, and a click there
		// neither opens Settings nor closes the sheet.
		Button settings = ui.Window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SettingsRow");
		Point centre = settings.TranslatePoint(new Point(settings.Bounds.Width / 2, settings.Bounds.Height / 2), ui.Window)!.Value;
		IInputElement hit = ui.Window.InputHitTest(centre);
		Assert.True(hit is Visual v && (v == scrim || v.GetVisualAncestors().Contains(scrim)), $"the pointer found {hit}");
		ui.Window.MouseDown(centre, MouseButton.Left, RawInputModifiers.None);
		ui.Window.MouseUp(centre, MouseButton.Left, RawInputModifiers.None);
		ui.Pump();
		Assert.Equal(before, ui.Main.ActivePage);
		Assert.IsType<ShortcutsSheetViewModel>(ui.Main.Sheets.Current);
	}

	[AvaloniaFact]
	public void MoveOrCopy_InDark_KeepsItsChoicesInAGroup()
	{
		using UiSession ui = new UiSession(ThemeVariant.Dark);
		Task<object> asking = ui.Main.Sheets.ShowAsync(new MoveOrCopySheetViewModel("sde.sqlite"));
		ui.Pump();
		Assert.Equal(3, ui.Window.GetVisualDescendants().OfType<RadioButton>().Count(r => r.IsEffectivelyVisible));
		Assert.Empty(AccessibilityTests.Unnamed(ui.Window));
		ui.Snapshot("92-sheet-move-or-copy-dark");
		ui.Main.Sheets.CancelCurrentCommand.Execute(null);
	}

	private static SheetFooter Footer(UiSession ui) => ui.Window.GetVisualDescendants().OfType<SheetFooter>().Single();
}
