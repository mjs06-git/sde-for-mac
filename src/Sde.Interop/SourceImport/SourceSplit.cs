using System.Globalization;

namespace Sde.Interop;

/// <summary>One structure made from a source row that covers several buildings.</summary>
internal sealed class SplitPart
{
	/// <summary>1-based position among the row's buildings; 0 when the row is not split.</summary>
	public int Index { get; init; }

	/// <summary>How many buildings the row covers (1 when it is not split).</summary>
	public int Count { get; init; }

	/// <summary>Added to the record ID to tell this building's link apart ("#2", "#Unit B"); empty for the first building of a count.</summary>
	public string RecordSuffix { get; init; } = "";

	/// <summary>Put in the profile's suffix field ("Bldg 2", "Unit B"); null when the row is not split.</summary>
	public string Label { get; init; }

	public bool IsSplit => Index > 0;
}

/// <summary>
/// Works out how many structures one source row becomes, from the profile's split settings. The record ID of the first
/// building of a count stays the plain record ID, so a record that grew from one building to three keeps its first
/// structure; for a list, every item gets "#item" so the order of the list doesn't matter.
/// </summary>
internal static class SourceSplit
{
	/// <summary>The most buildings one row may become.</summary>
	public const int MaxBuildings = 200;

	/// <summary>The structures a row becomes. <paramref name="problem"/> is set when the row can't be split (and then null is returned).</summary>
	public static List<SplitPart> Plan(SourceProfile profile, string cell, List<string> warnings, out string problem)
	{
		problem = null;
		SplitPart single = new SplitPart { Index = 0, Count = 1 };
		if (profile.SplitMode == SourceSplitMode.None || string.IsNullOrWhiteSpace(cell))
		{
			return new List<SplitPart> { single };
		}
		cell = cell.Trim();
		if (profile.SplitMode == SourceSplitMode.CountColumn)
		{
			if (!decimal.TryParse(cell, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal number) || number != decimal.Truncate(number))
			{
				problem = $"The number of buildings '{cell}' is not a whole number";
				return null;
			}
			if (number < 0)
			{
				problem = $"The number of buildings '{cell}' cannot be negative";
				return null;
			}
			if (number > MaxBuildings)
			{
				problem = $"The number of buildings is {number:0}, more than the limit of {MaxBuildings}. Check the file; nothing is imported for this row";
				return null;
			}
			int count = (int)number;
			if (count <= 1)
			{
				return new List<SplitPart> { single };
			}
			string word = string.IsNullOrWhiteSpace(profile.SplitLabel) ? "Bldg" : profile.SplitLabel.Trim();
			List<SplitPart> parts = new();
			for (int i = 1; i <= count; i++)
			{
				parts.Add(new SplitPart { Index = i, Count = count, RecordSuffix = i == 1 ? "" : "#" + i.ToString(CultureInfo.InvariantCulture), Label = word + " " + i.ToString(CultureInfo.InvariantCulture) });
			}
			return parts;
		}

		List<string> items = new();
		foreach (string raw in cell.Split(profile.EffectiveSplitDelimiter))
		{
			string item = raw.Trim();
			if (item.Length == 0)
			{
				continue;
			}
			if (items.Contains(item, StringComparer.OrdinalIgnoreCase))
			{
				warnings.Add($"'{item}' is listed more than once; only one structure is made for it");
				continue;
			}
			items.Add(item);
		}
		if (items.Count == 0)
		{
			return new List<SplitPart> { single };
		}
		if (items.Count > MaxBuildings)
		{
			problem = $"The list has {items.Count} buildings, more than the limit of {MaxBuildings}. Check the file; nothing is imported for this row";
			return null;
		}
		return items.Select((item, i) => new SplitPart { Index = i + 1, Count = items.Count, RecordSuffix = "#" + item, Label = item }).ToList();
	}
}
