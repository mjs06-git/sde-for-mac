using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.App.ViewModels.Editor;
using Sde.Core.Records;
using Sde.Data;

namespace Sde.App.ViewModels.Sheets;

/// <summary>One of the two structures in the merge sheet: its address and a few facts to tell them apart.</summary>
public sealed class MergeSideViewModel
{
	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	public MergeSideViewModel(SdeStore store, Guid propertyId)
	{
		PropertyId = propertyId;
		PropertyRecord p = store.GetProperty(propertyId) ?? throw new ArgumentException("That structure is no longer in the database.");
		Address = AssessmentEditorViewModel.Address(p, store.Reference, "(no address)");
		string owner = string.Join(" ", new[] { p.OwnerFirstName, p.OwnerLastName }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
		Owner = owner.Length == 0 ? "No owner recorded" : owner;
		Parcel = string.IsNullOrWhiteSpace(p.ParcelNumber) ? "No parcel number" : "Parcel " + p.ParcelNumber.Trim();
		List<StructureSummary> assessments = store.ListStructures().Where(s => s.PropertyID == propertyId && s.AssessmentID != null).ToList();
		StructureSummary latest = assessments.OrderByDescending(s => s.DateofInspection).ThenByDescending(s => s.LastUpdate).FirstOrDefault();
		string count = assessments.Count == 1 ? "1 assessment" : $"{assessments.Count:N0} assessments";
		Assessments = latest?.DateofInspection is DateTime on ? $"{count}, latest {on.ToString("MMM d, yyyy", Us)}" : count;
	}

	public Guid PropertyId { get; }

	public string Address { get; }

	public string Owner { get; }

	public string Parcel { get; }

	/// <summary>"2 assessments, latest Sep 20, 2026".</summary>
	public string Assessments { get; }
}

/// <summary>
/// Review, then Merge… on a duplicate finding: the two structures side by side with a choice of which to keep, and what
/// the merge would do from the store's preview (what moves, blank fields filled in from the other record, where they
/// differ, what stays behind, warnings). When the preview has conflicts the merge can't be done: the reasons show and
/// Merge is off. Merge closes the sheet with the store's MergePropertiesResult; a refusal shows its reasons instead.
/// </summary>
public sealed partial class MergeStructuresSheetViewModel : SheetViewModel
{
	private readonly AppServices _app;

	/// <param name="first">The structure the finding is about (kept unless the person chooses the other).</param>
	/// <param name="second">The duplicate the finding names.</param>
	public MergeStructuresSheetViewModel(AppServices app, Guid first, Guid second)
		: base("Merge duplicate structures")
	{
		_app = app;
		First = new MergeSideViewModel(app.Store, first);
		Second = new MergeSideViewModel(app.Store, second);
		Refresh();
	}

	public override SheetSize Size => SheetSize.Large;

	public MergeSideViewModel First { get; }

	public MergeSideViewModel Second { get; }

	/// <summary>True to keep <see cref="First"/> and merge <see cref="Second"/> into it; false for the other way round.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(KeepSecond), nameof(Keep), nameof(Other))]
	public partial bool KeepFirst { get; set; } = true;

	/// <summary>The second structure's choice (the radio buttons bind one each).</summary>
	public bool KeepSecond
	{
		get => !KeepFirst;
		set => KeepFirst = !value;
	}

	partial void OnKeepFirstChanged(bool value)
	{
		Refusal.Clear();
		Refresh();
	}

	public MergeSideViewModel Keep => KeepFirst ? First : Second;

	public MergeSideViewModel Other => KeepFirst ? Second : First;

	/// <summary>The store's preview for the current choice.</summary>
	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(MergeCommand))]
	public partial MergePreview Preview { get; private set; }

	/// <summary>"2 assessments", "1 document": what moves to the kept structure.</summary>
	public IReadOnlyList<string> Moving => Preview?.Moving.Select(m => $"{m.What}: {m.Count:N0}").ToList() ?? new List<string>();

	/// <summary>"Parcel number: 12-345 (from the other record)".</summary>
	public IReadOnlyList<string> Filled => Preview?.Filled.Select(f => $"{f.Label}: {f.OtherValue}").ToList() ?? new List<string>();

	/// <summary>"Owner: Pat Example stays (the other record has Sam Sample)".</summary>
	public IReadOnlyList<string> Differences => Preview?.Differences.Select(f => $"{f.Label}: {Show(f.KeptValue)} stays. The other record has {Show(f.OtherValue)}.").ToList() ?? new List<string>();

	public IReadOnlyList<string> StayingBehind => Preview?.StayingBehind ?? new List<string>();

	public IReadOnlyList<string> Warnings => Preview?.Warnings ?? new List<string>();

	/// <summary>Why the merge can't be done: the preview's conflicts, or the reasons a merge was refused with.</summary>
	public List<string> Refusal { get; } = new();

	public IReadOnlyList<string> Conflicts => (Preview?.Conflicts ?? new List<string>()).Concat(Refusal).Distinct().ToList();

	public bool HasMoving => Moving.Count > 0;

	public bool HasFilled => Filled.Count > 0;

	public bool HasDifferences => Differences.Count > 0;

	public bool HasStayingBehind => StayingBehind.Count > 0;

	public bool HasWarnings => Warnings.Count > 0;

	public bool HasConflicts => Conflicts.Count > 0;

	/// <summary>"48 Mill Creek Street goes to Recently Deleted once everything has moved."</summary>
	public string Outcome => $"{Other.Address} goes to Recently Deleted once everything has moved to {Keep.Address}.";

	private static string Show(string value) => string.IsNullOrWhiteSpace(value) ? "blank" : value;

	/// <summary>Reads the preview again for the current choice.</summary>
	private void Refresh()
	{
		try
		{
			Preview = _app.Store.PreviewMerge(Keep.PropertyId, Other.PropertyId);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			Preview = null;
			Refusal.Add(ErrorPresenter.Describe(null, ex).Message);
		}
		foreach (string name in new[] { nameof(Moving), nameof(Filled), nameof(Differences), nameof(StayingBehind), nameof(Warnings), nameof(Conflicts),
			nameof(HasMoving), nameof(HasFilled), nameof(HasDifferences), nameof(HasStayingBehind), nameof(HasWarnings), nameof(HasConflicts), nameof(Outcome) })
		{
			OnPropertyChanged(name);
		}
		MergeCommand.NotifyCanExecuteChanged();
	}

	/// <summary>Merges the other structure into the kept one, with the preview the person read; refused, it says why.</summary>
	[RelayCommand(CanExecute = nameof(CanMerge))]
	private void Merge()
	{
		MergePropertiesResult result;
		try
		{
			result = _app.Store.MergeProperties(Keep.PropertyId, Other.PropertyId, Preview);
		}
		catch (MergeRefusedException ex)
		{
			Refusal.Clear();
			Refusal.AddRange(ex.Reasons);
			Refresh();
			return;
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			Refusal.Clear();
			Refusal.Add(ErrorPresenter.Describe(null, ex).Message);
			Refresh();
			return;
		}
		Close(result);
	}

	private bool CanMerge() => Preview is { CanMerge: true };
}
