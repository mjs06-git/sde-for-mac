using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.Data;

namespace Sde.App.ViewModels;

/// <summary>
/// The case file page: one structure across every event, opened from the editor, the structure list or ⌘K. It is a
/// sub-page of the page it was opened from, so the title band's back button leads there (to the open assessment when
/// the editor opened it), and the sidebar keeps that page chosen.
/// </summary>
public sealed partial class MainViewModel
{
	/// <summary>The page the case file's back button leads to (the editor, or a sidebar page).</summary>
	private Page _caseFileReturn = Page.Structures;

	/// <summary>The sidebar page chosen while the case file shows.</summary>
	private Page _caseFileRoot = Page.Structures;

	/// <summary>The open case file, or null.</summary>
	[ObservableProperty]
	public partial CaseFileViewModel CaseFilePage { get; private set; }

	partial void OnCaseFilePageChanged(CaseFileViewModel oldValue, CaseFileViewModel newValue)
	{
		oldValue?.Detach();
		OnPropertyChanged(nameof(PageTitle));
		OnPropertyChanged(nameof(PageParent));
		OnPropertyChanged(nameof(HasPageParent));
	}

	/// <summary>The sidebar page chosen while an assessment is open (the case file's own page when it opened the assessment).</summary>
	private Page EditorSidebarPage => _editorParent == Page.CaseFile ? _caseFileRoot : _editorParent;

	/// <summary>What the case file's back button says: the open assessment's address, or the page it came from.</summary>
	private string CaseFileParentTitle => _caseFileReturn == Page.Editor && Editor != null ? Editor.Title
		: SidebarItems.FirstOrDefault(i => i.Page == _caseFileRoot)?.Title ?? "Structures";

	/// <summary>
	/// The structure the ⌘K palette's "Case file" command is for: the open assessment's while one is showing, otherwise
	/// the one selected in the structure list. Null when there is none.
	/// </summary>
	public Guid? CurrentStructureId => ActivePage == Page.Editor && Editor != null && !Editor.IsDraft ? Editor.Property.PropertyID
		: ActivePage == Page.CaseFile && CaseFilePage != null ? CaseFilePage.PropertyId
		: Structures.Selected?.Summary.PropertyID;

	/// <summary>
	/// Shows a structure's case file. <paramref name="structure"/> is a structure's ID, a row of the structure list or a
	/// search result; null means the current structure (see <see cref="CurrentStructureId"/>).
	/// </summary>
	[RelayCommand]
	private void ShowCaseFile(object structure)
	{
		Guid? id = structure switch
		{
			Guid g => g,
			StructureRowViewModel row => row.Summary.PropertyID,
			StructureSummary summary => summary.PropertyID,
			_ => CurrentStructureId,
		};
		if (id is not Guid propertyId || propertyId == Guid.Empty)
		{
			_app.Dialogs.Notify("No structure", "Choose a structure first.");
			return;
		}
		ShowCaseFile(propertyId);
	}

	/// <summary>Shows a structure's case file, remembering where to go back to.</summary>
	public void ShowCaseFile(Guid propertyId)
	{
		Editor?.Save();
		CaseFileViewModel page = new CaseFileViewModel(_app, this, propertyId);
		if (!page.Load())
		{
			page.Detach();
			return;
		}
		if (ActivePage == Page.Editor && Editor != null)
		{
			_caseFileReturn = Page.Editor;
			_caseFileRoot = EditorSidebarPage;
		}
		else if (ActivePage != Page.CaseFile)
		{
			_caseFileReturn = SidebarPage(ActivePage);
			_caseFileRoot = _caseFileReturn;
		}
		RememberRecent(propertyId);
		CaseFilePage = page;
		ActivePage = Page.CaseFile;
		CurrentPage = page;
		RefreshStatus();
	}

	/// <summary>The case file's back button: the open assessment it came from, or the page.</summary>
	public void CloseCaseFile()
	{
		if (_caseFileReturn == Page.Editor && Editor != null)
		{
			Show(Page.Editor);
			RefreshEditor();
		}
		else
		{
			Show(_caseFileReturn == Page.Editor ? _caseFileRoot : _caseFileReturn);
		}
		CaseFilePage = null;
	}

	/// <summary>
	/// Opens one of the case file's assessments. Closing it leads back to the case file, whose own back button then
	/// leads to the page the case file was opened from.
	/// </summary>
	public void OpenAssessmentFromCaseFile(Guid assessmentId)
	{
		if (_caseFileReturn == Page.Editor)
		{
			_caseFileReturn = _caseFileRoot;
		}
		OpenAssessmentFrom(assessmentId, Page.CaseFile);
	}
}
