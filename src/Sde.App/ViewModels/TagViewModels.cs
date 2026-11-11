using CommunityToolkit.Mvvm.ComponentModel;
using Sde.Data;

namespace Sde.App.ViewModels;

/// <summary>
/// A tag as the app shows it: a small neutral chip with a colour dot and the tag's name. The dot carries the colour
/// (one of the TagColour tokens, light and dark); the name is ordinary text.
/// </summary>
public sealed class TagViewModel
{
	public TagViewModel(TagRecord tag)
	{
		Id = tag.TagID;
		Name = tag.Name;
		Colour = TagColours.Normalize(tag.Colour) ?? TagColours.Default;
	}

	public long Id { get; }

	public string Name { get; }

	/// <summary>One of <see cref="TagColours.All"/>.</summary>
	public string Colour { get; }

	/// <summary>The brush resource for the dot ("TagBlueDot" and so on, in Styles/Tokens.axaml).</summary>
	public string DotKey => DotKeyFor(Colour);

	/// <summary>The brush resource for a tag colour's dot.</summary>
	public static string DotKeyFor(string colour) => "Tag" + (TagColours.Normalize(colour) ?? TagColours.Default) + "Dot";

	public override string ToString() => Name;
}

/// <summary>One of the six tag colours, for a colour picker (a row of dots, the chosen one ringed).</summary>
public sealed partial class TagColourChoice(string colour) : ObservableObject
{
	public string Colour { get; } = colour;

	public string DotKey => TagViewModel.DotKeyFor(Colour);

	/// <summary>What VoiceOver reads for the swatch: "Blue".</summary>
	public string Name => Colour;

	[ObservableProperty]
	public partial bool IsChosen { get; set; }

	/// <summary>The six colours, the given one chosen.</summary>
	public static List<TagColourChoice> All(string chosen) =>
		TagColours.All.Select(c => new TagColourChoice(c) { IsChosen = c == (TagColours.Normalize(chosen) ?? TagColours.Default) }).ToList();
}

/// <summary>A tag in the tag filter's editor: ticked when the list is filtered by it.</summary>
public sealed partial class TagFilterChoice(TagViewModel tag, bool selected, Action changed) : ObservableObject
{
	public TagViewModel Tag { get; } = tag;

	[ObservableProperty]
	public partial bool IsSelected { get; set; } = selected;

	partial void OnIsSelectedChanged(bool value) => changed?.Invoke();
}
