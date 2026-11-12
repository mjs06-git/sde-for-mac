using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.Data;

namespace Sde.App.ViewModels.Sheets;

/// <summary>
/// New Tag (from a Tags menu): a name and one of the six colours. Closes with the new tag (or the existing one with
/// that name), or null when cancelled.
/// </summary>
public sealed partial class TagSheetViewModel : SheetViewModel
{
	private readonly StructuresViewModel _list;

	public TagSheetViewModel(StructuresViewModel list, string title, int structures) : base(title)
	{
		_list = list;
		Intro = structures == 0 ? null : $"It goes on {structures:N0} structure{(structures == 1 ? "" : "s")}.";
	}

	public override SheetSize Size => SheetSize.Small;

	/// <summary>"It goes on 3 structures.", or null when nothing is checked or selected.</summary>
	public string Intro { get; }

	public bool HasIntro => Intro != null;

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(SaveCommand))]
	public partial string Name { get; set; }

	partial void OnNameChanged(string value) => Problem = null;

	public List<TagColourChoice> Colours { get; } = TagColourChoice.All(TagColours.Default);

	/// <summary>Why the name can't be used, or null.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasProblem))]
	public partial string Problem { get; private set; }

	public bool HasProblem => Problem != null;

	[RelayCommand]
	private void ChooseColour(TagColourChoice choice)
	{
		foreach (TagColourChoice c in Colours)
		{
			c.IsChosen = c == choice;
		}
	}

	[RelayCommand(CanExecute = nameof(HasName))]
	private void Save()
	{
		TagViewModel made = _list.CreateTag(Name, Colours.FirstOrDefault(c => c.IsChosen)?.Colour, out string problem);
		if (made == null)
		{
			Problem = problem;
			return;
		}
		Close(made);
	}

	private bool HasName() => !string.IsNullOrWhiteSpace(Name);
}

/// <summary>One tag in Manage Tags: its colour, its name (renamed as it is typed), how many structures have it.</summary>
public sealed partial class ManageTagRowViewModel : ObservableObject
{
	private readonly ManageTagsSheetViewModel _sheet;
	private bool _loading;

	public ManageTagRowViewModel(ManageTagsSheetViewModel sheet, TagRecord tag, int count)
	{
		_sheet = sheet;
		Id = tag.TagID;
		Count = count;
		_loading = true;
		Name = tag.Name;
		Colour = TagColours.Normalize(tag.Colour) ?? TagColours.Default;
		_loading = false;
		Colours = TagColourChoice.All(Colour);
	}

	public long Id { get; }

	/// <summary>How many structures (not in Recently Deleted) have the tag.</summary>
	public int Count { get; }

	/// <summary>"3 structures".</summary>
	public string CountText => $"{Count:N0} structure{(Count == 1 ? "" : "s")}";

	/// <summary>The name: a valid new name is saved at once; an invalid one shows why and isn't saved.</summary>
	[ObservableProperty]
	public partial string Name { get; set; }

	partial void OnNameChanged(string value)
	{
		if (!_loading)
		{
			Problem = _sheet.Rename(this, value);
		}
	}

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(DotKey))]
	public partial string Colour { get; private set; }

	public string DotKey => TagViewModel.DotKeyFor(Colour);

	public List<TagColourChoice> Colours { get; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasProblem))]
	public partial string Problem { get; private set; }

	public bool HasProblem => Problem != null;

	public string NameLabel => "Name of tag " + Name;

	public string ColourLabel => $"Colour of {Name}: {Colour}";

	public string UpLabel => "Move " + Name + " up";

	public string DownLabel => "Move " + Name + " down";

	public string DeleteLabel => "Delete " + Name;

	[RelayCommand]
	private void ChooseColour(TagColourChoice choice)
	{
		if (choice == null || choice.Colour == Colour)
		{
			return;
		}
		if (_sheet.Recolour(this, choice.Colour))
		{
			Colour = choice.Colour;
			foreach (TagColourChoice c in Colours)
			{
				c.IsChosen = c == choice;
			}
			OnPropertyChanged(nameof(ColourLabel));
		}
	}

	[RelayCommand]
	private void MoveUp() => _sheet.Move(this, -1);

	[RelayCommand]
	private void MoveDown() => _sheet.Move(this, 1);

	[RelayCommand]
	private Task Delete() => _sheet.DeleteAsync(this);
}

/// <summary>
/// Manage Tags: every tag with its colour and how many structures have it; rename, recolour, reorder and delete
/// (asking first, and saying how many structures lose the tag). Changes are saved as they are made.
/// </summary>
public sealed partial class ManageTagsSheetViewModel : SheetViewModel
{
	private readonly AppServices _app;

	public ManageTagsSheetViewModel(AppServices app) : base("Tags")
	{
		_app = app;
		Reload();
	}

	public override SheetSize Size => SheetSize.Medium;

	public ObservableCollection<ManageTagRowViewModel> Rows { get; } = new();

	public bool IsEmpty => Rows.Count == 0;

	/// <summary>True after any change, so the pages refresh when the sheet closes.</summary>
	public bool Changed { get; private set; }

	private void Reload()
	{
		Dictionary<long, int> counts = _app.Store.GetTagCounts();
		Rows.Clear();
		foreach (TagRecord t in _app.Store.GetTags())
		{
			Rows.Add(new ManageTagRowViewModel(this, t, counts.GetValueOrDefault(t.TagID)));
		}
		OnPropertyChanged(nameof(IsEmpty));
	}

	/// <summary>Saves a new name; returns why it can't be used, or null.</summary>
	public string Rename(ManageTagRowViewModel row, string name)
	{
		try
		{
			_app.Store.RenameTag(row.Id, name);
			Changed = true;
			return null;
		}
		catch (ArgumentException ex)
		{
			return ex.Message;
		}
	}

	public bool Recolour(ManageTagRowViewModel row, string colour)
	{
		try
		{
			_app.Store.SetTagColour(row.Id, colour);
			Changed = true;
			return true;
		}
		catch (ArgumentException ex)
		{
			_app.Errors.Show("Couldn't change the colour", ex);
			return false;
		}
	}

	/// <summary>Moves a tag up (-1) or down (+1) in the list, and saves the order.</summary>
	public void Move(ManageTagRowViewModel row, int by)
	{
		int at = Rows.IndexOf(row);
		int to = at + by;
		if (at < 0 || to < 0 || to >= Rows.Count)
		{
			return;
		}
		Rows.Move(at, to);
		_app.Store.ReorderTags(Rows.Select(r => r.Id).ToList());
		Changed = true;
	}

	/// <summary>Deletes a tag after asking (it comes off every structure that has it).</summary>
	public async Task DeleteAsync(ManageTagRowViewModel row)
	{
		string message = row.Count == 0
			? "No structure has this tag."
			: $"{row.Count:N0} structure{(row.Count == 1 ? " loses" : "s lose")} this tag. Their assessments don't change.";
		if (!await _app.Dialogs.ConfirmAsync($"Delete the tag “{row.Name}”?", message, "Delete Tag", destructive: true))
		{
			return;
		}
		_app.Store.DeleteTag(row.Id);
		Changed = true;
		Rows.Remove(row);
		OnPropertyChanged(nameof(IsEmpty));
	}

	[RelayCommand]
	private void Done() => Close(Changed);
}

