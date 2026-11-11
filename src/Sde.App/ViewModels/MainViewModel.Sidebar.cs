using System.Collections.ObjectModel;
using Sde.App.Services;
using Sde.App.ViewModels.Shell;
using Sde.Core;
using Sde.Data;

namespace Sde.App.ViewModels;

/// <summary>
/// The sidebar's Pinned and Saved Views sections, and the structures opened recently (the ⌘K palette's Recent group).
/// </summary>
public sealed partial class MainViewModel
{
	/// <summary>The per-database setting (MacSetting) that holds the pinned structures' property ids, in the order they were pinned.</summary>
	public const string PinnedSettingKey = "Pinned.Structures";

	private List<Guid> _pinned = new();
	private readonly ObservableCollection<object> _pinnedRows = new();
	private readonly ObservableCollection<object> _savedViewRows = new();

	/// <summary>The pinned structures' property ids, in the order they were pinned (including any now deleted).</summary>
	public IReadOnlyList<Guid> PinnedIds => _pinned;

	/// <summary>Whether a structure is pinned to the sidebar.</summary>
	public bool IsPinned(Guid propertyId) => _pinned.Contains(propertyId);

	/// <summary>Pins a structure to the sidebar (at the end of Pinned) or unpins it, and remembers that in the database.</summary>
	public void SetPinned(Guid propertyId, bool pinned)
	{
		if (propertyId == Guid.Empty || pinned == IsPinned(propertyId))
		{
			return;
		}
		if (pinned)
		{
			_pinned.Add(propertyId);
		}
		else
		{
			_pinned.Remove(propertyId);
		}
		try
		{
			_app.Store.SetSetting(PinnedSettingKey, _pinned.Count == 0 ? null : _pinned);
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			AppLog.Write("Couldn't remember the pinned structures", ex);
		}
		RebuildPinnedRows();
		Structures.PinsChanged();
		Editor?.PinsChanged();
	}

	/// <summary>Reads the open database's pins.</summary>
	private void LoadPins()
	{
		try
		{
			_pinned = _app.Store.GetSetting<List<Guid>>(PinnedSettingKey) ?? new List<Guid>();
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			AppLog.Write("Couldn't read the pinned structures", ex);
			_pinned = new List<Guid>();
		}
	}

	/// <summary>
	/// The Pinned rows, from the list the structure list last read: each pinned structure still in the database, with
	/// its latest assessment's result. A deleted structure has no row (it comes back if it is restored).
	/// </summary>
	private void RebuildPinnedRows()
	{
		Dictionary<Guid, StructureSummary> latest = new();
		foreach (StructureSummary s in Structures.Everything)
		{
			// The list is newest first, so the first row for a structure is its latest assessment.
			latest.TryAdd(s.PropertyID, s);
		}
		ReferenceData reference = null;
		_pinnedRows.Clear();
		foreach (Guid id in _pinned)
		{
			if (latest.TryGetValue(id, out StructureSummary s))
			{
				reference ??= _app.Store.Reference;
				_pinnedRows.Add(new PinnedRowViewModel(new StructureRowViewModel(s, reference), row => OpenStructure(row.Row.Summary), row => SetPinned(row.PropertyId, false)));
			}
		}
	}

	/// <summary>The Saved Views rows, one for each of the structure list's saved views.</summary>
	private void RebuildSavedViewRows()
	{
		_savedViewRows.Clear();
		foreach (string name in Structures.SavedViews)
		{
			_savedViewRows.Add(new SavedViewRowViewModel(name, ShowSavedView, n => Structures.RenameViewCommand.ExecuteAsync(n), n => Structures.DeleteViewCommand.Execute(n)));
		}
		UpdateSidebar();
	}

	/// <summary>A saved view's row: Structures, with the view's filters, sort and columns.</summary>
	private void ShowSavedView(string name)
	{
		if (ActivePage != Page.Structures)
		{
			Show(Page.Structures);
		}
		Structures.ApplyViewCommand.Execute(name);
		UpdateSidebar();
	}

	private readonly ObservableCollection<object> _tagRows = new();

	/// <summary>The Tags rows: each tag with its colour and how many structures have it (hidden while there are no tags).</summary>
	private void RebuildTagRows()
	{
		Dictionary<long, int> counts;
		try
		{
			counts = _app.Store.GetTagCounts();
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			AppLog.Write("Couldn't count the tags", ex);
			counts = new Dictionary<long, int>();
		}
		_tagRows.Clear();
		foreach (TagViewModel tag in Structures.AllTags)
		{
			_tagRows.Add(new TagRowViewModel(tag, counts.GetValueOrDefault(tag.Id), ShowTag, () => Structures.ManageTagsCommand.ExecuteAsync(null)));
		}
		UpdateSidebar();
	}

	/// <summary>A tag's row (or the palette's "Tag: …"): Structures, filtered to the structures with that tag.</summary>
	public void ShowTag(long tagId)
	{
		if (ActivePage != Page.Structures)
		{
			Show(Page.Structures);
		}
		Structures.ShowTag(tagId);
		UpdateSidebar();
	}

	/// <summary>Puts a structure first in this database's recent structures (the palette's Recent group).</summary>
	private void RememberRecent(Guid propertyId)
	{
		try
		{
			_app.Settings.AddRecentStructure(_app.Store.Database.FilePath, propertyId);
			_app.Settings.Save();
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			AppLog.Write("Couldn't remember the recent structures", ex);
		}
	}

	/// <summary>The structures opened most recently in this database, newest first.</summary>
	public IReadOnlyList<Guid> RecentStructureIds => _app.Settings.RecentStructuresIn(_app.Store.Database.FilePath);
}
