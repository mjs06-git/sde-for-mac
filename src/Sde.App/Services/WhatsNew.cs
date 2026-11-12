using System.Reflection;

namespace Sde.App.Services;

/// <summary>One group of the What's New sheet: a heading, its icon and one short line for each change.</summary>
/// <param name="Heading">The card's heading, in sentence case.</param>
/// <param name="Icon">An icon from Styles/Icons.axaml (without the "Icon" prefix).</param>
/// <param name="Items">One line each, plain English, no full stops.</param>
public sealed record WhatsNewGroup(string Heading, string Icon, IReadOnlyList<string> Items);

/// <summary>
/// What changed in this version, for Help, then What's New in SDE (and Settings, About). This is the one place it is
/// written: the release step updates <see cref="Version"/> and <see cref="Groups"/> (docs/RELEASING.md). Keep it short,
/// a few groups of one line each, in the house style: plain English, short, no em dashes.
/// </summary>
public static class WhatsNew
{
	/// <summary>The version these notes are for, as the heading shows it ("What's new in SDE 3.5").</summary>
	public const string Version = "3.5";

	/// <summary>The sheet's large heading.</summary>
	public const string Heading = "What's new in SDE " + Version;

	public static IReadOnlyList<WhatsNewGroup> Groups { get; } = new[]
	{
		new WhatsNewGroup("A new look", "Appearance", new[]
		{
			"A navy frame, bright pages and larger type",
			"Light and dark, or match your Mac",
			"Appearance settings, with a Compact size",
		}),
		new WhatsNewGroup("Your records", "Report", new[]
		{
			"Summary charts for the event",
			"A Case File for each structure",
			"Agenda, with calendar export",
			"Documents kept with each structure",
			"Compare two assessments side by side",
		}),
		new WhatsNewGroup("Finding and tidying", "Filter", new[]
		{
			"Tags to group structures your way",
			"Merge duplicate structures",
			"Pinned and recent structures",
			"Export This List, just as it is shown",
			"Photo Quick Look",
		}),
		new WhatsNewGroup("Map, field and imports", "Map", new[]
		{
			"Map clusters, a heat map and the water surface",
			"Phone form steps for damage, cost adjustments, status and re-inspection",
			"Several buildings from one imported record",
			"Drop files anywhere in the window to import them",
		}),
	};
}

/// <summary>The running app's name and version, as Settings, About shows it and the update check sends it.</summary>
public static class AppVersion
{
	/// <summary>The app's name in a User-Agent.</summary>
	public const string Name = "SDE-for-Mac";

	/// <summary>The running version, such as "3.5.0" (the project's Version, without any build suffix).</summary>
	public static string Running { get; } =
		typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.1";
}
