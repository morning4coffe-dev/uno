using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Runtime.CompilerServices;
using Uno;
using Uno.Extensions;
using Uno.Extensions.Specialized;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Core;

namespace Microsoft.UI.Xaml.Data
{
	internal partial class CollectionView : ICollectionView
	{
		private static readonly ConditionalWeakTable<INotifyCollectionChanged, CollectionChangedHub> _collectionChangedHubs = new();
		private IEnumerable _collection;
		private readonly bool _isGrouped;
		private readonly PropertyPath _itemsPath;
		private readonly IDisposable _collectionChangedSubscription;
		private object _currentItem;
		private bool _isProcessingCollectionChange;

		public CollectionView(IEnumerable collection, bool isGrouped, PropertyPath itemsPath)
		{
			_collection = collection;
			_isGrouped = isGrouped;
			_itemsPath = itemsPath;

			if (isGrouped)
			{
				CollectionGroups = new ObservableVector<object>();
				RebuildGroups();
			}

			if (_collection is INotifyCollectionChanged observableCollection)
			{
				_collectionChangedSubscription = _collectionChangedHubs
					.GetValue(observableCollection, static source => new CollectionChangedHub(source))
					.Register(this);
			}

			_currentItem = GetItemAtCurrentPosition();
		}

		public IEnumerable InnerCollection => _collection;

		private void OnCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
		{
			if (_isGrouped)
			{
				UpdateGroups(e);
			}
			else
			{
				var previousPosition = CurrentPosition;
				var previousItem = _currentItem;
				var wasEmptyBeforeChange =
					e.Action == NotifyCollectionChangedAction.Add &&
					Count == e.NewItems.Count;
				_isProcessingCollectionChange = true;
				try
				{
					VectorChanged?.Invoke(this, e.ToVectorChangedEventArgs());
				}
				finally
				{
					_isProcessingCollectionChange = false;
				}
				CurrentPosition = GetAdjustedCurrentPosition(e, previousPosition, wasEmptyBeforeChange);
				_currentItem = GetItemAtCurrentPosition();

				var currentOccurrenceChanged = previousPosition >= 0 &&
					(e.Action == NotifyCollectionChangedAction.Reset ||
						(e.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace &&
							e.OldStartingIndex >= 0 && previousPosition >= e.OldStartingIndex &&
							previousPosition - e.OldStartingIndex < e.OldItems.Count));
				if (currentOccurrenceChanged || previousPosition != CurrentPosition || !Equals(previousItem, _currentItem))
				{
					CurrentChanged?.Invoke(this, null);
				}
			}
		}

		private int GetAdjustedCurrentPosition(
			NotifyCollectionChangedEventArgs args,
			int previousPosition,
			bool wasEmptyBeforeChange)
		{
			if (previousPosition < 0)
			{
				return previousPosition;
			}

			var count = Count;
			if (count == 0)
			{
				return 0;
			}

			return args.Action switch
			{
				NotifyCollectionChangedAction.Add when wasEmptyBeforeChange => Math.Min(previousPosition, count - 1),
				NotifyCollectionChangedAction.Add when args.NewStartingIndex >= 0 && args.NewStartingIndex <= previousPosition
					=> previousPosition + args.NewItems.Count,
				NotifyCollectionChangedAction.Remove when args.OldStartingIndex >= 0 &&
					previousPosition >= args.OldStartingIndex + args.OldItems.Count
					=> previousPosition - args.OldItems.Count,
				NotifyCollectionChangedAction.Remove when args.OldStartingIndex >= 0 &&
					previousPosition >= args.OldStartingIndex
					=> Math.Min(args.OldStartingIndex, count - 1),
				NotifyCollectionChangedAction.Replace when args.OldStartingIndex >= 0 &&
					previousPosition >= args.OldStartingIndex + args.OldItems.Count
					=> Math.Clamp(previousPosition + args.NewItems.Count - args.OldItems.Count, 0, count - 1),
				NotifyCollectionChangedAction.Replace when args.OldStartingIndex >= 0 &&
					previousPosition >= args.OldStartingIndex
					=> Math.Min(args.NewStartingIndex + Math.Min(previousPosition - args.OldStartingIndex, args.NewItems.Count - 1), count - 1),
				NotifyCollectionChangedAction.Move => GetPositionAfterMove(args, previousPosition),
				NotifyCollectionChangedAction.Reset => Math.Min(previousPosition, count - 1),
				_ => Math.Min(previousPosition, count - 1),
			};
		}

		private static int GetPositionAfterMove(NotifyCollectionChangedEventArgs args, int previousPosition)
		{
			var movedCount = args.OldItems.Count;
			if (previousPosition >= args.OldStartingIndex && previousPosition < args.OldStartingIndex + movedCount)
			{
				return args.NewStartingIndex + previousPosition - args.OldStartingIndex;
			}

			var position = previousPosition;
			if (position >= args.OldStartingIndex + movedCount)
			{
				position -= movedCount;
			}
			if (position >= args.NewStartingIndex)
			{
				position += movedCount;
			}
			return position;
		}

		private object GetItemAtCurrentPosition()
			=> CurrentPosition >= 0 && CurrentPosition < Count
				? _isGrouped ? AsEnumerable.ElementAt(CurrentPosition) : _collection.ElementAt(CurrentPosition)
				: null;

		private void RebuildGroups()
		{
			CollectionGroups.Clear();
			foreach (var group in _collection)
			{
				CollectionGroups.Add(new CollectionViewGroup(group, _itemsPath));
			}
		}

		private void UpdateGroups(NotifyCollectionChangedEventArgs e)
		{
			switch (e.Action)
			{
				case NotifyCollectionChangedAction.Add:
					for (var i = 0; i < e.NewItems.Count; i++)
					{
						CollectionGroups.Insert(
							e.NewStartingIndex + i,
							new CollectionViewGroup(e.NewItems[i], _itemsPath));
					}
					break;

				case NotifyCollectionChangedAction.Remove:
					for (var i = e.OldItems.Count - 1; i >= 0; i--)
					{
						CollectionGroups.RemoveAt(e.OldStartingIndex + i);
					}
					break;

				case NotifyCollectionChangedAction.Replace:
					for (var i = 0; i < e.NewItems.Count; i++)
					{
						CollectionGroups[e.NewStartingIndex + i] =
							new CollectionViewGroup(e.NewItems[i], _itemsPath);
					}
					break;

				case NotifyCollectionChangedAction.Move:
					var moved = new List<object>();
					for (var i = 0; i < e.OldItems.Count; i++)
					{
						moved.Add(CollectionGroups[e.OldStartingIndex]);
						CollectionGroups.RemoveAt(e.OldStartingIndex);
					}
					for (var i = 0; i < moved.Count; i++)
					{
						CollectionGroups.Insert(e.NewStartingIndex + i, moved[i]);
					}
					break;

				case NotifyCollectionChangedAction.Reset:
					RebuildGroups();
					break;
			}
		}

		object IList<object>.this[int index]
		{
			get
			{
				if (!_isGrouped)
				{
					return _collection.ElementAt(index);
				}
				else
				{
					return AsEnumerable.ElementAt(index); //TODO: should use logic from ItemsControl for caching and utilizing group counts
				}
			}

			set
			{
				(_collection as IList ?? throw new NotSupportedException())[index] = value;
			}
		}

		/// <summary>
		/// Hack to prevent extension methods from detecting IList properties and optimizing in a way that breaks with grouping. Can be removed once grouping is handled more efficiently.
		/// </summary>
		private IEnumerable AsEnumerable
		{
			get
			{
				foreach (var item in this)
				{
					yield return item;
				}
			}
		}

		public object CurrentItem
		{
			get
			{
				if (!_isGrouped)
				{
					return _collectionChangedSubscription is null
						? GetItemAtCurrentPosition()
						: _currentItem;
				}
				else
				{
					return AsEnumerable.ElementAt(CurrentPosition); //TODO: should use logic from ItemsControl for caching and utilizing group counts
				}
			}
		}

		public int CurrentPosition { get; private set; }

		public bool HasMoreItems => false;

		public bool IsCurrentAfterLast => CurrentPosition >= Count;

		public bool IsCurrentBeforeFirst => CurrentPosition < 0;

		public int Count
		{
			get
			{
				if (!_isGrouped)
				{
					return _collection.Count();
				}
				else
				{
					var count = 0;

					foreach (ICollectionViewGroup group in CollectionGroups)
					{
						count += group.GroupItems.Count;
					}

					return count;
				}
			}
		}

		bool ICollection<object>.IsReadOnly => false;

		public IObservableVector<object> CollectionGroups { get; }

		public event EventHandler<object> CurrentChanged;
		public event CurrentChangingEventHandler CurrentChanging;

		public event VectorChangedEventHandler<object> VectorChanged;

		public IAsyncOperation<LoadMoreItemsResult> LoadMoreItemsAsync(uint count)
		{
			throw new NotSupportedException();
		}

		public bool MoveCurrentTo(object item)
		{
			var index = IndexOf(item);
			return MoveCurrentToPosition(index);
		}

		public bool MoveCurrentToFirst()
		{
			var target = Count > 0 ? 0 : -1;
			return MoveCurrentToPosition(target);
		}

		public bool MoveCurrentToLast()
		{
			var target = Count - 1;
			return MoveCurrentToPosition(target);
		}

		public bool MoveCurrentToNext()
		{
			return MoveCurrentToPosition(CurrentPosition + 1);
		}

		public bool MoveCurrentToPosition(int index)
		{
			if (_isProcessingCollectionChange)
			{
				return false;
			}

			if (index != CurrentPosition)
			{
				if (index < -1 || index >= Count)
				{
					return false;
				}

				var e = new CurrentChangingEventArgs();
				CurrentChanging?.Invoke(this, e);
				if (e.Cancel)
				{
					return false;
				}
				CurrentPosition = index;
				_currentItem = GetItemAtCurrentPosition();
				CurrentChanged?.Invoke(this, null); // null matches Windows here
				return true;
			}

			return true;
		}

		public bool MoveCurrentToPrevious()
		{
			return MoveCurrentToPosition(CurrentPosition - 1);
		}

		void ICollection<object>.Add(object item) => (_collection as IList ?? throw new NotSupportedException()).Add(item);

		void ICollection<object>.Clear() => (_collection as IList ?? throw new NotSupportedException()).Clear();

		public bool Contains(object item) => _collection?.Contains(item) ?? false;

		void ICollection<object>.CopyTo(object[] array, int arrayIndex)
		{
			//TODO: this is used by eg Linq.ToArray(), it should take grouping into account

			if (_collection is ICollection<object> list)
			{
				list.CopyTo(array, arrayIndex);
			}
			else if (_collection is ICollection collection)
			{
				collection.CopyTo(array, arrayIndex);
			}

			_collection?.ToObjectArray().CopyTo(array, arrayIndex);
		}

		IEnumerator<object> IEnumerable<object>.GetEnumerator()
		{
			var enumerator = (this as IEnumerable).GetEnumerator();
			while (enumerator.MoveNext())
			{
				yield return enumerator.Current;
			}
		}

		IEnumerator IEnumerable.GetEnumerator()
		{
			// In Windows if CollectionView is from a CollectionViewSource marked grouped, it enumerates the flattened list of objects
			if (_isGrouped)
			{
				return CollectionGroups.OfType<ICollectionViewGroup>().SelectManyUntyped(c => c.GroupItems).GetEnumerator();
			}
			return (_collection as IEnumerable).GetEnumerator();
		}

		public IEnumerator<object> GetEnumerator()
		{
			// In Windows if CollectionView is from a CollectionViewSource marked grouped, it enumerates the flattened list of objects
			if (_isGrouped)
			{
				return CollectionGroups.OfType<ICollectionViewGroup>().SelectMany(c => c.GroupItems).GetEnumerator();
			}
			return (_collection as IEnumerable<object>)?.GetEnumerator();
		}

		public int IndexOf(object item) => _collection.IndexOf(item);

		void IList<object>.Insert(int index, object item) => (_collection as IList ?? throw new NotSupportedException()).Insert(index, item);

		bool ICollection<object>.Remove(object item)
		{
			var contains = Contains(item);
			(_collection as IList ?? throw new NotSupportedException()).Remove(item);
			return contains;
		}

		void IList<object>.RemoveAt(int index) => (_collection as IList ?? throw new NotSupportedException()).RemoveAt(index);

		private sealed class CollectionChangedHub
		{
			private readonly WeakEventHelper.WeakEventCollection _handlers = new();

			public CollectionChangedHub(INotifyCollectionChanged source)
			{
				source.CollectionChanged += OnCollectionChanged;
			}

			public IDisposable Register(CollectionView owner)
			{
				NotifyCollectionChangedEventHandler handler = owner.OnCollectionChanged;
				return WeakEventHelper.RegisterEvent(
					_handlers,
					handler,
					static (registeredHandler, sender, args) =>
						((NotifyCollectionChangedEventHandler)registeredHandler)(
							sender,
							(NotifyCollectionChangedEventArgs)args));
			}

			private void OnCollectionChanged(object sender, NotifyCollectionChangedEventArgs args)
				=> _handlers.Invoke(sender, args);
		}
	}
}
