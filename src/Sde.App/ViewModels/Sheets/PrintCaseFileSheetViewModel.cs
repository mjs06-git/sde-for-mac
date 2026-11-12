using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.Reports;

namespace Sde.App.ViewModels.Sheets;

/// <summary>
/// Print Case File…: whether the PDF lists the timeline, and with it the field changes of the edit history. Closes
/// with the <see cref="CaseFileReportOptions"/> to print with, or null when cancelled.
/// </summary>
public sealed partial class PrintCaseFileSheetViewModel : SheetViewModel
{
	public PrintCaseFileSheetViewModel(int editCount) : base("Print case file")
	{
		EditCount = editCount;
	}

	public override SheetSize Size => SheetSize.Small;

	/// <summary>How many field changes the edit history has (the edits option says so).</summary>
	public int EditCount { get; }

	[ObservableProperty]
	public partial bool IncludeTimeline { get; set; } = true;

	[ObservableProperty]
	public partial bool IncludeEdits { get; set; }

	/// <summary>"Include field changes (12)".</summary>
	public string EditsLabel => EditCount == 0 ? "Include field changes" : $"Include field changes ({EditCount:N0})";

	[RelayCommand]
	private void Print() => Close(new CaseFileReportOptions { IncludeTimeline = IncludeTimeline, IncludeEdits = IncludeTimeline && IncludeEdits });
}
