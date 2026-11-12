using CommunityToolkit.Mvvm.ComponentModel;
using Sde.Core.Assessments;
using Sde.Core.Events;
using Sde.Core.Records;

namespace Sde.App.ViewModels;

/// <summary>
/// One bar of the damage distribution: the assessments whose percent damaged is from <see cref="From"/> up to (not
/// including) <see cref="To"/>; the last bar also holds 100 and anything over it. <see cref="ColourKey"/> is the gauge
/// colour it is drawn in: red at or above the substantial damage line, amber in the near-threshold band, blue below.
/// </summary>
public readonly record struct HistogramBin(double From, double To, int Count, string ColourKey);

/// <summary>How many assessments had been completed by the end of <see cref="Date"/>, counting from the first.</summary>
public readonly record struct ProgressPoint(DateTime Date, int Total);

/// <summary>The Summary's two charts: the damage distribution and progress over time.</summary>
public sealed partial class DashboardViewModel
{
	/// <summary>How many points wide each bar of the distribution is (ten bars from 0 to 100).</summary>
	public const int BinWidth = 10;

	/// <summary>The damage distribution: ten bars of 10 points each across the assessments with a result.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasDistribution))]
	public partial IReadOnlyList<HistogramBin> DamageBins { get; private set; }

	/// <summary>Whether any assessment has a percent damaged to show in the distribution.</summary>
	public bool HasDistribution => DamageBins != null && DamageBins.Any(b => b.Count > 0);

	/// <summary>"38 with a result" over the distribution.</summary>
	[ObservableProperty]
	public partial string DistributionNote { get; private set; }

	/// <summary>Assessments completed by inspection date, one point a day from the first inspection to the last.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasProgressSeries))]
	public partial IReadOnlyList<ProgressPoint> ProgressSeries { get; private set; }

	/// <summary>Whether any completed assessment has an inspection date.</summary>
	public bool HasProgressSeries => ProgressSeries != null && ProgressSeries.Count > 0;

	/// <summary>"40 completed" over the progress chart.</summary>
	[ObservableProperty]
	public partial string ProgressNote { get; private set; }

	/// <summary>The substantial damage line in percent, drawn through the distribution.</summary>
	[ObservableProperty]
	public partial double SubstantialLine { get; private set; } = (double)Determinations.DefaultSubstantialPercent;

	/// <summary>The share of structures with an assessment, for the headline band ("100%").</summary>
	[ObservableProperty]
	public partial string AssessedShareText { get; private set; }

	/// <summary>Rebuilds the charts from the same assessments, and with the same lines, as the rest of the page.</summary>
	private void ShowCharts(EventSummary s)
	{
		List<AssessmentRecord> records = _files.Where(f => f?.Assessment != null).Select(f => f.Assessment).ToList();
		DamageBins = BuildBins(records, s.SubstantialPercent, s.ClosePercent);
		int withResult = DamageBins.Sum(b => b.Count);
		DistributionNote = withResult == 0 ? "" : $"{withResult.ToString("N0", Us)} with a result";
		ProgressSeries = BuildProgress(records, s.SubstantialPercent, s.ClosePercent);
		int completed = ProgressSeries.Count == 0 ? 0 : ProgressSeries[^1].Total;
		ProgressNote = completed == 0 ? "" : $"{completed.ToString("N0", Us)} completed";
		SubstantialLine = (double)s.SubstantialPercent;
		AssessedShareText = s.TotalStructures == 0 ? "0%" : (100.0 * s.AssessedStructures / s.TotalStructures).ToString("0", Us) + "%";
	}

	/// <summary>
	/// Counts the assessments with a result (substantial, near the line or not substantial, and a percent damaged) into
	/// ten bars of 10 points: 0 to 9.99 in the first, 50 in the sixth, and 90 to 100 (and anything over 100) in the last.
	/// Incomplete and undetermined assessments have no percentage to place, so they are left out.
	/// </summary>
	public static IReadOnlyList<HistogramBin> BuildBins(IEnumerable<AssessmentRecord> assessments,
		decimal substantialPercent = Determinations.DefaultSubstantialPercent, decimal closePercent = Determinations.DefaultClosePercent)
	{
		int bins = 100 / BinWidth;
		int[] counts = new int[bins];
		foreach (AssessmentRecord a in assessments ?? Enumerable.Empty<AssessmentRecord>())
		{
			if (a?.PercentDamage is not decimal percent || !IsPlaced(a, substantialPercent, closePercent))
			{
				continue;
			}
			int index = (int)Math.Floor(percent / BinWidth);
			counts[Math.Clamp(index, 0, bins - 1)]++;
		}
		List<HistogramBin> result = new();
		for (int i = 0; i < bins; i++)
		{
			double from = i * BinWidth;
			string colour = from >= (double)substantialPercent ? "GaugeDanger"
				: from >= (double)closePercent ? "GaugeNear"
				: "GaugeWater";
			result.Add(new HistogramBin(from, from + BinWidth, counts[i], colour));
		}
		return result;
	}

	/// <summary>
	/// The running total of completed assessments (any with a determination, undetermined included) by inspection date,
	/// one point for every day from the first inspection to the last, so the line climbs at the pace the work was done.
	/// Assessments with no inspection date, or still incomplete, are not counted.
	/// </summary>
	public static IReadOnlyList<ProgressPoint> BuildProgress(IEnumerable<AssessmentRecord> assessments,
		decimal substantialPercent = Determinations.DefaultSubstantialPercent, decimal closePercent = Determinations.DefaultClosePercent)
	{
		Dictionary<DateTime, int> perDay = (assessments ?? Enumerable.Empty<AssessmentRecord>())
			.Where(a => a?.DateofInspection != null && Determinations.Classify(a, substantialPercent, closePercent) != DeterminationClass.Incomplete)
			.GroupBy(a => a.DateofInspection.Value.Date)
			.ToDictionary(g => g.Key, g => g.Count());
		List<ProgressPoint> points = new();
		if (perDay.Count == 0)
		{
			return points;
		}
		int total = 0;
		for (DateTime day = perDay.Keys.Min(); day <= perDay.Keys.Max(); day = day.AddDays(1))
		{
			total += perDay.TryGetValue(day, out int n) ? n : 0;
			points.Add(new ProgressPoint(day, total));
		}
		return points;
	}

	private static bool IsPlaced(AssessmentRecord a, decimal substantialPercent, decimal closePercent) =>
		Determinations.Classify(a, substantialPercent, closePercent) is DeterminationClass.Substantial or DeterminationClass.Close or DeterminationClass.NotSubstantial;
}
