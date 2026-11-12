using Sde.Core.Quality;
using Sde.Data;

namespace Sde.App.Services;

/// <summary>
/// Runs the quality checks (<see cref="QaRules"/>) over assessments in the database, for the Review page and the
/// question asked before an SDE export or a submission folder. The flood map zone and high-water mark checks compare with
/// data the database doesn't keep (an online look-up, a marks file), so they don't run here; <see cref="NotChecked"/>
/// says so, for the page to show with a "Check Online" action. Nothing here goes online.
/// </summary>
public static class QualityChecks
{
	/// <summary>
	/// The findings for these assessments (null: every active assessment of an active structure), with any that were
	/// set aside marked as dismissed. Structure checks, such as duplicates, compare only the structures among them.
	/// </summary>
	public static List<QaFinding> Run(SdeStore store, IReadOnlyCollection<Guid> assessmentIds) =>
		assessmentIds is { Count: 0 } ? new List<QaFinding>() : QaRules.Check(store.LoadQaInput(assessmentIds));

	/// <summary>The checks that could not run for these assessments for lack of stored data, each with a plain-English reason.</summary>
	public static List<QaNotChecked> NotChecked(SdeStore store, IReadOnlyCollection<Guid> assessmentIds) =>
		assessmentIds is { Count: 0 } ? new List<QaNotChecked>() : QaRules.NotChecked(store.LoadQaInput(assessmentIds));

	/// <summary>The open (not set aside) findings of error severity among these assessments.</summary>
	public static List<QaFinding> OpenErrors(SdeStore store, IReadOnlyCollection<Guid> assessmentIds) =>
		Run(store, assessmentIds).Where(f => f.Severity == QaSeverity.Error && !f.IsDismissed).ToList();
}
