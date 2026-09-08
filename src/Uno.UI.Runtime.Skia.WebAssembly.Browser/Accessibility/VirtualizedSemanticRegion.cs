#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.JavaScript;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Uno.Foundation.Logging;

namespace Uno.UI.Runtime.Skia;

/// <summary>
/// Tracks the accessibility state of a virtualized container
/// (ItemsRepeater, ListView, GridView). Creates/removes semantic DOM elements
/// as items are realized/unrealized, maintaining correct aria-posinset/aria-setsize.
/// </summary>
internal sealed partial class VirtualizedSemanticRegion : IDisposable
{
	private readonly IntPtr _containerHandle;
	private readonly VirtualizedSemanticRegionState _state = new();
	private readonly int _generation;
	private int _totalItemCount;
	private bool _isFocusPinned;
	private int? _pinnedIndex;

	/// <summary>
	/// Initializes a new virtualized semantic region and registers it in the DOM.
	/// </summary>
	/// <param name="containerHandle">Handle of the container visual.</param>
	/// <param name="role">ARIA role ("listbox" or "grid").</param>
	/// <param name="label">Accessible name for the container.</param>
	/// <param name="multiselectable">Whether multiple items can be selected.</param>
	internal VirtualizedSemanticRegion(IntPtr containerHandle, string role, string? label, bool multiselectable)
	{
		if (this.Log().IsEnabled(LogLevel.Debug))
		{
			this.Log().Debug($"Register container={containerHandle} role='{role}' label='{label}' multiselectable={multiselectable}");
		}
		_containerHandle = containerHandle;
		_generation = NativeMethods.RegisterVirtualizedContainer(containerHandle, role, label ?? string.Empty, multiselectable);
	}

	/// <summary>Gets the handle of the virtualized container visual.</summary>
	internal IntPtr ContainerHandle => _containerHandle;
	/// <summary>Gets the total number of items in the data source.</summary>
	internal int TotalItemCount => _totalItemCount;
	/// <summary>Gets whether the region is tracking a focused item index.</summary>
	internal bool IsFocusPinned => _isFocusPinned;
	/// <summary>Gets the data index of the pinned (focused) item, if any.</summary>
	internal int? PinnedIndex => _pinnedIndex;
	/// <summary>True if the given item handle currently has a realized DOM node in this region.</summary>
	internal bool ContainsRealizedHandle(IntPtr handle) => _state.Contains(handle);
	internal bool IsDisposed => _state.IsDisposed;
	internal bool TryGetIndex(IntPtr handle, out int index) => _state.TryGetIndex(handle, out index);
	internal void OwnSubscription(Action unsubscribe) => _state.Own(unsubscribe);

	internal void RemoveExcept(HashSet<IntPtr> realizedHandles)
	{
		List<(IntPtr Handle, int Index)>? removed = null;
		foreach (var handle in _state.Handles)
		{
			if (!realizedHandles.Contains(handle) && _state.TryGetIndex(handle, out var index))
			{
				(removed ??= new()).Add((handle, index));
			}
		}
		if (removed is not null)
		{
			foreach (var item in removed)
			{
				OnItemUnrealized(item.Handle, item.Index);
			}
		}
	}

	/// <summary>
	/// Called when an item is realized (ElementPrepared).
	/// </summary>
	internal void OnItemRealized(IntPtr itemHandle, int index, int totalCount, float x, float y, float width, float height, string role, string label, bool disabled = false, bool selected = false)
	{
		if (!_state.TryRealize(itemHandle, index, out var displaced))
		{
			return;
		}
		if (this.Log().IsEnabled(LogLevel.Trace))
		{
			this.Log().Trace($"ItemRealized container={_containerHandle} item={itemHandle} index={index} total={totalCount} role='{role}' label='{label}' pos=({x},{y}) size={width}x{height}");
		}
		if (displaced != IntPtr.Zero)
		{
			NativeMethods.RemoveVirtualizedItem(displaced, _containerHandle, _generation);
		}
		UpdateItemCount(totalCount);
		NativeMethods.AddVirtualizedItem(_containerHandle, itemHandle, index, totalCount, x, y, width, height, role, label, _generation, disabled, selected);
	}

	/// <summary>
	/// Called when an item is unrealized (ElementClearing).
	/// </summary>
	internal void OnItemUnrealized(IntPtr itemHandle, int index)
	{
		if (!_state.TryUnrealize(itemHandle, index))
		{
			return;
		}
		if (_pinnedIndex == index)
		{
			// A recycled element must not keep exposing the previous item's focused identity.
			UnpinFocusedItem();
		}
		if (this.Log().IsEnabled(LogLevel.Trace))
		{
			this.Log().Trace($"ItemUnrealized container={_containerHandle} item={itemHandle} index={index}");
		}

		NativeMethods.RemoveVirtualizedItem(itemHandle, _containerHandle, _generation);
	}

	/// <summary>
	/// Updates the total item count (e.g., when data source changes).
	/// </summary>
	internal void UpdateItemCount(int totalCount)
	{
		if (IsDisposed || _totalItemCount == totalCount)
		{
			return;
		}
		if (this.Log().IsEnabled(LogLevel.Debug))
		{
			this.Log().Debug($"UpdateItemCount container={_containerHandle} oldCount={_totalItemCount} newCount={totalCount}");
		}
		_totalItemCount = totalCount;
		NativeMethods.UpdateVirtualizedItemCount(_containerHandle, totalCount, _generation);
	}

	/// <summary>
	/// Records the focused index without overriding the control's realization lifetime.
	/// </summary>
	internal void PinFocusedItem(int index)
	{
		if (IsDisposed)
		{
			return;
		}
		if (this.Log().IsEnabled(LogLevel.Debug))
		{
			this.Log().Debug($"PinFocusedItem container={_containerHandle} index={index}");
		}
		_isFocusPinned = true;
		_pinnedIndex = index;
	}

	/// <summary>
	/// Unpins the focused item.
	/// </summary>
	internal void UnpinFocusedItem()
	{
		if (this.Log().IsEnabled(LogLevel.Debug))
		{
			this.Log().Debug($"UnpinFocusedItem container={_containerHandle} wasIndex={_pinnedIndex}");
		}
		_isFocusPinned = false;
		_pinnedIndex = null;
	}

	public void Dispose()
	{
		if (!IsDisposed)
		{
			if (this.Log().IsEnabled(LogLevel.Debug))
			{
				this.Log().Debug($"Dispose container={_containerHandle}");
			}
			_state.Dispose();
			UnpinFocusedItem();
			NativeMethods.UnregisterVirtualizedContainer(_containerHandle, _generation);
		}
	}

	private static partial class NativeMethods
	{
		[JSImport("globalThis.Uno.UI.Runtime.Skia.SemanticElements.registerVirtualizedContainer")]
		internal static partial int RegisterVirtualizedContainer(IntPtr containerHandle, string role, string label, bool multiselectable);

		[JSImport("globalThis.Uno.UI.Runtime.Skia.SemanticElements.addVirtualizedItem")]
		internal static partial void AddVirtualizedItem(IntPtr containerHandle, IntPtr itemHandle, int index, int totalCount, float x, float y, float width, float height, string role, string label, int generation, bool disabled, bool selected);

		[JSImport("globalThis.Uno.UI.Runtime.Skia.SemanticElements.removeVirtualizedItem")]
		internal static partial void RemoveVirtualizedItem(IntPtr itemHandle, IntPtr containerHandle, int generation);

		[JSImport("globalThis.Uno.UI.Runtime.Skia.SemanticElements.updateVirtualizedItemCount")]
		internal static partial void UpdateVirtualizedItemCount(IntPtr containerHandle, int totalCount, int generation);

		[JSImport("globalThis.Uno.UI.Runtime.Skia.SemanticElements.unregisterVirtualizedContainer")]
		internal static partial void UnregisterVirtualizedContainer(IntPtr containerHandle, int generation);
	}
}
