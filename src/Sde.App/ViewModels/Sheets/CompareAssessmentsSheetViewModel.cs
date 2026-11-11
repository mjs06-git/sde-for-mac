using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Sde.App.Services;
using Sde.App.ViewModels.Editor;
using Sde.Core;
using Sde.Core.Assessments;
using Sde.Core.Records;

namespace Sde.App.ViewModels.Sheets;

/// <summary>An assessment to compare, as the pickers list it ("Sep 21, 2026 · 75.0%").</summary>
public sealed record CompareChoice(Guid Id, string Label)
{
	public override string ToString() => Label;
}

/// <summary>One side's result at the top of the comparison: the figure, the determination and the gauge.</summary>
public sealed class CompareSideViewModel
{
	public CompareSideViewModel(AssessmentFile file, string label)
	{
		File = file;
		Label = label;
		State = CaseFileViewModel.StateOf(file.Assessment);
	}

	public AssessmentFile File { get; }

	/// <summary>"Inspected Sep 21, 2026".</summary>
	public string Label { get; }

	public ResultState State { get; }

	public bool HasPercent => State is not (ResultState.Incomplete or ResultState.Undetermined);

	public decimal? Percent => HasPercent ? File.Assessment.PercentDamage : null;

	public string PercentText => HasPercent ? File.Assessment.PercentDamage?.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "";

	public double Value => HasPercent ? (double)Math.Clamp(File.Assessment.PercentDamage ?? 0m, 0m, 100m) : 0;

	public string Verdict => File.Assessment.DamageMessage ?? "Data Entry Incomplete";

	public bool IsSubstantial => State == ResultState.Substantial;

	public string LineText => HasPercent ? ResultLine.Describe(Value) : "";
}

/// <summary>One field of the comparison: its label as the editor shows it, and both values.</summary>
public sealed class CompareRowViewModel
{
	public CompareRowViewModel(string label, string left, string right, bool differs, double? leftPercent = null, double? rightPercent = null)
	{
		Label = label;
		Left = string.IsNullOrEmpty(left) ? "–" : left;
		Right = string.IsNullOrEmpty(right) ? "–" : right;
		Differs = differs;
		LeftPercent = leftPercent ?? 0;
		RightPercent = rightPercent ?? 0;
		HasGauges = leftPercent != null || rightPercent != null;
	}

	public string Label { get; }

	public string Left { get; }

	public string Right { get; }

	public bool Differs { get; }

	/// <summary>An element's percent damaged: shown with a mini gauge on each side.</summary>
	public bool HasGauges { get; }

	public double LeftPercent { get; }

	public double RightPercent { get; }

	public string AccessibleName => $"{Label}: {Left}, then {Right}" + (Differs ? "" : ", unchanged");
}

/// <summary>The fields of one of the editor's steps, in the comparison.</summary>
public sealed class CompareGroupViewModel(string title, IReadOnlyList<CompareRowViewModel> rows)
{
	public string Title { get; } = title;

	public IReadOnlyList<CompareRowViewModel> Rows { get; } = rows;
}

/// <summary>
/// Compare Assessments: two assessments of one structure side by side, read only. Their results at the top with the
/// difference in points, then every field that differs, grouped by the editor's eight steps and labelled and formatted
/// as the editor does (from <see cref="FieldCatalog"/>), with each element's percent damaged on a mini gauge. Fields
/// with the same value are hidden unless Show unchanged is on.
/// </summary>
public sealed partial class CompareAssessmentsSheetViewModel : SheetViewModel
{
	private readonly AppServices _app;
	private readonly Dictionary<Guid, AssessmentFile> _files = new();
	private bool _building;

	/// <param name="first">One of the two to compare (the older of the two is shown on the left).</param>
	public CompareAssessmentsSheetViewModel(AppServices app, Guid propertyId, Guid first, Guid second) : base("Compare assessments")
	{
		_app = app;
		Choices = app.Store.GetAssessmentsForProperty(propertyId)
			.Select(a => new CompareChoice(a.AssessmentID, Describe(a)))
			.ToList();
		List<CompareChoice> chosen = Choices.Where(c => c.Id == first || c.Id == second).Reverse().ToList();
		_building = true;
		Left = chosen.ElementAtOrDefault(0) ?? Choices.ElementAtOrDefault(1);
		Right = chosen.ElementAtOrDefault(1) ?? Choices.ElementAtOrDefault(0);
		_building = false;
		Rebuild();
	}

	/// <summary>The defaults from the editor: this assessment and the one inspected before it (or after it, for the oldest).</summary>
	public static (Guid First, Guid Second)? Defaults(IReadOnlyList<Guid> newestFirst, Guid current)
	{
		if (newestFirst.Count < 2)
		{
			return null;
		}
		int at = Math.Max(0, newestFirst.ToList().IndexOf(current));
		Guid other = at + 1 < newestFirst.Count ? newestFirst[at + 1] : newestFirst[at - 1];
		return (newestFirst[at], other);
	}

	public override SheetSize Size => SheetSize.Large;

	/// <summary>The structure's assessments, newest first.</summary>
	public IReadOnlyList<CompareChoice> Choices { get; }

	[ObservableProperty]
	public partial CompareChoice Left { get; set; }

	[ObservableProperty]
	public partial CompareChoice Right { get; set; }

	partial void OnLeftChanged(CompareChoice value) => Rebuild();

	partial void OnRightChanged(CompareChoice value) => Rebuild();

	[ObservableProperty]
	public partial bool ShowUnchanged { get; set; }

	partial void OnShowUnchangedChanged(bool value) => Rebuild();

	[ObservableProperty]
	public partial CompareSideViewModel LeftSide { get; private set; }

	[ObservableProperty]
	public partial CompareSideViewModel RightSide { get; private set; }

	/// <summary>"+7.5" (the right's percent less the left's), or "–" when either has no percentage.</summary>
	[ObservableProperty]
	public partial string DifferenceText { get; private set; }

	/// <summary>"points higher", "points lower" or "no change".</summary>
	[ObservableProperty]
	public partial string DifferenceNote { get; private set; }

	/// <summary>"12 fields differ".</summary>
	[ObservableProperty]
	public partial string CountText { get; private set; }

	[ObservableProperty]
	public partial bool HasRows { get; private set; }

	public ObservableCollection<CompareGroupViewModel> Groups { get; } = new();

	/// <summary>How many fields differ (every step together).</summary>
	public int DifferentCount { get; private set; }

	private static string Describe(AssessmentRecord a) =>
		(a.DateofInspection?.ToString("MMM d, yyyy", CultureInfo.GetCultureInfo("en-US")) ?? "No date") + (a.PercentDamage is decimal p ? $" · {p:0.0}%" : "");

	private AssessmentFile Load(Guid id)
	{
		if (!_files.TryGetValue(id, out AssessmentFile file))
		{
			file = _app.Store.LoadAssessment(id);
			_files[id] = file;
		}
		return file;
	}

	private void Rebuild()
	{
		if (_building || Left == null || Right == null)
		{
			return;
		}
		AssessmentFile a = Load(Left.Id);
		AssessmentFile b = Load(Right.Id);
		if (a == null || b == null)
		{
			return;
		}
		LeftSide = new CompareSideViewModel(a, Inspected(a));
		RightSide = new CompareSideViewModel(b, Inspected(b));
		if (LeftSide.Percent is decimal lp && RightSide.Percent is decimal rp)
		{
			decimal gap = Math.Round(rp - lp, 1);
			DifferenceText = gap == 0 ? "0" : (gap > 0 ? "+" : "−") + Math.Abs(gap).ToString("0.0", CultureInfo.InvariantCulture);
			DifferenceNote = gap == 0 ? "no change" : gap > 0 ? "points higher" : "points lower";
		}
		else
		{
			DifferenceText = "–";
			DifferenceNote = "no percentage to compare";
		}

		ReferenceData reference = _app.Store.Reference;
		List<CompareGroupViewModel> groups = new();
		int differing = 0;
		for (int step = 1; step <= FieldCatalog.Steps.Count; step++)
		{
			List<CompareRowViewModel> rows = new();
			foreach (FieldInfo f in FieldCatalog.All.Where(f => f.Step == step && (f.AppliesTo(a) || f.AppliesTo(b))))
			{
				object left = FieldCatalog.GetValue(a, f);
				object right = FieldCatalog.GetValue(b, f);
				bool differs = FieldCatalog.Canonical(left) != FieldCatalog.Canonical(right);
				rows.Add(new CompareRowViewModel(f.Label, FieldCatalog.Display(f, left, reference), FieldCatalog.Display(f, right, reference), differs));
			}
			if (step == FieldCatalog.DamageStep)
			{
				rows.AddRange(Elements(a, b));
			}
			differing += rows.Count(r => r.Differs);
			List<CompareRowViewModel> shown = rows.Where(r => ShowUnchanged || r.Differs).ToList();
			if (shown.Count > 0)
			{
				groups.Add(new CompareGroupViewModel(FieldCatalog.Steps[step - 1], shown));
			}
		}
		DifferentCount = differing;
		CountText = differing == 0 ? "No fields differ" : differing == 1 ? "1 field differs" : $"{differing} fields differ";
		Groups.Clear();
		foreach (CompareGroupViewModel g in groups)
		{
			Groups.Add(g);
		}
		HasRows = Groups.Count > 0;
	}

	/// <summary>Each element's percent damaged, matched by name, in the order of the newer assessment's table.</summary>
	private static IEnumerable<CompareRowViewModel> Elements(AssessmentFile a, AssessmentFile b)
	{
		Dictionary<string, decimal> left = a.ElementPercentages.GroupBy(e => e.ElementName ?? "").ToDictionary(g => g.Key, g => g.First().UserDamage);
		Dictionary<string, decimal> right = b.ElementPercentages.GroupBy(e => e.ElementName ?? "").ToDictionary(g => g.Key, g => g.First().UserDamage);
		IEnumerable<string> names = b.ElementPercentages.OrderBy(e => e.CalcID).Select(e => e.ElementName ?? "")
			.Concat(a.ElementPercentages.OrderBy(e => e.CalcID).Select(e => e.ElementName ?? "")).Distinct();
		foreach (string name in names)
		{
			decimal? l = left.TryGetValue(name, out decimal lv) ? lv : null;
			decimal? r = right.TryGetValue(name, out decimal rv) ? rv : null;
			yield return new CompareRowViewModel(name + " damage", Percent(l), Percent(r), l != r, (double?)l, (double?)r);
		}
	}

	private static string Percent(decimal? p) => p?.ToString("0.##", CultureInfo.InvariantCulture) + (p == null ? "" : "%");

	private static string Inspected(AssessmentFile f) => f.Assessment.DateofInspection is DateTime d
		? "Inspected " + d.ToString("MMM d, yyyy", CultureInfo.GetCultureInfo("en-US"))
		: "No inspection date";
}
