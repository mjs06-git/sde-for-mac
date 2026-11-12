using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.Data;

namespace Sde.App.ViewModels.Sheets;

/// <summary>A kind of document to choose when adding one.</summary>
public sealed record DocumentKindChoice(string Kind, string Label)
{
	public override string ToString() => Label;
}

/// <summary>One document in the Documents sheet's table.</summary>
public sealed class DocumentRowViewModel(DocumentRecord record, DocumentsSheetViewModel sheet)
{
	public DocumentRecord Record { get; } = record;

	public DocumentsSheetViewModel Sheet { get; } = sheet;

	public string Title => DocumentsSheetViewModel.NameOf(Record);

	public string KindText => DocumentsSheetViewModel.KindLabel(Record.Kind);

	/// <summary>The name of the file it was added from (never where it is stored).</summary>
	public string FileName => string.IsNullOrWhiteSpace(Record.OriginalName) ? "" : Path.GetFileName(Record.OriginalName);

	public string AddedText => Record.AddedOn?.ToString("MMM d, yyyy", CultureInfo.GetCultureInfo("en-US")) ?? "";

	public string AddedBy => Record.AddedBy ?? "";

	public string OpenLabel => "Open " + Title;

	public string RevealLabel => $"Show {Title} in Finder";

	public string RemoveLabel => "Remove " + Title;
}

/// <summary>
/// The structure's documents (elevation certificates, permits and others kept with it): a table to open them, show them
/// in Finder or remove them, and Add Document… (or a file dropped on the sheet) to keep another one. Only PDFs, pictures
/// and common office documents up to <see cref="MaxBytes"/> are kept; the app never opens or previews their content
/// itself (Open hands the file to the Mac). Closes with true when something was added or removed.
/// </summary>
public sealed partial class DocumentsSheetViewModel : SheetViewModel
{
	/// <summary>The largest file kept, as for an elevation certificate's PDF.</summary>
	public const long MaxBytes = 200L * 1024 * 1024;

	/// <summary>The kinds of file kept: PDF, pictures and common office formats.</summary>
	public static readonly IReadOnlyList<string> Extensions = new[]
	{
		".pdf", ".jpg", ".jpeg", ".png", ".heic", ".tif", ".tiff",
		".doc", ".docx", ".xls", ".xlsx", ".csv", ".txt", ".rtf", ".pages", ".numbers",
	};

	/// <summary>The kinds offered when adding a document.</summary>
	public static readonly IReadOnlyList<DocumentKindChoice> Kinds = new[]
	{
		new DocumentKindChoice(DocumentKinds.Permit, "Permit"),
		new DocumentKindChoice(DocumentKinds.ElevationCertificate, "Elevation certificate"),
		new DocumentKindChoice(DocumentKinds.Other, "Other"),
	};

	private readonly AppServices _app;
	private readonly Guid _propertyId;
	private readonly Guid? _assessmentId;
	private bool _changed;

	/// <param name="assessmentId">The open assessment, which a new document is recorded with (null from the case file).</param>
	public DocumentsSheetViewModel(AppServices app, Guid propertyId, Guid? assessmentId) : base("Documents")
	{
		_app = app;
		_propertyId = propertyId;
		_assessmentId = assessmentId;
		Reload();
	}

	public override SheetSize Size => SheetSize.Large;

	public ObservableCollection<DocumentRowViewModel> Rows { get; } = new();

	public bool HasRows => Rows.Count > 0;

	/// <summary>A file chosen or dropped, waiting for its kind and title before it is kept; null when none.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsAdding), nameof(PendingName))]
	public partial string PendingPath { get; private set; }

	public bool IsAdding => PendingPath != null;

	public string PendingName => PendingPath == null ? "" : Path.GetFileName(PendingPath);

	[ObservableProperty]
	public partial DocumentKindChoice PendingKind { get; set; } = Kinds[0];

	[ObservableProperty]
	public partial string PendingTitle { get; set; }

	/// <summary>Why the last file was refused or couldn't be kept; null when none.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasError))]
	public partial string Error { get; private set; }

	public bool HasError => !string.IsNullOrEmpty(Error);

	/// <summary>The document's title, or the name of the file it was added from.</summary>
	public static string NameOf(DocumentRecord d)
	{
		string name = !string.IsNullOrWhiteSpace(d.Title) ? d.Title : Path.GetFileName(d.OriginalName ?? "");
		return string.IsNullOrWhiteSpace(name) ? "Document" : name.Trim();
	}

	public static string KindLabel(string kind) => kind switch
	{
		DocumentKinds.ElevationCertificate => "Elevation certificate",
		DocumentKinds.Permit => "Permit",
		null or "" or DocumentKinds.Other => "Other",
		_ => kind,
	};

	/// <summary>Null when the file can be kept, otherwise why not.</summary>
	public static string Refusal(string path)
	{
		string name = Path.GetFileName(path);
		if (!Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
		{
			return $"{name} wasn't added. Documents can be PDFs, pictures or office files.";
		}
		FileInfo info = new FileInfo(path);
		if (!info.Exists)
		{
			return $"{name} isn't there any more.";
		}
		if (info.Length > MaxBytes)
		{
			return $"{name} is larger than {MaxBytes / (1024 * 1024)} MB, so it wasn't added.";
		}
		if (info.Length == 0)
		{
			return $"{name} is empty, so it wasn't added.";
		}
		return null;
	}

	private void Reload()
	{
		Rows.Clear();
		foreach (DocumentRecord d in _app.Store.GetDocuments(_propertyId))
		{
			Rows.Add(new DocumentRowViewModel(d, this));
		}
		OnPropertyChanged(nameof(HasRows));
	}

	/// <summary>Add Document…: choose the file, then its kind and title.</summary>
	[RelayCommand]
	private async Task Add()
	{
		string[] patterns = Extensions.Select(e => "*" + e).ToArray();
		IReadOnlyList<string> picked = await _app.Dialogs.PickFilesAsync("Choose a document", false, "Documents", patterns);
		if (picked != null && picked.Count > 0)
		{
			Offer(picked[0]);
		}
	}

	/// <summary>
	/// A file chosen or dropped: refused with the reason when it isn't a kind kept or is too large, otherwise offered with
	/// a kind and title to confirm (a PDF named like a certificate is offered as one).
	/// </summary>
	public bool Offer(string path)
	{
		Error = Refusal(path);
		if (Error != null)
		{
			PendingPath = null;
			return false;
		}
		string stem = Path.GetFileNameWithoutExtension(path);
		PendingTitle = stem;
		string lower = stem.ToLowerInvariant();
		PendingKind = Kinds.First(k => k.Kind == (lower.Contains("elevation") || lower.Contains("certificate") ? DocumentKinds.ElevationCertificate
			: lower.Contains("permit") ? DocumentKinds.Permit : DocumentKinds.Other));
		PendingPath = path;
		return true;
	}

	/// <summary>Files dropped on the sheet: the first one is offered.</summary>
	public void Dropped(IReadOnlyList<string> paths)
	{
		if (paths != null && paths.Count > 0)
		{
			Offer(paths[0]);
		}
	}

	/// <summary>Keeps the chosen file with the structure: a copy goes into the database's documents folder.</summary>
	[RelayCommand]
	private void Keep()
	{
		if (PendingPath == null)
		{
			return;
		}
		Error = Refusal(PendingPath);
		if (Error != null)
		{
			return;
		}
		try
		{
			_app.Store.AddDocument(_propertyId, PendingPath, PendingKind?.Kind ?? DocumentKinds.Other, PendingTitle, _assessmentId);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
		{
			Error = "The document couldn't be kept: " + ex.Message;
			return;
		}
		_changed = true;
		PendingPath = null;
		Reload();
	}

	/// <summary>Puts the chosen file back without keeping it.</summary>
	[RelayCommand]
	private void Discard()
	{
		PendingPath = null;
		Error = null;
	}

	[RelayCommand]
	private void Open(DocumentRowViewModel row)
	{
		if (row == null)
		{
			return;
		}
		string path = _app.Store.DocumentPath(row.Record);
		if (!File.Exists(path))
		{
			Error = $"The file for {row.Title} is missing from the documents folder.";
			return;
		}
		_app.Dialogs.Open(path);
	}

	[RelayCommand]
	private void Reveal(DocumentRowViewModel row)
	{
		if (row == null)
		{
			return;
		}
		string path = _app.Store.DocumentPath(row.Record);
		if (!File.Exists(path))
		{
			Error = $"The file for {row.Title} is missing from the documents folder.";
			return;
		}
		_app.Dialogs.Reveal(path);
	}

	/// <summary>Remove…: after asking, the document and its stored copy are deleted (it doesn't go to Recently deleted).</summary>
	[RelayCommand]
	private async Task Remove(DocumentRowViewModel row)
	{
		if (row == null)
		{
			return;
		}
		if (!await _app.Dialogs.ConfirmAsync("Remove " + row.Title + "?",
			"The copy kept with the structure is deleted. This can't be undone. The original file, wherever it came from, isn't touched.", "Remove", destructive: true))
		{
			return;
		}
		try
		{
			_app.Store.RemoveDocument(row.Record.DocumentID);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Error = "The document couldn't be removed: " + ex.Message;
			return;
		}
		_changed = true;
		Reload();
	}

	[RelayCommand]
	private void Done() => Close(_changed);
}
