using System.Globalization;
using Sde.Core.Assessments;

namespace Sde.App.ViewModels;

/// <summary>
/// Says in words where a percent damaged stands against FEMA's substantial damage line, for the line under the
/// staff gauge in the Structures inspector and the editor's result: "25 points above the 50% line".
/// </summary>
public static class ResultLine
{
	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	/// <summary>The substantial damage line the gauges draw, in percent.</summary>
	public static double Threshold => (double)Determinations.DefaultSubstantialPercent;

	/// <summary>"25 points above the 50% line", "On the 50% line" or "8.5 points below the 50% line".</summary>
	public static string Describe(double percent)
	{
		double gap = Math.Round(percent - Threshold, 1);
		string line = Threshold.ToString("0.#", Us) + "% line";
		if (gap == 0)
		{
			return "On the " + line;
		}
		string points = Math.Abs(gap).ToString("0.#", Us) + (Math.Abs(gap) == 1 ? " point" : " points");
		return gap > 0 ? $"{points} above the {line}" : $"{points} below the {line}";
	}
}
