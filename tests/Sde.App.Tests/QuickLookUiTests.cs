using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Sde.App.Services;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Sheets;
using Sde.Core.Photos;
using SkiaSharp;

namespace Sde.App.Tests;

/// <summary>
/// Quick Look on the Photos step (3.5): Space, a double-click or the menu shows the photo large; the arrow keys and
/// chevrons step, Esc or Space closes, and a marked-up photo shows its marks. Writes the Quick Look screenshots.
/// </summary>
public class QuickLookUiTests
{
	/// <summary>The first sample with three made-up photos, on the Photos and notes step.</summary>
	private static async Task<(UiSession Ui, AssessmentEditorViewModel Editor)> WithPhotos(ThemeVariant theme = null)
	{
		UiSession ui = new UiSession(theme);
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		ui.Pump();
		ui.Main.OpenAssessment(ui.Main.Structures.Rows.First(r => r.Address.StartsWith("123", StringComparison.Ordinal)).AssessmentID!.Value);
		ui.Pump();
		AssessmentEditorViewModel e = ui.Main.Editor;
		string wall = Path.Combine(ui.Folder, "front wall.jpg");
		File.WriteAllBytes(wall, FieldPhotos.Wall());
		string side = Path.Combine(ui.Folder, "side.jpg");
		File.WriteAllBytes(side, FieldPhotos.Jpeg(new SKColor(0x7A, 0x9C, 0xB8)));
		string back = Path.Combine(ui.Folder, "back.jpg");
		File.WriteAllBytes(back, FieldPhotos.Jpeg(new SKColor(0xA8, 0x8F, 0x6A)));
		await e.AddPhotosAsync(new[] { wall, side, back });
		e.Photos[0].Description = "Front wall, water line at the second siding seam";
		e.SelectedSection = e.Sections.OfType<PhotosSectionViewModel>().Single();
		ui.Pump();
		return (ui, e);
	}

	private static async Task<QuickLookSheetViewModel> Open(UiSession ui, AssessmentEditorViewModel editor, int index)
	{
		_ = editor.QuickLookCommand.ExecuteAsync(editor.Photos[index]);
		ui.Pump();
		QuickLookSheetViewModel sheet = Assert.IsType<QuickLookSheetViewModel>(ui.Main.Sheets.Current);
		await sheet.Loaded;
		ui.Pump();
		return sheet;
	}

	[AvaloniaFact]
	public async Task TheArrowKeys_Step_AndSpaceCloses()
	{
		(UiSession ui, AssessmentEditorViewModel editor) = await WithPhotos();
		using UiSession session = ui;
		QuickLookSheetViewModel sheet = await Open(ui, editor, 0);
		Assert.Equal(SheetSize.Large, sheet.Size);
		Assert.Equal("1 of 3", sheet.PositionText);
		Assert.Equal("Front wall, water line at the second siding seam", sheet.Caption);
		Assert.True(sheet.HasImage);
		Assert.False(sheet.PreviousCommand.CanExecute(null));
		Image image = ui.Window.GetVisualDescendants().OfType<Image>().Single(i => i.Name == "QuickLookImage");
		Assert.Same(sheet.Image, image.Source);
		// Decoded no larger than the stage needs.
		Assert.True(Math.Max(sheet.Image.PixelSize.Width, sheet.Image.PixelSize.Height) <= QuickLookSheetViewModel.MaxEdge);

		Control view = ui.Window.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "QuickLook");
		view.Focus();
		ui.Pump();
		Bitmap first = sheet.Image;
		ui.Window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
		await sheet.Loaded;
		ui.Pump();
		Assert.Equal("2 of 3", sheet.PositionText);
		Assert.Same(editor.Photos[1], sheet.Current);
		Assert.NotSame(first, sheet.Image);
		ui.Window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
		ui.Window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);   // the last one: stays
		await sheet.Loaded;
		Assert.Equal("3 of 3", sheet.PositionText);
		Assert.False(sheet.NextCommand.CanExecute(null));
		ui.Window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
		await sheet.Loaded;
		Assert.Equal("2 of 3", sheet.PositionText);

		ui.Window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
		ui.Pump();
		Assert.True(sheet.IsClosed);
		Assert.False(ui.Main.Sheets.HasSheet);
		Assert.Null(sheet.Image);   // let go when it closed
	}

	[AvaloniaFact]
	public async Task Escape_Closes_AndTheChevronsStep()
	{
		(UiSession ui, AssessmentEditorViewModel editor) = await WithPhotos();
		using UiSession session = ui;
		QuickLookSheetViewModel sheet = await Open(ui, editor, 1);
		Assert.Equal("2 of 3", sheet.PositionText);
		Button previous = ui.Window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "QuickLookPrevious");
		previous.Command.Execute(null);
		await sheet.Loaded;
		Assert.Equal("1 of 3", sheet.PositionText);
		ui.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
		ui.Pump();
		Assert.True(sheet.IsClosed);
	}

	[AvaloniaFact]
	public async Task AMarkedUpPhoto_ShowsItsMarks_AndMarkUpOpensTheMarkupSheet()
	{
		(UiSession ui, AssessmentEditorViewModel editor) = await WithPhotos();
		using UiSession session = ui;
		PhotoViewModel photo = editor.Photos[0];
		PhotoMarkup markup = new PhotoMarkup();
		markup.Shapes.Add(new MarkupShape { Kind = MarkupKinds.Box, X1 = 0.1, Y1 = 0.1, X2 = 0.9, Y2 = 0.9, Color = "#E53935", Width = 0.05 });
		photo.SetMarkup(markup);

		QuickLookSheetViewModel sheet = await Open(ui, editor, 0);
		Assert.True(sheet.ShowsMarkup);
		Assert.Equal("Change Marks…", sheet.MarkUpText);
		// The picture shown is the marked-up one: a red box edge where the photo's sky is.
		using (MemoryStream png = new MemoryStream())
		{
			sheet.Image.Save(png);
			using SKBitmap shown = SKBitmap.Decode(png.ToArray());
			SKColor edge = shown.GetPixel((int)(shown.Width * 0.5), (int)(shown.Height * 0.1));
			Assert.True(edge.Red > 180 && edge.Green < 110, edge.ToString());
		}
		await editor.Photos[1].ThumbnailLoaded;

		// Without marks, the next photo shows as it is.
		sheet.NextCommand.Execute(null);
		await sheet.Loaded;
		Assert.False(sheet.ShowsMarkup);

		// Mark Up… puts the markup sheet over Quick Look; Quick Look is there again when it closes.
		Task marking = sheet.MarkUpCommand.ExecuteAsync(null);
		ui.Pump();
		Assert.IsType<PhotoMarkupSheetViewModel>(ui.Main.Sheets.Current);
		ui.Main.Sheets.Current.Cancel();
		await marking;
		ui.Pump();
		Assert.Same(sheet, ui.Main.Sheets.Current);
		sheet.Cancel();
	}

	[AvaloniaFact]
	public async Task TheThumbnail_OpensQuickLook_AndItsMenuOffersIt()
	{
		(UiSession ui, AssessmentEditorViewModel editor) = await WithPhotos();
		using UiSession session = ui;
		List<Button> thumbs = ui.Window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("photoThumb")).ToList();
		Assert.Equal(3, thumbs.Count);
		Assert.Equal("Quick Look side.jpg", Avalonia.Automation.AutomationProperties.GetName(thumbs[1]).Replace(editor.Photos[1].FileName, "side.jpg"));
		thumbs[1].Command.Execute(thumbs[1].CommandParameter);
		ui.Pump();
		QuickLookSheetViewModel sheet = Assert.IsType<QuickLookSheetViewModel>(ui.Main.Sheets.Current);
		Assert.Equal("2 of 3", sheet.PositionText);
		sheet.Cancel();
		ui.Pump();

		// Space on the focused thumbnail.
		Assert.True(thumbs[2].Focus());
		ui.Pump();
		Assert.Same(thumbs[2], ui.Window.FocusManager!.GetFocusedElement());
		ui.Window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
		ui.Window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);   // a button clicks when the key comes up
		ui.Pump();
		sheet = Assert.IsType<QuickLookSheetViewModel>(ui.Main.Sheets.Current);
		Assert.Equal("3 of 3", sheet.PositionText);
		sheet.Cancel();

		// The context menu: its Quick Look finds the editor's command once it opens.
		ContextMenu menu = thumbs[0].ContextMenu!;
		menu.Open(thumbs[0]);
		ui.Pump();
		List<MenuItem> items = menu.Items.OfType<MenuItem>().ToList();
		Assert.Equal(new[] { "Quick Look", "Open in Preview", "Mark Up…" }, items.Select(i => (string)i.Header));
		Assert.Same(editor.QuickLookCommand, items[0].Command);
		Assert.Same(editor.Photos[0], items[0].CommandParameter);
		menu.Close();
	}

	[AvaloniaFact]
	public async Task Screenshots_LightAndDark()
	{
		foreach ((ThemeVariant theme, string name) in new[] { (ThemeVariant.Light, "85-quick-look"), (ThemeVariant.Dark, "86-dark-quick-look") })
		{
			(UiSession ui, AssessmentEditorViewModel editor) = await WithPhotos(theme);
			using UiSession session = ui;
			PhotoMarkup markup = new PhotoMarkup();
			markup.Shapes.Add(new MarkupShape { Kind = MarkupKinds.WaterLine, X1 = 0.04, Y1 = 0.62, X2 = 0.96, Y2 = 0.62, Color = "#1565C0", Text = "Water 2 ft 6 in" });
			editor.Photos[0].SetMarkup(markup);
			await Open(ui, editor, 0);
			ui.Snapshot(name);
		}
	}
}
