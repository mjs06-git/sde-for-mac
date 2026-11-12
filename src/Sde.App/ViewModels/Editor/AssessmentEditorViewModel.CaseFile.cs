using CommunityToolkit.Mvvm.Input;
using Sde.App.ViewModels.Sheets;

namespace Sde.App.ViewModels.Editor;

/// <summary>
/// The structure across its events, from the editor: its case file, its documents, a comparison of two of its
/// assessments, and Quick Look on the Photos step.
/// </summary>
public sealed partial class AssessmentEditorViewModel
{
	/// <summary>Saves first; false when there is nothing in the database yet to show (a new assessment not started).</summary>
	private bool SaveForStructure()
	{
		if (IsDraft)
		{
			_dirty = true;
		}
		Save();
		return !IsDraft && SaveState == "Saved";
	}

	/// <summary>⋯, then Case File: everything on record about this structure, across every event.</summary>
	[RelayCommand]
	private void ShowCaseFile()
	{
		if (SaveForStructure())
		{
			_main.ShowCaseFile(Property.PropertyID);
		}
	}

	/// <summary>⋯, then Documents…: the structure's documents, to open, add or remove.</summary>
	[RelayCommand]
	private async Task ShowDocuments()
	{
		if (SaveForStructure())
		{
			await _app.Sheets.ShowAsync(new DocumentsSheetViewModel(_app, Property.PropertyID, Assessment.AssessmentID));
		}
	}

	/// <summary>True when the structure has another assessment to compare this one with.</summary>
	public bool CanCompareAssessments => HasOtherAssessments && !IsDraft;

	/// <summary>The menu item's tooltip: why it is unavailable with only one assessment.</summary>
	public string CompareAssessmentsTip => CanCompareAssessments ? "This assessment beside another of the structure's"
		: "The structure has only one assessment. Add a re-inspection to compare.";

	/// <summary>⋯, then Compare Assessments…: this assessment beside the one inspected before it.</summary>
	[RelayCommand]
	private async Task CompareAssessments()
	{
		if (!CanCompareAssessments || !SaveForStructure())
		{
			return;
		}
		if (CompareAssessmentsSheetViewModel.Defaults(OtherAssessments.Select(a => a.Id).ToList(), Assessment.AssessmentID) is (Guid first, Guid second))
		{
			await _app.Sheets.ShowAsync(new CompareAssessmentsSheetViewModel(_app, Property.PropertyID, first, second));
		}
	}

	/// <summary>Space, a double-click or Quick Look on a photo: the photo large over the window, to step through the others.</summary>
	[RelayCommand]
	private async Task QuickLook(PhotoViewModel photo)
	{
		if (Photos.Count == 0)
		{
			return;
		}
		int index = photo == null ? 0 : Math.Max(0, Photos.IndexOf(photo));
		await _app.Sheets.ShowAsync(new QuickLookSheetViewModel(Photos.ToList(), index, MarkUpPhotoAsync));
	}
}
