using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Services;
using Sde.Data;

namespace Sde.App.ViewModels;

/// <summary>One thing in Recently Deleted.</summary>
public sealed class DeletedItemViewModel(DeletedItemRecord item)
{
	private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

	public DeletedItemRecord Item { get; } = item;

	public string Label => Item.Label;

	public string Kind => Item.Value switch
	{
		DeletedKind.Assessment => "Assessment",
		DeletedKind.Photo => "Photo",
		_ => "Structure",
	};

	/// <summary>The icon beside the item: a house, a clipboard or a photo.</summary>
	public string Icon => Item.Value switch
	{
		DeletedKind.Assessment => "Clipboard",
		DeletedKind.Photo => "Image",
		_ => "Structures",
	};

	public string When => $"Deleted {Item.DeletedOn:MMM d, yyyy h:mm tt}" + (string.IsNullOrEmpty(Item.DeletedBy) ? "" : " by " + Item.DeletedBy);

	/// <summary>"Oct 28, 2026 11:38 AM", for the table.</summary>
	public string DeletedOnText => Item.DeletedOn.ToString("MMM d, yyyy h:mm tt", Us);

	public DateTime DeletedOn => Item.DeletedOn;

	/// <summary>Who deleted it, or a dash when nobody was recorded.</summary>
	public string DeletedBy => string.IsNullOrWhiteSpace(Item.DeletedBy) ? "–" : Item.DeletedBy.Trim();

	/// <summary>Whole days until it is removed for good (0 on its last day).</summary>
	public int DaysLeftCount => Math.Max(0, (int)Math.Ceiling((Item.PurgeAfter - DateTime.Now).TotalDays));

	/// <summary>"30 days", "1 day", for the table.</summary>
	public string DaysLeftText => DaysLeftCount == 1 ? "1 day" : $"{DaysLeftCount:N0} days";

	public string DaysLeft => DaysLeftCount == 1 ? "Removed for good tomorrow" : $"Removed for good in {DaysLeftCount} days";

	/// <summary>The row's second line: "Structure · Deleted Oct 28, 2026 11:38 AM by Reviewer A · Removed for good in 30 days".</summary>
	public string Detail => $"{Kind} · {When} · {DaysLeft}";

	/// <summary>The record's own detail (what the structure or assessment was), for the label's tooltip.</summary>
	public string RecordDetail => string.IsNullOrWhiteSpace(Item.Detail) ? Detail : Item.Detail;
}

/// <summary>
/// Recently Deleted: what was deleted, when and by whom, and how long until it is removed for good, as a table. Restore
/// and Delete Now work on the selected rows (one or several), from the toolbar, the row menu or the Delete key.
/// </summary>
public sealed partial class TrashViewModel(AppServices app) : ViewModelBase
{
	private readonly List<DeletedItemViewModel> _selection = new();

	public ObservableCollection<DeletedItemViewModel> Items { get; } = new();

	public bool IsEmpty => Items.Count == 0;

	public bool HasItems => Items.Count > 0;

	/// <summary>The row the table's selection starts from (the table reports several with <see cref="SetSelection"/>).</summary>
	[ObservableProperty]
	public partial DeletedItemViewModel Selected { get; set; }

	partial void OnSelectedChanged(DeletedItemViewModel value)
	{
		if (_selection.Count <= 1)
		{
			_selection.Clear();
			if (value != null)
			{
				_selection.Add(value);
			}
			SelectionChanged();
		}
	}

	/// <summary>The selected rows (one or several).</summary>
	public IReadOnlyList<DeletedItemViewModel> SelectedItems => _selection;

	public bool HasSelection => _selection.Count > 0;

	/// <summary>The table's selection changed (several rows can be selected).</summary>
	public void SetSelection(IEnumerable<DeletedItemViewModel> items)
	{
		_selection.Clear();
		_selection.AddRange(items.Where(i => i != null).Distinct());
		SelectionChanged();
	}

	private void SelectionChanged()
	{
		OnPropertyChanged(nameof(SelectedItems));
		OnPropertyChanged(nameof(HasSelection));
		OnPropertyChanged(nameof(StatusText));
		RestoreCommand.NotifyCanExecuteChanged();
		DeleteNowCommand.NotifyCanExecuteChanged();
	}

	public string Intro => $"Deleted structures, assessments and photos stay here for {SdeStore.KeepDays} days, then they are removed for good. They aren't in lists, reports or exports while they're here.";

	/// <summary>The status bar: "2 items · Items are kept for 30 days", and how many are selected when several are.</summary>
	public string StatusText
	{
		get
		{
			string keep = $"Items are kept for {SdeStore.KeepDays} days";
			string count = Items.Count switch
			{
				0 => keep,
				1 => "1 item · " + keep,
				int n => $"{n} items · {keep}",
			};
			return _selection.Count > 1 ? $"{count} · {_selection.Count} selected" : count;
		}
	}

	public void Reload()
	{
		HashSet<string> keep = _selection.Select(s => s.Item.ItemKey).ToHashSet();
		Items.Clear();
		foreach (DeletedItemRecord r in app.Store.GetDeletedItems())
		{
			Items.Add(new DeletedItemViewModel(r));
		}
		_selection.Clear();
		_selection.AddRange(Items.Where(i => keep.Contains(i.Item.ItemKey)));
		Selected = _selection.FirstOrDefault();
		OnPropertyChanged(nameof(IsEmpty));
		OnPropertyChanged(nameof(HasItems));
		SelectionChanged();
	}

	/// <summary>The item given (a row's own button or a test), or else the selected rows.</summary>
	private List<DeletedItemViewModel> Targets(DeletedItemViewModel item) => item != null ? new List<DeletedItemViewModel> { item } : _selection.ToList();

	/// <summary>Puts the item (or the selected items) back where it was.</summary>
	[RelayCommand(CanExecute = nameof(CanAct))]
	private void Restore(DeletedItemViewModel item)
	{
		foreach (DeletedItemViewModel target in Targets(item))
		{
			try
			{
				app.Writes.Write(target.Item.AssessmentID is Guid id ? new[] { id } : null, () => app.Store.Restore(target.Item.ItemKey));
			}
			catch (Exception ex) when (ex is not OutOfMemoryException)
			{
				app.Errors.Show("Couldn't restore " + target.Label, ex);
			}
		}
		Reload();
	}

	/// <summary>Removes the item (or the selected items) for good, after asking once.</summary>
	[RelayCommand(CanExecute = nameof(CanAct))]
	private async Task DeleteNow(DeletedItemViewModel item)
	{
		List<DeletedItemViewModel> targets = Targets(item);
		if (targets.Count == 0)
		{
			return;
		}
		string title = targets.Count == 1 ? $"Permanently Delete “{targets[0].Label}”?" : $"Permanently Delete {targets.Count} Items?";
		if (!await app.Dialogs.ConfirmAsync(title, "This can't be undone.", "Delete", destructive: true))
		{
			return;
		}
		foreach (DeletedItemViewModel target in targets)
		{
			app.Store.Purge(target.Item.ItemKey);
		}
		Reload();
	}

	private bool CanAct(DeletedItemViewModel item) => item != null || _selection.Count > 0;

	[RelayCommand]
	private async Task EmptyAll()
	{
		if (IsEmpty || !await app.Dialogs.ConfirmAsync("Empty Recently Deleted?", $"{(Items.Count == 1 ? "1 item" : $"{Items.Count} items")} will be permanently deleted. This can't be undone.", "Empty", destructive: true))
		{
			return;
		}
		app.Store.EmptyTrash();
		Reload();
	}
}
