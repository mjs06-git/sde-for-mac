using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.App.Controls;
using Sde.App.ViewModels.Editor;

namespace Sde.App.ViewModels.Shell;

/// <summary>One page in the sidebar: its icon, name, count badge and keyboard shortcut (⌘1 for the first, and so on).</summary>
public sealed partial class NavItemViewModel : ObservableObject
{
	private readonly Action<Page> _show;

	public NavItemViewModel(Page page, string title, string icon, int number, Action<Page> show)
	{
		Page = page;
		Title = title;
		Icon = icon;
		Number = number;
		_show = show;
	}

	public Page Page { get; }

	public string Title { get; }

	/// <summary>The icon's kind (Styles/Icons.axaml).</summary>
	public string Icon { get; }

	/// <summary>Its place in the sidebar, from 1: View › Title is ⌘ and this number.</summary>
	public int Number { get; }

	/// <summary>"⌘3", for menus and tooltips (none for a page without a number, such as Settings, which is ⌘,).</summary>
	public string Shortcut => Number is >= 1 and <= 9 ? "⌘" + Number : null;

	public string ToolTip => Shortcut == null ? Title : $"{Title} ({Shortcut})";

	/// <summary>The number on the badge (structures, items in Recently deleted…); no badge when zero.</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasCount), nameof(CountText))]
	public partial int Count { get; set; }

	/// <summary>The badge's colour: neutral for totals, accent or warning for things waiting on someone.</summary>
	[ObservableProperty]
	public partial Tone CountTone { get; set; }

	public bool HasCount => Count > 0;

	public string CountText => Count > 9999 ? "9999+" : Count.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("en-US"));

	/// <summary>True while its page (or one of its sub-pages) is showing.</summary>
	[ObservableProperty]
	public partial bool IsActive { get; set; }

	[RelayCommand]
	private void Show() => _show(Page);
}

/// <summary>
/// A row of the sidebar's source list that isn't a page of its own: a smart group (Structures with one result filter)
/// or an event (Structures scoped to it).
/// </summary>
public sealed partial class SourceRowViewModel : ObservableObject
{
	private readonly Action _choose;

	public SourceRowViewModel(string title, string icon, Action choose)
	{
		Title = title;
		Icon = icon;
		_choose = choose;
	}

	public string Title { get; }

	/// <summary>The icon's kind (Styles/Icons.axaml).</summary>
	public string Icon { get; }

	/// <summary>The smart group's result filter, for a smart group.</summary>
	public StructureFilter? Filter { get; init; }

	/// <summary>The event, for an event's row.</summary>
	public EventChoice Event { get; init; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasCount), nameof(CountText))]
	public partial int Count { get; set; }

	public bool HasCount => Count > 0;

	public string CountText => Count.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("en-US"));

	[ObservableProperty]
	public partial bool IsActive { get; set; }

	[RelayCommand]
	private void Show() => _choose();
}

/// <summary>A pinned structure in the sidebar: its address and result dot; choosing it opens its latest assessment.</summary>
public sealed partial class PinnedRowViewModel : ObservableObject
{
	private readonly Action<PinnedRowViewModel> _open;
	private readonly Action<PinnedRowViewModel> _unpin;

	public PinnedRowViewModel(StructureRowViewModel row, Action<PinnedRowViewModel> open, Action<PinnedRowViewModel> unpin)
	{
		Row = row;
		_open = open;
		_unpin = unpin;
	}

	/// <summary>The structure as the list shows it (its latest assessment's result).</summary>
	public StructureRowViewModel Row { get; }

	public Guid PropertyId => Row.Summary.PropertyID;

	public string Title => Row.Address;

	/// <summary>The result's tile, for the dot's colour.</summary>
	public ResultState State => Row.State;

	/// <summary>"123 River Street, Substantially Damaged": the tooltip and what VoiceOver reads.</summary>
	public string ToolTip => $"{Row.Address}, {Row.Result}";

	[RelayCommand]
	private void Show() => _open(this);

	/// <summary>The row's menu: Unpin.</summary>
	[RelayCommand]
	private void Unpin() => _unpin(this);
}

/// <summary>A saved view of the structure list in the sidebar: choosing it shows Structures with the view's filters, sort and columns.</summary>
public sealed partial class SavedViewRowViewModel : ObservableObject
{
	private readonly Action<string> _apply;
	private readonly Func<string, Task> _rename;
	private readonly Action<string> _delete;

	public SavedViewRowViewModel(string name, Action<string> apply, Func<string, Task> rename, Action<string> delete)
	{
		Title = name;
		_apply = apply;
		_rename = rename;
		_delete = delete;
	}

	public string Title { get; }

	/// <summary>True while the list shows this view (until a filter, the sort or the columns change).</summary>
	[ObservableProperty]
	public partial bool IsActive { get; set; }

	[RelayCommand]
	private void Show() => _apply(Title);

	[RelayCommand]
	private Task Rename() => _rename(Title);

	[RelayCommand]
	private void Delete() => _delete(Title);
}

/// <summary>A titled section of the source list (Library, Pinned, Saved Views, Events, Workflow, Data): page rows and source rows.</summary>
public sealed class SourceSectionViewModel(string title, System.Collections.ObjectModel.ObservableCollection<object> rows)
{
	public string Title { get; } = title;

	public System.Collections.ObjectModel.ObservableCollection<object> Rows { get; } = rows;
}
