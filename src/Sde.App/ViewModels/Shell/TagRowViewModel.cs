using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sde.App.ViewModels.Shell;

/// <summary>A tag in the sidebar's Tags section: its colour dot, name and how many structures have it; choosing it filters the list.</summary>
public sealed partial class TagRowViewModel : ObservableObject
{
	private readonly Action<long> _show;
	private readonly Func<Task> _manage;

	public TagRowViewModel(TagViewModel tag, int count, Action<long> show, Func<Task> manage)
	{
		Tag = tag;
		Count = count;
		_show = show;
		_manage = manage;
	}

	public TagViewModel Tag { get; }

	public long Id => Tag.Id;

	public string Title => Tag.Name;

	public string DotKey => Tag.DotKey;

	/// <summary>How many structures (not in Recently Deleted) have the tag.</summary>
	public int Count { get; }

	public bool HasCount => Count > 0;

	public string CountText => Count.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("en-US"));

	/// <summary>"Revisit, 3 structures": the tooltip.</summary>
	public string ToolTip => $"{Title}, {Count:N0} structure{(Count == 1 ? "" : "s")}";

	/// <summary>True while the list shows only this tag's structures.</summary>
	[ObservableProperty]
	public partial bool IsActive { get; set; }

	[RelayCommand]
	private void Show() => _show(Id);

	/// <summary>The row's menu: Manage Tags.</summary>
	[RelayCommand]
	private Task Manage() => _manage();
}
