using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Sde.Interop;

namespace Sde.App.ViewModels;

/// <summary>
/// The rows that came from one row of the file, for the preview: one row when it was not split, a heading and one row
/// per building when it covers several buildings (see <see cref="SourceProfile.SplitMode"/>).
/// </summary>
public sealed class SourcePreviewGroupViewModel(ImportRowGroup group, IReadOnlyList<SourcePreviewRowViewModel> rows)
{
	/// <summary>The row's number in the file.</summary>
	public int RowNumber { get; } = group.RowNumber;

	/// <summary>The record ID as written in the file.</summary>
	public string RecordId { get; } = group.RecordId ?? "(no record ID)";

	/// <summary>The rows of this source row that the preview shows, in building order.</summary>
	public IReadOnlyList<SourcePreviewRowViewModel> Rows { get; } = rows;

	/// <summary>The row became several structures.</summary>
	public bool IsSplit { get; } = group.IsSplit;

	/// <summary>"Row 2 · P-1 · 3 buildings", above the buildings of a split row.</summary>
	public string Header => $"Row {RowNumber} · {RecordId} · {(group.Rows.Count == 1 ? "1 building" : $"{group.Rows.Count} buildings")}";
}

/// <summary>
/// Import inspections or parcels, a record that covers several buildings: the profile's split settings (one row is one
/// building, a number of buildings in a column, or a list of buildings in a column), the preview grouped by the row of
/// the file, and the structures made earlier that a row no longer lists.
/// </summary>
public sealed partial class SourceImportViewModel
{
	/// <summary>The choices for "One row is", in the order of <see cref="SourceSplitMode"/>.</summary>
	public static IReadOnlyList<string> SplitModeChoices { get; } = new[]
	{
		"One building",
		"A number of buildings in a column",
		"A list of buildings in a column",
	};

	/// <summary>The choices for the field that tells the buildings apart, in the order of <see cref="SplitSuffixFieldIndex"/>.</summary>
	public static IReadOnlyList<string> SplitSuffixFieldChoices { get; } = new[] { "Unit", "Lot number" };

	/// <summary>0 = one row is one building, 1 = a number of buildings in a column, 2 = a list of buildings in a column.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(IsSplitting), nameof(IsSplitByCount), nameof(IsSplitByList))]
	public partial int SplitModeIndex { get; set; }

	public bool IsSplitting => SplitModeIndex > 0;

	public bool IsSplitByCount => SplitModeIndex == (int)SourceSplitMode.CountColumn;

	public bool IsSplitByList => SplitModeIndex == (int)SourceSplitMode.DelimitedColumn;

	/// <summary>"(none)" plus the columns (after checklist items are combined), for the buildings column.</summary>
	[ObservableProperty]
	public partial IReadOnlyList<string> SplitColumnChoices { get; set; } = new[] { None };

	/// <summary>The column with the number of buildings, or with the list of buildings.</summary>
	[ObservableProperty]
	public partial string SplitColumn { get; set; } = None;

	/// <summary>What separates the buildings in a list; blank is a semicolon.</summary>
	[ObservableProperty]
	public partial string SplitDelimiter { get; set; }

	/// <summary>0 = the unit (after the street number), 1 = the lot number.</summary>
	[ObservableProperty]
	public partial int SplitSuffixFieldIndex { get; set; }

	/// <summary>The word before a building's number ("Bldg" gives "Bldg 2"); blank is "Bldg".</summary>
	[ObservableProperty]
	public partial string SplitLabel { get; set; }

	partial void OnSplitModeIndexChanged(int value) => SplitEdited();

	partial void OnSplitColumnChanged(string value) => SplitEdited();

	partial void OnSplitDelimiterChanged(string value) => SplitEdited();

	partial void OnSplitSuffixFieldIndexChanged(int value) => SplitEdited();

	partial void OnSplitLabelChanged(string value) => SplitEdited();

	/// <summary>A split setting changed: store it in the profile and preview again.</summary>
	private void SplitEdited()
	{
		if (_suspend)
		{
			return;
		}
		_profile.SplitMode = (SourceSplitMode)Math.Clamp(SplitModeIndex, 0, 2);
		_profile.SplitColumn = _profile.SplitMode == SourceSplitMode.None ? null : Real(SplitColumn);
		_profile.SplitDelimiter = string.IsNullOrEmpty(SplitDelimiter) ? null : SplitDelimiter;
		_profile.SplitSuffixField = SplitSuffixFieldIndex == 1 ? SourceProfile.SplitFieldLot : null;
		_profile.SplitLabel = string.IsNullOrWhiteSpace(SplitLabel) ? null : SplitLabel.Trim();
		SchedulePreview();
	}

	/// <summary>Shows a profile's split settings (called while the profile is applied, with edits suspended).</summary>
	private void ShowSplit(SourceProfile p)
	{
		SplitModeIndex = (int)p.SplitMode;
		SplitColumn = p.SplitColumn is string c && SplitColumnChoices.Contains(c) ? c : p.SplitColumn ?? None;
		SplitDelimiter = p.SplitDelimiter;
		SplitSuffixFieldIndex = p.EffectiveSplitSuffixField == SourceProfile.SplitFieldLot ? 1 : 0;
		SplitLabel = p.SplitLabel;
	}

	/// <summary>The columns the buildings can come from, after the layout changed.</summary>
	private void RefreshSplitColumns()
	{
		SplitColumnChoices = new[] { None }.Concat(_shaped.Table.Columns).ToList();
	}

	/// <summary>The preview's rows, grouped under the row of the file they came from.</summary>
	public ObservableCollection<SourcePreviewGroupViewModel> PreviewGroups { get; } = new();

	/// <summary>Structures made earlier from a row that now lists fewer buildings: reported, never deleted.</summary>
	public ObservableCollection<string> MissingSplitStructures { get; } = new();

	public bool HasMissingSplitStructures => MissingSplitStructures.Count > 0;

	/// <summary>Groups the shown rows by the row of the file, and lists the structures no longer in the file.</summary>
	private void ShowGroups(SourcePreview preview)
	{
		Dictionary<ImportRow, SourcePreviewRowViewModel> shown = new(ReferenceEqualityComparer.Instance);
		foreach (SourcePreviewRowViewModel r in PreviewRows)
		{
			shown[r.Row] = r;
		}
		PreviewGroups.Clear();
		foreach (ImportRowGroup g in preview.Groups)
		{
			List<SourcePreviewRowViewModel> rows = g.Rows.Where(shown.ContainsKey).Select(r => shown[r]).ToList();
			if (rows.Count > 0)
			{
				PreviewGroups.Add(new SourcePreviewGroupViewModel(g, rows));
			}
		}
		MissingSplitStructures.Clear();
		foreach (MissingSplitStructure m in preview.MissingSplitStructures)
		{
			MissingSplitStructures.Add(m.Description);
		}
		OnPropertyChanged(nameof(HasMissingSplitStructures));
		OnPropertyChanged(nameof(HasIssues));
	}

	/// <summary>What stops the import, one line each, for the problems list.</summary>
	public IReadOnlyList<string> MappingProblemLines => Lines(MappingProblems);

	/// <summary>Notes about the file that don't stop the import, one line each.</summary>
	public IReadOnlyList<string> ShapeNoteLines => Lines(ShapeNotes);

	/// <summary>Anything to list in the problems card: problems, notes or structures no longer in the file.</summary>
	public bool HasIssues => MappingProblemLines.Count > 0 || ShapeNoteLines.Count > 0 || HasMissingSplitStructures;

	partial void OnMappingProblemsChanged(string value)
	{
		OnPropertyChanged(nameof(MappingProblemLines));
		OnPropertyChanged(nameof(HasIssues));
	}

	partial void OnShapeNotesChanged(string value)
	{
		OnPropertyChanged(nameof(ShapeNoteLines));
		OnPropertyChanged(nameof(HasIssues));
	}

	private static IReadOnlyList<string> Lines(string text) =>
		string.IsNullOrWhiteSpace(text) ? Array.Empty<string>() : text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

	/// <summary>
	/// The key a preview row keeps across a fresh preview: its row in the file and its building. Rows split from one
	/// source row share the row number, so the number alone is not enough.
	/// </summary>
	internal static (int Row, int Building) RowKey(ImportRow row) => (row.RowNumber, row.SplitIndex);
}
