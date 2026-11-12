using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Sde.App.ViewModels.Editor;
using Sde.App.ViewModels.Sheets;
using Sde.Data;
using Sde.TestSupport;

namespace Sde.App.Tests;

/// <summary>
/// The Documents sheet (3.5): the structure's documents listed, opened, shown in Finder and removed, and new ones added
/// (chosen or dropped) when they are a kind kept and not too large. Writes the documents screenshots.
/// </summary>
public class DocumentsUiTests
{
	private static (UiSession Ui, AssessmentEditorViewModel Editor) WithEditor(ThemeVariant theme = null)
	{
		UiSession ui = new UiSession(theme);
		ScreenTourTests.AddSamples(ui.Store);
		ui.Services.RaiseDataChanged();
		ui.Pump();
		ui.Main.OpenAssessment(ui.Main.Structures.Rows.First(r => r.Address.StartsWith("123", StringComparison.Ordinal)).AssessmentID!.Value);
		ui.Pump();
		return (ui, ui.Main.Editor);
	}

	private static string Write(UiSession ui, string name, int bytes = 64)
	{
		string path = Path.Combine(ui.Folder, name);
		File.WriteAllBytes(path, Enumerable.Repeat((byte)'x', bytes).ToArray());
		return path;
	}

	private static DocumentsSheetViewModel Open(UiSession ui, AssessmentEditorViewModel editor)
	{
		_ = editor.ShowDocumentsCommand.ExecuteAsync(null);
		ui.Pump();
		return Assert.IsType<DocumentsSheetViewModel>(ui.Main.Sheets.Current);
	}

	[AvaloniaFact]
	public void AddDocument_ChoosesKindAndTitle_AndKeepsACopy()
	{
		(UiSession ui, AssessmentEditorViewModel editor) = WithEditor();
		using UiSession session = ui;
		DocumentsSheetViewModel sheet = Open(ui, editor);
		Assert.False(sheet.HasRows);

		string permit = Write(ui, "Building permit BP-0101.pdf");
		ui.Dialogs.Answers.Enqueue(new[] { permit });
		sheet.AddCommand.Execute(null);
		ui.Pump();
		Assert.True(sheet.IsAdding);
		Assert.Equal("Building permit BP-0101.pdf", sheet.PendingName);
		Assert.Equal(DocumentKinds.Permit, sheet.PendingKind.Kind);
		Assert.Equal("Building permit BP-0101", sheet.PendingTitle);
		sheet.PendingTitle = "Permit for repairs";
		sheet.KeepCommand.Execute(null);
		ui.Pump();

		Assert.False(sheet.IsAdding);
		DocumentRowViewModel row = Assert.Single(sheet.Rows);
		Assert.Equal("Permit for repairs", row.Title);
		Assert.Equal("Permit", row.KindText);
		Assert.Equal("Building permit BP-0101.pdf", row.FileName);
		Assert.Equal("Reviewer A", row.AddedBy);
		DocumentRecord stored = Assert.Single(ui.Store.GetDocuments(editor.Property.PropertyID));
		Assert.Equal(editor.Assessment.AssessmentID, stored.AssessmentID);
		string copy = ui.Store.DocumentPath(stored);
		Assert.True(File.Exists(copy));
		Assert.NotEqual(permit, copy);

		// "Other" is a kind too; a file dropped on the sheet is offered the same way.
		sheet.Dropped(new[] { Write(ui, "notes from the owner.docx") });
		Assert.Equal(DocumentKinds.Other, sheet.PendingKind.Kind);
		sheet.KeepCommand.Execute(null);
		Assert.Equal(2, sheet.Rows.Count);
		sheet.DoneCommand.Execute(null);
		Assert.Equal(true, sheet.Closed.Result);
	}

	[AvaloniaFact]
	public void RefusedFiles_SayWhy_AndAreNotKept()
	{
		(UiSession ui, AssessmentEditorViewModel editor) = WithEditor();
		using UiSession session = ui;
		DocumentsSheetViewModel sheet = Open(ui, editor);

		foreach (string name in new[] { "installer.app", "script.sh", "macro.command", "archive.zip" })
		{
			Assert.False(sheet.Offer(Write(ui, name)), name);
			Assert.False(sheet.IsAdding);
			Assert.Contains("PDFs, pictures or office files", sheet.Error);
		}
		Assert.False(sheet.Offer(Write(ui, "empty.pdf", 0)));
		Assert.Contains("empty", sheet.Error);
		Assert.False(sheet.Offer(Path.Combine(ui.Folder, "gone.pdf")));
		Assert.NotNull(DocumentsSheetViewModel.Refusal(Path.Combine(ui.Folder, "huge.pdf")));
		Assert.Empty(ui.Store.GetDocuments(editor.Property.PropertyID));

		// A good one clears the message.
		Assert.True(sheet.Offer(Write(ui, "Elevation certificate.pdf")));
		Assert.Null(sheet.Error);
		Assert.Equal(DocumentKinds.ElevationCertificate, sheet.PendingKind.Kind);
		sheet.DiscardCommand.Execute(null);
		Assert.False(sheet.IsAdding);
		Assert.Empty(ui.Store.GetDocuments(editor.Property.PropertyID));
	}

	[AvaloniaFact]
	public void TooLargeAFile_IsRefused()
	{
		using TempDatabase folder = new TempDatabase();
		string path = Path.Combine(folder.Folder, "scan.pdf");
		using (FileStream s = File.Create(path))
		{
			s.SetLength(DocumentsSheetViewModel.MaxBytes + 1);
		}
		Assert.Contains("larger than 200 MB", DocumentsSheetViewModel.Refusal(path));
	}

	[AvaloniaFact]
	public async Task OpenReveal_AndRemove_AfterConfirming()
	{
		(UiSession ui, AssessmentEditorViewModel editor) = WithEditor();
		using UiSession session = ui;
		DocumentRecord d = ui.Store.AddDocument(editor.Property.PropertyID, Write(ui, "EC 2026.pdf"), DocumentKinds.ElevationCertificate, "Elevation certificate");
		string copy = ui.Store.DocumentPath(d);
		DocumentsSheetViewModel sheet = Open(ui, editor);
		DocumentRowViewModel row = Assert.Single(sheet.Rows);
		Assert.Equal("Elevation certificate", row.KindText);

		sheet.OpenCommand.Execute(row);
		sheet.RevealCommand.Execute(row);
		Assert.Equal(new[] { copy, "reveal:" + copy }, ui.Dialogs.Opened);

		// Declined: nothing changes.
		ui.Dialogs.Answers.Enqueue(false);
		await sheet.RemoveCommand.ExecuteAsync(row);
		Assert.Single(sheet.Rows);
		Assert.True(File.Exists(copy));

		// Confirmed: the record and its stored copy are gone.
		ui.Dialogs.Answers.Enqueue(true);
		await sheet.RemoveCommand.ExecuteAsync(row);
		Assert.Empty(sheet.Rows);
		Assert.False(File.Exists(copy));
		Assert.Empty(ui.Store.GetDocuments(editor.Property.PropertyID));
	}

	[AvaloniaFact]
	public void TheEditorMenu_HasDocuments_AndTheCaseFileCardOpensTheSheet()
	{
		(UiSession ui, CaseFileSample sample) = (new UiSession(), null);
		using UiSession session = ui;
		sample = CaseFileSample.Create(ui.Store, Path.Combine(ui.Folder, "source"));
		ui.Services.RaiseDataChanged();
		ui.Main.ShowCaseFile(sample.PropertyId);
		ui.Pump();
		_ = ui.Main.CaseFilePage.ShowDocumentsCommand.ExecuteAsync(null);
		ui.Pump();
		DocumentsSheetViewModel sheet = Assert.IsType<DocumentsSheetViewModel>(ui.Main.Sheets.Current);
		Assert.Equal("Elevation certificate 2026", Assert.Single(sheet.Rows).Title);
		sheet.Cancel();
	}

	[AvaloniaFact]
	public void Screenshots_LightAndDark()
	{
		foreach ((ThemeVariant theme, string name) in new[] { (ThemeVariant.Light, "83-documents"), (ThemeVariant.Dark, "84-dark-documents") })
		{
			(UiSession ui, AssessmentEditorViewModel editor) = WithEditor(theme);
			using UiSession session = ui;
			Guid pid = editor.Property.PropertyID;
			ui.Store.AddDocument(pid, Write(ui, "Elevation certificate 2026.pdf"), DocumentKinds.ElevationCertificate, "Elevation certificate");
			ui.Store.AddDocument(pid, Write(ui, "BP-0101.pdf"), DocumentKinds.Permit, "Repair permit BP-0101");
			ui.Store.AddDocument(pid, Write(ui, "Owner letter.docx"), DocumentKinds.Other, "Letter from the owner");
			DocumentsSheetViewModel sheet = Open(ui, editor);
			sheet.Offer(Write(ui, "Contractor estimate.pdf"));
			ui.Snapshot(name);
		}
	}
}
