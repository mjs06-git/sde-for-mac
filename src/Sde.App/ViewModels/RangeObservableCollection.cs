using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Sde.App.ViewModels;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can replace all its items in one batch, raising a single Reset
/// notification instead of one CollectionChanged event per item.
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
	public RangeObservableCollection()
	{
	}

	public RangeObservableCollection(IEnumerable<T> items)
		: base(items)
	{
	}

	/// <summary>Replaces the collection's contents and raises a single Reset notification.</summary>
	public void ReplaceAll(IEnumerable<T> items)
	{
		ArgumentNullException.ThrowIfNull(items);
		CheckReentrancy();
		Items.Clear();
		if (items is IList<T> list)
		{
			for (int i = 0; i < list.Count; i++)
			{
				Items.Add(list[i]);
			}
		}
		else
		{
			foreach (T item in items)
			{
				Items.Add(item);
			}
		}
		OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
		OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
		OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
	}
}
