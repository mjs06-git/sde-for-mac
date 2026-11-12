using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.App.ViewModels.Sheets;
using Sde.Data;

namespace Sde.App.ViewModels;

/// <summary>
/// Tags on the Structures page: the Tags column, the tag filter, the inspector's tags with Add Tag, the Tags menus
/// (Change ▾ and the row menu) and Manage Tags. Tags are the person's own grouping: they write no edit history and are
/// never exported in FEMA's formats (SdeStore.Tags).
/// </summary>
public sealed partial class StructuresViewModel
{
	/// <summary>Every tag, in list order.</summary>
	public ObservableCollection<TagViewModel> AllTags { get; } = new();

	public bool HasTags => AllTags.Count > 0;

	/// <summary>Reads the tags and which structures have them; the rows are given theirs.</summary>
	private ILookup<Guid, TagViewModel> ReadTags()
	{
		bool hadTags = AllTags.Count > 0;
		List<TagViewModel> tags;
		ILookup<Guid, TagRecord> byStructure;
		try
		{
			tags = _app.Store.GetTags().Select(t => new TagViewModel(t)).ToList();
			byStructure = _app.Store.GetTagsForMany(Everything.Select(s => s.PropertyID));
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			AppLog.Write("Couldn't read the tags", ex);
			tags = new List<TagViewModel>();
			byStructure = Array.Empty<(Guid, TagRecord)>().ToLookup(p => p.Item1, p => p.Item2);
		}
		AllTags.Clear();
		foreach (TagViewModel t in tags)
		{
			AllTags.Add(t);
		}
		Dictionary<long, TagViewModel> byId = tags.ToDictionary(t => t.Id);
		OnPropertyChanged(nameof(HasTags));
		if (!hadTags && tags.Count > 0 && _tagsJustCreated)
		{
			// The first tag: the Tags column shows, so the person sees where tags go.
			_tagsJustCreated = false;
			Columns.Single(c => c.Key == "Tags").IsVisible = true;
		}
		return byStructure.SelectMany(g => g.Select(t => (g.Key, Tag: byId.GetValueOrDefault(t.TagID))))
			.Where(p => p.Tag != null).ToLookup(p => p.Key, p => p.Tag);
	}

	/// <summary>Set when this page made the database's first tag, so the Tags column comes on.</summary>
	private bool _tagsJustCreated;

	/// <summary>Whether an optional column shows when nothing has been chosen: the Tags column shows once any tag exists.</summary>
	private bool DefaultVisible(string key)
	{
		if (key == "Tags")
		{
			try
			{
				return _app.Store.GetTags().Count > 0;
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				AppLog.Write("Couldn't read the tags", ex);
				return false;
			}
		}
		return OptionalColumns.First(o => o.Key == key).Visible;
	}

	/// <summary>The tag filter: structures with any (or all) of the chosen tags.</summary>
	private IEnumerable<StructureRowViewModel> FilterByTags(IEnumerable<StructureRowViewModel> rows)
	{
		IReadOnlyList<long> wanted = EffectiveTagFilter;
		if (wanted.Count == 0)
		{
			return rows;
		}
		return TagsMatchAll
			? rows.Where(r => wanted.All(id => r.Tags.Any(t => t.Id == id)))
			: rows.Where(r => r.Tags.Any(t => wanted.Contains(t.Id)));
	}

	/// <summary>The sidebar's tag row or the palette's "Tag: …": the list filtered to structures with that tag.</summary>
	public void ShowTag(long tagId)
	{
		_restoring = true;
		try
		{
			TagsMatchAll = false;
			TagFilter = new[] { tagId };
		}
		finally
		{
			_restoring = false;
		}
		Changed();
	}

	/// <summary>True while the list is filtered by exactly this one tag (its sidebar row is chosen).</summary>
	public bool IsShowingOnlyTag(long tagId) => AppliedView == null && EffectiveTagFilter.Count == 1 && EffectiveTagFilter[0] == tagId;

	/// <summary>After the list is read again, the same structure stays selected (the inspector keeps showing it).</summary>
	private void ReselectAfterReload(StructureRowViewModel previous)
	{
		if (previous == null)
		{
			return;
		}
		Selected = Rows.FirstOrDefault(r => r.AssessmentID != null && r.AssessmentID == previous.AssessmentID)
			?? Rows.FirstOrDefault(r => r.AssessmentID == null && r.Summary.PropertyID == previous.Summary.PropertyID);
	}

	protected override void OnPropertyChanged(PropertyChangedEventArgs e)
	{
		base.OnPropertyChanged(e);
		if (e.PropertyName is nameof(CheckedCount) or nameof(Selected) or nameof(HasTags))
		{
			base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(TagMenuItems)));
		}
		if (e.PropertyName is nameof(Selected) or nameof(HasTags))
		{
			base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(SelectedTags)));
			base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(HasSelectedTags)));
			RefreshTagSuggestions();
		}
	}

	// ───────────── Tagging the checked (or the selected) structures ─────────────

	/// <summary>The structures a Tags menu acts on: the checked ones, or the selected one when none is checked.</summary>
	public IReadOnlyList<Guid> TagTargets()
	{
		List<Guid> ids = _all.Where(r => r.IsChecked).Select(r => r.Summary.PropertyID).Distinct().ToList();
		if (ids.Count == 0 && Selected != null)
		{
			ids.Add(Selected.Summary.PropertyID);
		}
		return ids;
	}

	/// <summary>Whether every one of the structures has the tag (its menu item is ticked; choosing it takes it off).</summary>
	public bool AllHaveTag(long tagId, IReadOnlyList<Guid> propertyIds)
	{
		if (propertyIds.Count == 0)
		{
			return false;
		}
		HashSet<Guid> wanted = propertyIds.ToHashSet();
		HashSet<Guid> tagged = _all.Where(r => wanted.Contains(r.Summary.PropertyID) && r.Tags.Any(t => t.Id == tagId)).Select(r => r.Summary.PropertyID).ToHashSet();
		return wanted.All(tagged.Contains);
	}

	/// <summary>
	/// Change ▾ › Tags and the row menu's Tags: each tag (ticked when all the checked structures, or the selected one,
	/// have it), then New Tag and Manage Tags. A fresh list each time, as a menu item can be in only one menu.
	/// </summary>
	public IReadOnlyList<Control> TagMenuItems
	{
		get
		{
			IReadOnlyList<Guid> targets = TagTargets();
			List<Control> items = new();
			foreach (TagViewModel tag in AllTags)
			{
				Ellipse dot = new Ellipse { Classes = { "dot" } };
				ThemeBrush.SetFill(dot, tag.DotKey);
				items.Add(new MenuItem
				{
					Header = tag.Name,
					Icon = dot,
					ToggleType = MenuItemToggleType.CheckBox,
					IsChecked = AllHaveTag(tag.Id, targets),
					Command = ToggleTagCommand,
					CommandParameter = tag,
				});
			}
			if (items.Count > 0)
			{
				items.Add(new Separator());
			}
			items.Add(new MenuItem { Header = "New Tag…", Command = NewTagCommand });
			items.Add(new MenuItem { Header = "Manage Tags…", Command = ManageTagsCommand });
			return items;
		}
	}

	/// <summary>A tag in a Tags menu: puts it on all the target structures, or takes it off when they all have it.</summary>
	[RelayCommand]
	private void ToggleTag(TagViewModel tag)
	{
		IReadOnlyList<Guid> targets = TagTargets();
		if (tag == null)
		{
			return;
		}
		if (targets.Count == 0)
		{
			_app.Dialogs.Notify("Nothing to tag", "Check some structures, or select one, first.");
			return;
		}
		if (AllHaveTag(tag.Id, targets))
		{
			RemoveTag(tag.Id, targets);
		}
		else
		{
			AddTag(tag.Id, targets);
		}
	}

	/// <summary>Puts a tag on structures (no edit history: a tag isn't part of the record).</summary>
	public void AddTag(long tagId, IReadOnlyList<Guid> propertyIds)
	{
		try
		{
			_app.Store.AddTag(tagId, propertyIds);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't add the tag", ex);
		}
		Reload();
	}

	/// <summary>Takes a tag off structures.</summary>
	public void RemoveTag(long tagId, IReadOnlyList<Guid> propertyIds)
	{
		try
		{
			_app.Store.RemoveTag(tagId, propertyIds);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			_app.Errors.Show("Couldn't remove the tag", ex);
		}
		Reload();
	}

	/// <summary>Makes a tag, or finds the one with that name (ignoring case). Null when the name can't be used.</summary>
	public TagViewModel CreateTag(string name, string colour, out string problem)
	{
		problem = null;
		string clean = string.Join(" ", (name ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
		TagViewModel existing = AllTags.FirstOrDefault(t => string.Equals(t.Name, clean, StringComparison.OrdinalIgnoreCase));
		if (existing != null)
		{
			return existing;
		}
		try
		{
			bool first = AllTags.Count == 0;
			TagRecord made = _app.Store.CreateTag(clean, colour ?? TagColours.Default);
			_tagsJustCreated = first;
			return new TagViewModel(made);
		}
		catch (ArgumentException ex)
		{
			problem = ex.Message;
			return null;
		}
	}

	/// <summary>A Tags menu, then New Tag: asks for a name and colour, makes the tag and puts it on the target structures.</summary>
	[RelayCommand]
	private async Task NewTag()
	{
		IReadOnlyList<Guid> targets = TagTargets();
		if (await _app.Sheets.ShowAsync(new TagSheetViewModel(this, "New Tag", targets.Count)) is TagViewModel made)
		{
			if (targets.Count > 0)
			{
				AddTag(made.Id, targets);
			}
			else
			{
				Reload();
			}
		}
	}

	/// <summary>A Tags menu, then Manage Tags: rename, recolour, reorder and delete tags.</summary>
	[RelayCommand]
	private async Task ManageTags()
	{
		await _app.Sheets.ShowAsync(new ManageTagsSheetViewModel(_app));
		Reload();
	}

	// ───────────── The inspector's tags ─────────────

	/// <summary>The selected structure's tags, in list order.</summary>
	public IReadOnlyList<TagViewModel> SelectedTags => Selected?.Tags ?? Array.Empty<TagViewModel>();

	public bool HasSelectedTags => SelectedTags.Count > 0;

	/// <summary>What is typed in Add Tag: it narrows the tags offered, or names a new one.</summary>
	[ObservableProperty]
	public partial string TagQuery { get; set; }

	partial void OnTagQueryChanged(string value)
	{
		TagProblem = null;
		RefreshTagSuggestions();
	}

	/// <summary>The tags Add Tag offers: those the selected structure hasn't got that match what is typed.</summary>
	public ObservableCollection<TagViewModel> TagSuggestions { get; } = new();

	public bool HasTagSuggestions => TagSuggestions.Count > 0;

	/// <summary>True when what is typed names no tag yet, so Add Tag offers to make it.</summary>
	public bool CanCreateTag
	{
		get
		{
			string q = (TagQuery ?? "").Trim();
			return q.Length > 0 && !AllTags.Any(t => string.Equals(t.Name, q, StringComparison.OrdinalIgnoreCase));
		}
	}

	/// <summary>"Create “Revisit”".</summary>
	public string CreateTagText => $"Create “{(TagQuery ?? "").Trim()}”";

	/// <summary>The colour a tag made in Add Tag gets.</summary>
	public List<TagColourChoice> NewTagColours { get; } = TagColourChoice.All(TagColours.Default);

	/// <summary>Why a tag couldn't be made ("A tag's name can be at most 40 characters."), or null.</summary>
	[ObservableProperty]
	public partial string TagProblem { get; private set; }

	private void RefreshTagSuggestions()
	{
		string q = (TagQuery ?? "").Trim();
		HashSet<long> has = SelectedTags.Select(t => t.Id).ToHashSet();
		TagSuggestions.Clear();
		foreach (TagViewModel t in AllTags.Where(t => !has.Contains(t.Id) && (q.Length == 0 || t.Name.Contains(q, StringComparison.OrdinalIgnoreCase))))
		{
			TagSuggestions.Add(t);
		}
		OnPropertyChanged(nameof(HasTagSuggestions));
		OnPropertyChanged(nameof(CanCreateTag));
		OnPropertyChanged(nameof(CreateTagText));
	}

	/// <summary>A colour in Add Tag's new-tag row.</summary>
	[RelayCommand]
	private void ChooseNewTagColour(TagColourChoice choice)
	{
		foreach (TagColourChoice c in NewTagColours)
		{
			c.IsChosen = c == choice;
		}
	}

	/// <summary>Add Tag, then a tag: the selected structure gets it.</summary>
	[RelayCommand]
	private void AddTagToSelected(TagViewModel tag)
	{
		if (Selected == null || tag == null)
		{
			return;
		}
		TagQuery = null;
		AddTag(tag.Id, new[] { Selected.Summary.PropertyID });
	}

	/// <summary>Add Tag, then Create: makes the typed tag in the chosen colour and gives it to the selected structure.</summary>
	[RelayCommand]
	private void CreateTagForSelected()
	{
		if (Selected == null || !CanCreateTag)
		{
			return;
		}
		string colour = NewTagColours.FirstOrDefault(c => c.IsChosen)?.Colour ?? TagColours.Default;
		TagViewModel made = CreateTag(TagQuery, colour, out string problem);
		if (made == null)
		{
			TagProblem = problem;
			return;
		}
		AddTagToSelected(made);
	}

	/// <summary>A tag's × in the inspector: takes it off the selected structure.</summary>
	[RelayCommand]
	private void RemoveTagFromSelected(TagViewModel tag)
	{
		if (Selected != null && tag != null)
		{
			RemoveTag(tag.Id, new[] { Selected.Summary.PropertyID });
		}
	}
}
