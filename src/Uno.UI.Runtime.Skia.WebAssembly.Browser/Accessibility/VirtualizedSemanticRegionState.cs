#nullable enable

using System;
using System.Collections.Generic;
using Uno.Disposables;

namespace Uno.UI.Runtime.Skia;

internal sealed class VirtualizedSemanticRegionState : IDisposable
{
	private readonly Dictionary<IntPtr, int> _indices = new();
	private readonly Dictionary<int, IntPtr> _handles = new();
	private readonly CompositeDisposable _subscriptions = new();

	internal bool IsDisposed { get; private set; }
	internal bool Contains(IntPtr handle) => _indices.ContainsKey(handle);
	internal bool TryGetIndex(IntPtr handle, out int index) => _indices.TryGetValue(handle, out index);
	internal IEnumerable<IntPtr> Handles => _indices.Keys;

	internal void Own(Action unsubscribe) => _subscriptions.Add(Disposable.Create(unsubscribe));

	internal bool TryRealize(IntPtr handle, int index, out IntPtr displaced)
	{
		displaced = IntPtr.Zero;
		if (IsDisposed || index < 0)
		{
			return false;
		}

		if (_indices.TryGetValue(handle, out var previousIndex) &&
			_handles.TryGetValue(previousIndex, out var previousHandle) && previousHandle == handle)
		{
			_handles.Remove(previousIndex);
		}

		if (_handles.TryGetValue(index, out var existing) && existing != handle)
		{
			_indices.Remove(existing);
			displaced = existing;
		}

		_indices[handle] = index;
		_handles[index] = handle;
		return true;
	}

	internal bool TryUnrealize(IntPtr handle, int index)
	{
		if (IsDisposed || !_indices.TryGetValue(handle, out var currentIndex) || currentIndex != index)
		{
			return false;
		}

		_indices.Remove(handle);
		if (_handles.TryGetValue(index, out var currentHandle) && currentHandle == handle)
		{
			_handles.Remove(index);
		}
		return true;
	}

	public void Dispose()
	{
		if (!IsDisposed)
		{
			IsDisposed = true;
			_indices.Clear();
			_handles.Clear();
			_subscriptions.Dispose();
		}
	}
}
