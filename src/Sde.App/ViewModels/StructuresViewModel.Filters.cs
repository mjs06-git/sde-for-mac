using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sde.Core;

namespace Sde.App.ViewModels;

/// <summary>A field the filter bar's Add Filter menu offers ("Type", "Inspector", "Tags"…).</summary>
public sealed record FilterField(string Key, string Title);

/// <summary>
/// One filter in the bar above the list, shown as a removable chip ("Type: Residential ×"). Clicking the chip opens a
/// small editor for its value; the × takes the filter off. The value lives on <see cref="StructuresViewModel"/> (so it
/// is remembered and saved in views as before); a chip only shows and edits it.
/// </summary>
public abstract partial class FilterChipViewModel : ObservableObject
{
	private readonly Action<string> _remove;

	protected FilterChipViewModel(string key, string title, Action<string> remove)
	{
		Key = key;
		Title = title;
		_remove = remove;
	}

	public string Key { get; }

	/// <summary>The field's name: "Type".</summary>
	public string Title { get; }

	/// <summary>True when the filter has a value (a chip just added from the menu has none until one is chosen).</summary>
	public abstract bool IsSet { get; }

	/// <summary>The value in words: "Residential", or "Any" with none.</summary>
	public abstract string ValueText { get; }

	/// <summary>The chip's text: "Type: Residential".</summary>
	public string Label => $"{Title}: {ValueText}";

	/// <summary>What VoiceOver reads for the chip: "Filter Type: Residential. Change".</summary>
	public string EditName => $"Filter {Label}. Change";

	/// <summary>What VoiceOver reads for the chip's ×.</summary>
	public string RemoveName => $"Remove the {Title} filter";

	[RelayCommand]
	private void Remove() => _remove(Key);

	/// <summary>The value changed on the list: everything the chip shows follows.</summary>
	public virtual void Refresh()
	{
		OnPropertyChanged(string.Empty);
	}
}

/// <summary>A filter with one choice from a short list (Type, Source, Status, Inspector).</summary>
public sealed partial class ChoiceFilterChipViewModel(string key, string title, Action<string> remove, Func<IReadOnlyList<string>> options, Func<int> get, Action<int> set)
	: FilterChipViewModel(key, title, remove)
{
	private IReadOnlyList<string> _options;

	/// <summary>The choices (with no "any": taking the filter off is the chip's ×).</summary>
	public IReadOnlyList<string> Options => _options ??= options();

	public override void Refresh()
	{
		IReadOnlyList<string> now = options();
		if (_options == null || !_options.SequenceEqual(now))
		{
			_options = now;
		}
		base.Refresh();
	}

	/// <summary>The chosen option's place in <see cref="Options"/>, or -1 for none.</summary>
	public int SelectedIndex
	{
		get => get();
		set
		{
			if (value >= 0 && value != get())
			{
				set(value);
			}
		}
	}

	public override bool IsSet => get() >= 0;

	public override string ValueText => IsSet && get() < Options.Count ? Options[get()] : "Any";
}

/// <summary>A filter by text the structure's field contains or starts with (Street, ZIP code, Parcel).</summary>
public sealed partial class TextFilterChipViewModel(string key, string title, Action<string> remove, Func<string> get, Action<string> set)
	: FilterChipViewModel(key, title, remove)
{
	public string Text
	{
		get => get();
		set => set(value);
	}

	public override bool IsSet => !string.IsNullOrWhiteSpace(get());

	public override string ValueText => IsSet ? get().Trim() : "Any";
}

/// <summary>A filter by inspection date: from, to, or both.</summary>
public sealed partial class DateFilterChipViewModel(string key, string title, Action<string> remove, Func<DateTime?> getFrom, Action<DateTime?> setFrom, Func<DateTime?> getTo, Action<DateTime?> setTo)
	: FilterChipViewModel(key, title, remove)
{
	public DateTime? From
	{
		get => getFrom();
		set => setFrom(value);
	}

	public DateTime? To
	{
		get => getTo();
		set => setTo(value);
	}

	public override bool IsSet => getFrom() != null || getTo() != null;

	public override string ValueText
	{
		get
		{
			CultureInfo us = CultureInfo.GetCultureInfo("en-US");
			return (getFrom(), getTo()) switch
			{
				(DateTime f, DateTime t) => $"{f.ToString("MMM d, yyyy", us)} to {t.ToString("MMM d, yyyy", us)}",
				(DateTime f, null) => $"from {f.ToString("MMM d, yyyy", us)}",
				(null, DateTime t) => $"until {t.ToString("MMM d, yyyy", us)}",
				_ => "Any",
			};
		}
	}
}

/// <summary>A filter by percent damaged: at least, at most, or between.</summary>
public sealed partial class PercentFilterChipViewModel(string key, string title, Action<string> remove, Func<decimal?> getMin, Action<decimal?> setMin, Func<decimal?> getMax, Action<decimal?> setMax)
	: FilterChipViewModel(key, title, remove)
{
	public decimal? Minimum
	{
		get => getMin();
		set => setMin(value);
	}

	public decimal? Maximum
	{
		get => getMax();
		set => setMax(value);
	}

	public override bool IsSet => getMin() != null || getMax() != null;

	public override string ValueText => (getMin(), getMax()) switch
	{
		(decimal a, decimal b) => $"{Percent(a)} to {Percent(b)}",
		(decimal a, null) => $"at least {Percent(a)}",
		(null, decimal b) => $"at most {Percent(b)}",
		_ => "Any",
	};

	private static string Percent(decimal p) => p.ToString("0.#", CultureInfo.InvariantCulture) + "%";
}

/// <summary>A filter by tags: structures with any of the ticked tags, or with all of them.</summary>
public sealed partial class TagsFilterChipViewModel : FilterChipViewModel
{
	private readonly StructuresViewModel _list;
	private bool _syncing;

	public TagsFilterChipViewModel(StructuresViewModel list, Action<string> remove) : base(StructuresViewModel.TagsFilterKey, "Tags", remove)
	{
		_list = list;
	}

	/// <summary>Every tag, ticked when the list is filtered by it.</summary>
	public ObservableCollection<TagFilterChoice> Choices { get; } = new();

	/// <summary>0 = any of the ticked tags, 1 = all of them.</summary>
	public int MatchIndex
	{
		get => _list.TagsMatchAll ? 1 : 0;
		set => _list.TagsMatchAll = value == 1;
	}

	public override bool IsSet => _list.EffectiveTagFilter.Count > 0;

	public override string ValueText
	{
		get
		{
			List<string> names = _list.EffectiveTagFilter.Select(id => _list.AllTags.First(t => t.Id == id).Name).ToList();
			string joiner = _list.TagsMatchAll ? " and " : " or ";
			return names.Count switch
			{
				0 => "Any",
				1 => names[0],
				2 => names[0] + joiner + names[1],
				_ => (_list.TagsMatchAll ? "all of " : "any of ") + names.Count.ToString(CultureInfo.InvariantCulture),
			};
		}
	}

	public override void Refresh()
	{
		_syncing = true;
		try
		{
			HashSet<long> chosen = _list.EffectiveTagFilter.ToHashSet();
			if (Choices.Select(c => c.Tag.Id).SequenceEqual(_list.AllTags.Select(t => t.Id)) && Choices.All(c => _list.AllTags.Any(t => t.Id == c.Tag.Id && t.Name == c.Tag.Name && t.Colour == c.Tag.Colour)))
			{
				foreach (TagFilterChoice c in Choices)
				{
					c.IsSelected = chosen.Contains(c.Tag.Id);
				}
			}
			else
			{
				Choices.Clear();
				foreach (TagViewModel t in _list.AllTags)
				{
					Choices.Add(new TagFilterChoice(t, chosen.Contains(t.Id), ChoicesChanged));
				}
			}
		}
		finally
		{
			_syncing = false;
		}
		base.Refresh();
	}

	private void ChoicesChanged()
	{
		if (!_syncing)
		{
			_list.TagFilter = Choices.Where(c => c.IsSelected).Select(c => c.Tag.Id).ToList();
		}
	}
}

/// <summary>The filter bar: Add Filter, a chip for each filter in use, and Clear.</summary>
public sealed partial class StructuresViewModel
{
	public const string TagsFilterKey = "Tags";

	/// <summary>Filters added from the menu that have no value yet (their chips show "Any" with the editor open).</summary>
	private readonly HashSet<string> _pendingFilters = new();

	private readonly Dictionary<string, FilterChipViewModel> _chips = new();

	/// <summary>The inspector's name to filter by, or null for any.</summary>
	[ObservableProperty]
	public partial string InspectorFilter { get; set; }

	/// <summary>The tags to filter by (TagIDs); empty for none.</summary>
	[ObservableProperty]
	public partial IReadOnlyList<long> TagFilter { get; set; } = Array.Empty<long>();

	/// <summary>True: only structures with every tag in <see cref="TagFilter"/>; false: with any of them.</summary>
	[ObservableProperty]
	public partial bool TagsMatchAll { get; set; }

	partial void OnTagFilterChanged(IReadOnlyList<long> value) => Changed();

	/// <summary>The tag filter without tags that have since been deleted, in list order.</summary>
	public IReadOnlyList<long> EffectiveTagFilter
	{
		get
		{
			HashSet<long> wanted = (TagFilter ?? Array.Empty<long>()).ToHashSet();
			return AllTags.Where(t => wanted.Contains(t.Id)).Select(t => t.Id).ToList();
		}
	}

	/// <summary>The chips in the bar, one per filter in use (and one just added from the menu), in the menu's order.</summary>
	public ObservableCollection<FilterChipViewModel> FilterChips { get; } = new();

	/// <summary>True while any filter in the bar has a value (Clear shows).</summary>
	public bool HasActiveFilters => FilterChips.Any(c => c.IsSet);

	/// <summary>Raised after Add Filter adds a chip: the view opens that chip's editor.</summary>
	public event Action<string> FilterEditRequested;

	/// <summary>The fields Add Filter lists, in order: Source only once something was imported, Tags once a tag exists.</summary>
	public IReadOnlyList<FilterField> FilterFields
	{
		get
		{
			List<FilterField> fields = new()
			{
				new FilterField("Type", "Type"),
			};
			if (HasImported || SourceFilter != 0)
			{
				fields.Add(new FilterField("Source", "Source"));
			}
			fields.Add(new FilterField("Status", "Status"));
			fields.Add(new FilterField("Inspector", "Inspector"));
			fields.Add(new FilterField("Inspected", "Inspected"));
			fields.Add(new FilterField("Damaged", "Damaged"));
			fields.Add(new FilterField("Street", "Street"));
			fields.Add(new FilterField("Zip", "ZIP code"));
			fields.Add(new FilterField("Parcel", "Parcel"));
			if (HasTags || TagFilter.Count > 0)
			{
				fields.Add(new FilterField(TagsFilterKey, "Tags"));
			}
			return fields;
		}
	}

	private static readonly string[] FilterOrder = { "Type", "Source", "Status", "Inspector", "Inspected", "Damaged", "Street", "Zip", "Parcel", TagsFilterKey };

	private static readonly string[] TypeChoices = { "Residential", "Non-residential" };

	private static readonly string[] SourceChoices = { "Imported", "Entered in SDE" };

	private static IReadOnlyList<string> StatusOnlyChoices => StatusChoices.Skip(1).ToList();

	/// <summary>The inspectors named in the list, A to Z (and the one filtered by, if it has gone).</summary>
	private IReadOnlyList<string> InspectorChoices()
	{
		List<string> names = _all.Select(r => (r.Inspector ?? "").Trim()).Where(n => n.Length > 0)
			.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
		if (Blank(InspectorFilter) is string current && !names.Contains(current.Trim(), StringComparer.OrdinalIgnoreCase))
		{
			names.Add(current.Trim());
		}
		return names;
	}

	/// <summary>Add Filter, then a field: its chip appears (with no value yet) and its editor opens.</summary>
	[RelayCommand]
	public void AddFilter(string key)
	{
		if (!FilterOrder.Contains(key))
		{
			return;
		}
		_pendingFilters.Add(key);
		RefreshFilterChips();
		FilterEditRequested?.Invoke(key);
	}

	/// <summary>A chip's ×: takes that filter off.</summary>
	public void RemoveFilter(string key)
	{
		_restoring = true;
		try
		{
			_pendingFilters.Remove(key);
			switch (key)
			{
				case "Type":
					TypeFilter = 0;
					break;
				case "Source":
					SourceFilter = 0;
					break;
				case "Status":
					StatusFilter = 0;
					break;
				case "Inspector":
					InspectorFilter = null;
					break;
				case "Inspected":
					InspectedFrom = null;
					InspectedTo = null;
					break;
				case "Damaged":
					MinPercent = null;
					MaxPercent = null;
					break;
				case "Street":
					StreetFilter = null;
					break;
				case "Zip":
					ZipFilter = null;
					break;
				case "Parcel":
					ParcelFilter = null;
					break;
				case TagsFilterKey:
					TagFilter = Array.Empty<long>();
					TagsMatchAll = false;
					break;
			}
		}
		finally
		{
			_restoring = false;
		}
		Changed();
	}

	/// <summary>A chip's editor opened: the chip stays while it is edited, even if its value is cleared to type a new one.</summary>
	public void BeginEditFilter(string key)
	{
		if (FilterOrder.Contains(key))
		{
			_pendingFilters.Add(key);
		}
	}

	/// <summary>A chip's editor closed: a chip that still has no value goes away.</summary>
	public void DropPendingFilter(string key)
	{
		if (_pendingFilters.Remove(key))
		{
			RefreshFilterChips();
		}
	}

	/// <summary>The chip for a filter (made once, so an open editor survives the list refreshing).</summary>
	public FilterChipViewModel Chip(string key)
	{
		if (_chips.TryGetValue(key, out FilterChipViewModel chip))
		{
			return chip;
		}
		chip = key switch
		{
			"Type" => new ChoiceFilterChipViewModel(key, "Type", RemoveFilter, () => TypeChoices, () => TypeFilter - 1, i => TypeFilter = i + 1),
			"Source" => new ChoiceFilterChipViewModel(key, "Source", RemoveFilter, () => SourceChoices, () => SourceFilter - 1, i => SourceFilter = i + 1),
			"Status" => new ChoiceFilterChipViewModel(key, "Status", RemoveFilter, () => StatusOnlyChoices, () => StatusFilter - 1, i => StatusFilter = i + 1),
			"Inspector" => new ChoiceFilterChipViewModel(key, "Inspector", RemoveFilter, InspectorChoices,
				() => Blank(InspectorFilter) is string name ? InspectorChoices().ToList().FindIndex(n => string.Equals(n, name.Trim(), StringComparison.OrdinalIgnoreCase)) : -1,
				i => InspectorFilter = InspectorChoices()[i]),
			"Inspected" => new DateFilterChipViewModel(key, "Inspected", RemoveFilter, () => InspectedFrom, v => InspectedFrom = v, () => InspectedTo, v => InspectedTo = v),
			"Damaged" => new PercentFilterChipViewModel(key, "Damaged", RemoveFilter, () => MinPercent, v => MinPercent = v, () => MaxPercent, v => MaxPercent = v),
			"Street" => new TextFilterChipViewModel(key, "Street", RemoveFilter, () => StreetFilter, v => StreetFilter = v),
			"Zip" => new TextFilterChipViewModel(key, "ZIP code", RemoveFilter, () => ZipFilter, v => ZipFilter = v),
			"Parcel" => new TextFilterChipViewModel(key, "Parcel", RemoveFilter, () => ParcelFilter, v => ParcelFilter = v),
			TagsFilterKey => new TagsFilterChipViewModel(this, RemoveFilter),
			_ => throw new ArgumentException("No filter called " + key),
		};
		_chips[key] = chip;
		return chip;
	}

	/// <summary>Brings the chips in line with the filters: one for each filter in use or just added, in the menu's order.</summary>
	private void RefreshFilterChips()
	{
		List<FilterChipViewModel> wanted = FilterOrder.Select(Chip).Where(c => c.IsSet || _pendingFilters.Contains(c.Key)).ToList();
		for (int i = FilterChips.Count - 1; i >= 0; i--)
		{
			if (!wanted.Contains(FilterChips[i]))
			{
				FilterChips.RemoveAt(i);
			}
		}
		for (int i = 0; i < wanted.Count; i++)
		{
			if (i >= FilterChips.Count || FilterChips[i] != wanted[i])
			{
				FilterChips.Remove(wanted[i]);
				FilterChips.Insert(i, wanted[i]);
			}
		}
		foreach (FilterChipViewModel c in wanted)
		{
			c.Refresh();
		}
		OnPropertyChanged(nameof(HasActiveFilters));
		OnPropertyChanged(nameof(FilterFields));
	}
}
