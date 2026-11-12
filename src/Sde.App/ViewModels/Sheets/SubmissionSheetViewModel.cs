using Sde.App.Services;

namespace Sde.App.ViewModels.Sheets;

/// <summary>
/// Export ▾ › Submission Folder as a sheet: the choices, an Also Include group, progress while the folder is written,
/// and any warnings afterwards. The work is <see cref="SubmissionViewModel"/>'s; the sheet closes when it does.
/// </summary>
public sealed partial class SubmissionSheetViewModel : SheetViewModel
{
	public SubmissionSheetViewModel(SubmissionViewModel submission) : base("Submission Folder")
	{
		Submission = submission;
		submission.PropertyChanged += OnSubmissionChanged;
		Closing += _ =>
		{
			submission.PropertyChanged -= OnSubmissionChanged;
			submission.IsOpen = false;
		};
	}

	public SubmissionViewModel Submission { get; }

	public override SheetSize Size => SheetSize.Medium;

	/// <summary>Esc does nothing while the folder is being written (Stop does).</summary>
	public override bool CanCancel => !Submission.IsWriting;

	private void OnSubmissionChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(SubmissionViewModel.IsOpen) && !Submission.IsOpen)
		{
			Close();
		}
	}
}
