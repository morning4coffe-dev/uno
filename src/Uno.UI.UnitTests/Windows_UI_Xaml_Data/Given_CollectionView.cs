#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Foundation.Collections;

namespace Uno.UI.Tests.Windows_UI_Xaml_Data;

[TestClass]
public class Given_CollectionView
{
	[TestMethod]
	public void When_Ungrouped_Source_Changes_Then_Vector_And_Current_State_Follow()
	{
		var source = new ObservableCollection<object>();
		var view = new CollectionView(source, false, new PropertyPath(string.Empty));
		var changes = new List<CollectionChange>();
		view.VectorChanged += (_, args) => changes.Add(args.CollectionChange);

		Assert.AreEqual(0, view.CurrentPosition);

		source.Add("first");
		Assert.AreEqual(0, view.CurrentPosition);
		Assert.AreEqual("first", view.CurrentItem);
		CollectionAssert.AreEqual(new[] { CollectionChange.ItemInserted }, changes);

		source.Clear();
		Assert.AreEqual(0, view.CurrentPosition);
		Assert.IsNull(view.CurrentItem);
		CollectionAssert.AreEqual(
			new[] { CollectionChange.ItemInserted, CollectionChange.Reset },
			changes);
	}

	[TestMethod]
	public void When_NonNotifying_Item_Is_Replaced_Then_CurrentItem_Reads_The_Source()
	{
		var source = new List<object> { "A" };
		var view = new CollectionView(source, false, new PropertyPath(string.Empty));

		Assert.AreEqual("A", view.CurrentItem);
		source[0] = "B";
		Assert.AreEqual("B", view.CurrentItem);
	}

	[TestMethod]
	public void When_NonNotifying_Empty_Source_Grows_Then_CurrentItem_Reads_The_Source()
	{
		var source = new List<object>();
		var view = new CollectionView(source, false, new PropertyPath(string.Empty));

		Assert.IsNull(view.CurrentItem);
		source.Add("A");
		Assert.AreEqual("A", view.CurrentItem);
		Assert.IsTrue(view.MoveCurrentToPosition(0));
		Assert.AreEqual("A", view.CurrentItem);
	}

	[TestMethod]
	public void When_Item_Before_Selected_Null_Is_Inserted_Then_Null_Remains_Current()
	{
		var source = new ObservableCollection<object?> { null, "B" };
		var view = new CollectionView(source, false, new PropertyPath(string.Empty));
		Assert.AreEqual(0, view.CurrentPosition);
		Assert.IsNull(view.CurrentItem);

		source.Insert(0, "A");

		Assert.AreEqual(1, view.CurrentPosition);
		Assert.IsNull(view.CurrentItem);
	}

	[TestMethod]
	public void When_Group_Source_Resets_Then_Groups_Are_Rebuilt()
	{
		var source = new ObservableCollection<object>
		{
			new Group("one"),
			new Group("two", "three")
		};
		var view = new CollectionView(source, true, new PropertyPath(nameof(Group.Items)));
		Assert.AreEqual(2, view.CollectionGroups.Count);
		Assert.AreEqual(3, view.Count);

		source.Clear();
		source.Add(new Group("replacement"));

		Assert.AreEqual(1, view.CollectionGroups.Count);
		Assert.AreEqual(1, view.Count);
	}

	[TestMethod]
	[DataRow(0, 2, 1, "C", true)]
	[DataRow(2, 1, 1, "B", false)]
	[DataRow(1, 1, 1, "C", true)]
	[DataRow(2, 2, 1, "B", true)]
	public void When_Item_Is_Removed_Then_Synchronized_Selection_Tracks_Currency(
		int removedIndex,
		int currentIndex,
		int expectedIndex,
		string expectedItem,
		bool currentChanged)
	{
		var source = new ObservableCollection<object> { "A", "B", "C" };
		var view = new CollectionView(source, false, new PropertyPath(string.Empty));
		var selector = new ListView
		{
			ItemsSource = view,
			SelectionMode = ListViewSelectionMode.Single
		};
		view.MoveCurrentToPosition(currentIndex);
		Assert.AreEqual(currentIndex, selector.SelectedIndex);
		var notifications = new List<string>();
		view.VectorChanged += (_, _) => notifications.Add("VectorChanged");
		view.CurrentChanged += (_, _) => notifications.Add("CurrentChanged");

		source.RemoveAt(removedIndex);

		Assert.AreEqual(expectedIndex, view.CurrentPosition);
		Assert.AreEqual(expectedItem, view.CurrentItem);
		Assert.AreEqual(expectedIndex, selector.SelectedIndex);
		Assert.AreEqual(expectedItem, selector.SelectedItem);
		CollectionAssert.AreEqual(
			currentChanged
				? new[] { "VectorChanged", "CurrentChanged" }
				: new[] { "VectorChanged" },
			notifications);
	}

	[TestMethod]
	[DataRow(false, false)]
	[DataRow(false, true)]
	[DataRow(true, false)]
	[DataRow(true, true)]
	public void When_Current_Occurrence_Changes_Then_Equal_Successor_Remains_Selected(bool sameInstance, bool replace)
	{
		var current = new EqualItem("B");
		var successor = sameInstance ? current : new EqualItem("B");
		var source = new ObservableCollection<object> { "A", current, successor };
		var view = new CollectionView(source, false, new PropertyPath(string.Empty));
		var selector = new ListView { ItemsSource = view, SelectionMode = ListViewSelectionMode.Single };
		view.MoveCurrentToPosition(1);
		var notifications = new List<string>();
		view.VectorChanged += (_, _) => notifications.Add("VectorChanged");
		view.CurrentChanged += (_, _) => notifications.Add("CurrentChanged");

		if (replace)
			source[1] = successor;
		else
			source.RemoveAt(1);

		Assert.AreEqual(1, view.CurrentPosition);
		Assert.AreSame(successor, view.CurrentItem);
		Assert.AreEqual(1, selector.SelectedIndex);
		Assert.AreSame(successor, selector.SelectedItem);
		CollectionAssert.AreEqual(new[] { "VectorChanged", "CurrentChanged" }, notifications);
	}

	private sealed record EqualItem(string Value);

	[TestMethod]
	public void When_Source_Resets_With_The_Same_Current_Item_Then_Selection_Is_Restored()
	{
		var source = new ResettableCollection { "A", "B" };
		var view = new CollectionView(source, false, new PropertyPath(string.Empty));
		var selector = new ListView { ItemsSource = view, SelectionMode = ListViewSelectionMode.Single };
		view.MoveCurrentToPosition(1);
		var changed = 0;
		view.CurrentChanged += (_, _) => changed++;

		source.NotifyReset();

		Assert.AreEqual(1, view.CurrentPosition);
		Assert.AreEqual(1, selector.SelectedIndex);
		Assert.AreEqual("B", selector.SelectedItem);
		Assert.AreEqual(1, changed);
	}

	private sealed class ResettableCollection : ObservableCollection<object>
	{
		public void NotifyReset() => OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
	}

	[TestMethod]
	public void When_View_Is_Replaced_Then_Observable_Source_Does_Not_Retain_It()
	{
		var source = new TrackingCollection<object> { "A", "B" };
		var references = Enumerable.Range(0, 32)
			.Select(_ => CreateDetachedView(source))
			.SelectMany(pair => new[] { pair.View, pair.Selector })
			.ToArray();

		Collect(references);
		source.RaiseReset();
		Collect(references);

		Assert.IsTrue(references.All(reference => !reference.IsAlive));
		Assert.AreEqual(1, source.SubscriberCount);
		Assert.AreEqual(0, source.RemovalCount);
		Assert.IsFalse(source.WrongThreadRemoval);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static (WeakReference View, WeakReference Selector) CreateDetachedView(TrackingCollection<object> source)
	{
		var view = new CollectionView(source, false, new PropertyPath(string.Empty));
		var selector = new ListView { ItemsSource = view };
		return (new WeakReference(view), new WeakReference(selector));
	}

	static void Collect(params WeakReference[] references)
	{
		for (var attempt = 0; attempt < 10 && references.Any(reference => reference.IsAlive); attempt++)
		{
			GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
			GC.WaitForPendingFinalizers();
		}
	}

	private sealed class Group(params object[] items)
	{
		public ObservableCollection<object> Items { get; } = new(items);
	}

	private sealed class TrackingCollection<T> : IList, INotifyCollectionChanged
	{
		readonly List<T> _items = new();
		int _subscriberCount;
		NotifyCollectionChangedEventHandler? _collectionChanged;

		public int SubscriberCount => _subscriberCount;
		public int RemovalCount { get; private set; }
		public bool WrongThreadRemoval { get; private set; }

		public event NotifyCollectionChangedEventHandler? CollectionChanged
		{
			add
			{
				_subscriberCount++;
				_collectionChanged += value;
			}
			remove
			{
				WrongThreadRemoval = true;
				RemovalCount++;
				_subscriberCount--;
				_collectionChanged -= value;
			}
		}

		public void RaiseReset() =>
			_collectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

		public int Add(object? value)
		{
			_items.Add((T)value!);
			return _items.Count - 1;
		}

		public void Clear() => _items.Clear();
		public bool Contains(object? value) => value is T item && _items.Contains(item);
		public int IndexOf(object? value) => value is T item ? _items.IndexOf(item) : -1;
		public void Insert(int index, object? value) => _items.Insert(index, (T)value!);
		public bool IsFixedSize => false;
		public bool IsReadOnly => false;
		public void Remove(object? value)
		{
			if (value is T item)
			{
				_items.Remove(item);
			}
		}
		public void RemoveAt(int index) => _items.RemoveAt(index);
		public object? this[int index]
		{
			get => _items[index];
			set => _items[index] = (T)value!;
		}
		public void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
		public int Count => _items.Count;
		public bool IsSynchronized => false;
		public object SyncRoot => this;
		public IEnumerator GetEnumerator() => _items.GetEnumerator();
	}
}
