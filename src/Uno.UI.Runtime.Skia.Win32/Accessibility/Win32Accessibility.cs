#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Uno.Foundation.Logging;

namespace Uno.UI.Runtime.Skia.Win32;

/// <summary>
/// Manages the Win32 UIAutomation provider tree for Skia-rendered Uno applications.
/// Per top-level <see cref="Microsoft.UI.Xaml.Window"/>: creates UIA providers lazily
/// for elements that have automation peers so Narrator / other screen readers can
/// navigate the tree. The UIA tree follows the automation peer tree (which flattens
/// layout-only elements) rather than the raw visual tree.
/// </summary>
internal sealed class Win32Accessibility : SkiaAccessibilityBase
{
	private readonly nint _hwnd;
	private readonly DispatcherQueue _dispatcherQueue;
	private readonly Func<IRawElementProviderSimple, int> _disconnectProvider;
	private Win32RawElementProvider _rootProvider;
	private readonly Win32SyntheticPaneProvider _outerPane;
	private readonly Win32SyntheticPaneProvider _innerPane;
	private readonly ConditionalWeakTable<UIElement, Win32RawElementProvider> _providers = new();
	private readonly ConditionalWeakTable<AutomationPeer, Win32RawElementProvider> _peerProviders = new();
	// Index lifetimes without retaining data peers for as long as their shared owner.
	private readonly ConditionalWeakTable<UIElement, ConditionalWeakTable<Win32RawElementProvider, Win32RawElementProvider>> _providersByOwner = new();
	private readonly ConditionalWeakTable<AutomationPeer, ConditionalWeakTable<Win32RawElementProvider, Win32RawElementProvider>> _materializedDescendants = new();
	private readonly HashSet<Win32RawElementProvider> _pendingStructureChanges = new();
	private bool _structureChangeFlushQueued;

	internal Win32RawElementProvider? RootProvider => _rootProvider;

	/// <summary>
	/// The outer synthetic pane — represents WinAppSDK's DesktopChildSiteBridge
	/// in the UIA tree. Sits between the HWND root and the inner pane.
	/// </summary>
	internal Win32SyntheticPaneProvider OuterPane => _outerPane;

	/// <summary>
	/// The inner synthetic pane — represents WinAppSDK's content-island host.
	/// Its children resolve to the user's Xaml content (whatever the HWND
	/// root's child-walk would have returned before the pane synthesis).
	/// </summary>
	internal Win32SyntheticPaneProvider InnerPane => _innerPane;

	internal Win32Accessibility(nint hwnd, UIElement rootElement, DispatcherQueue dispatcherQueue,
		Func<IRawElementProviderSimple, int>? disconnectProvider = null)
	{
		_hwnd = hwnd;
		_dispatcherQueue = dispatcherQueue;
		_disconnectProvider = disconnectProvider ?? Win32UIAutomationInterop.UiaDisconnectProvider;

		if (this.Log().IsEnabled(LogLevel.Debug))
		{
			this.Log().Debug($"[UIA] Win32Accessibility initialized for window 0x{hwnd:X}");
		}

		// Create root provider only; child providers are created lazily during navigation.
		var rootPeer = rootElement.GetOrCreateAutomationPeer()?.ResolveProviderPeer(resolveEventsSource: true);
		_rootProvider = new Win32RawElementProvider(rootElement, _hwnd, isRoot: true, this, rootPeer);
		TrackProvider(_rootProvider);
		_providers.AddOrUpdate(rootElement, _rootProvider);
		if (rootPeer is not null)
		{
			_peerProviders.AddOrUpdate(rootPeer, _rootProvider);
		}

		// Synthesize two intermediate pane providers (outer + inner) so the UIA
		// tree matches WinAppSDK exactly: window → pane → pane → user content.
		// The outer pane sits directly under the HWND root; the inner pane sits
		// under the outer pane and forwards child queries to the root's normal
		// peer-tree walk (via GetFirstChildCore / GetLastChildCore).
		// Parents/children are resolved via delegates so the two panes can
		// reference each other without a construction-order cycle.
		_outerPane = new Win32SyntheticPaneProvider(
			hwnd: _hwnd,
			accessibility: this,
			parentResolver: () => _rootProvider,
			firstChildResolver: () => _innerPane,
			lastChildResolver: () => _innerPane,
			debugTag: "outer");

		_innerPane = new Win32SyntheticPaneProvider(
			hwnd: _hwnd,
			accessibility: this,
			parentResolver: () => _outerPane,
			firstChildResolver: () => _rootProvider.GetFirstChildCore(),
			lastChildResolver: () => _rootProvider.GetLastChildCore(),
			debugTag: "inner");

		if (this.Log().IsEnabled(LogLevel.Information))
		{
			this.Log().Info(
				$"[UIA] Root provider created: element={rootElement.GetType().Name}, " +
				$"peer={rootPeer?.GetType().Name ?? "NULL"}, " +
				$"children count={rootElement.GetChildren().Count}, " +
				$"window=0x{_hwnd:X}");
		}
	}

	public override bool IsAccessibilityEnabled => !IsDisposed && _hwnd != nint.Zero;

	// ──────────────────────────────────────────────────────────────
	//  Announcements — override base to dispatch at the UIA layer
	//  (base's debouncing/throttling still runs; AnnounceOnPlatform
	//  implements the actual raise.)
	// ──────────────────────────────────────────────────────────────

	protected override void AnnounceOnPlatform(string text, bool assertive)
	{
		if (!IsAccessibilityEnabled)
		{
			return;
		}

		var processing = assertive
			? Win32UIAutomationInterop.AutomationNotificationProcessing_ImportantMostRecent
			: Win32UIAutomationInterop.AutomationNotificationProcessing_CurrentThenMostRecent;

		try
		{
			_ = Win32UIAutomationInterop.UiaRaiseNotificationEvent(
				_rootProvider,
				Win32UIAutomationInterop.AutomationNotificationKind_Other,
				processing,
				text,
				"UnoAnnouncement");
		}
		catch (Exception ex)
		{
			if (this.Log().IsEnabled(LogLevel.Debug))
			{
				this.Log().Debug($"[UIA] AnnounceOnPlatform failed: {ex.Message}");
			}
		}
	}

	// ──────────────────────────────────────────────────────────────
	//  Provider management
	// ──────────────────────────────────────────────────────────────

	/// <summary>
	/// Gets or lazily creates a UIA provider for the given element.
	/// Only creates providers for elements that have automation peers.
	/// </summary>
	internal Win32RawElementProvider? GetOrCreateProvider(UIElement element)
	{
		if (!IsAccessibilityEnabled)
		{
			return null;
		}

		if (_providers.TryGetValue(element, out var existing))
		{
			if (existing.HasCurrentPeer)
			{
				TrackDeclaredAncestry(existing);
				return existing;
			}
		}

		var peer = element.GetOrCreateAutomationPeer();
		if (peer is null)
		{
			if (this.Log().IsEnabled(LogLevel.Trace))
			{
				this.Log().Trace($"[UIA] GetOrCreateProvider: No automation peer for {element.GetType().Name}");
			}
			return null;
		}

		return GetOrCreateProviderForResolvedPeer(peer.ResolveProviderPeer(resolveEventsSource: true));
	}

	/// <summary>
	/// Resolves an <see cref="AutomationPeer"/> to its corresponding UIA provider.
	/// </summary>
	internal Win32RawElementProvider? GetProviderForPeer(AutomationPeer peer, bool resolveEventsSource = false)
	{
		return GetOrCreateProviderForResolvedPeer(peer.ResolveProviderPeer(resolveEventsSource));
	}

	internal Win32RawElementProvider? GetProvider(UIElement element) => GetOrCreateProvider(element);

	private Win32RawElementProvider? GetOrCreateProviderForResolvedPeer(AutomationPeer resolvedPeer)
	{
		if (!IsAccessibilityEnabled)
		{
			return null;
		}

		// Fast path: already have a provider keyed by this exact peer.
		if (_peerProviders.TryGetValue(resolvedPeer, out var existingByPeer))
		{
			if (existingByPeer.IsRetiredLogicalChild && (!CanRepublishLogicalPeer(resolvedPeer) || !IsAccessibilityEnabled))
			{
				return null;
			}
			if (existingByPeer.HasCurrentPeer)
			{
				TrackDeclaredAncestry(existingByPeer);
				return existingByPeer;
			}
			DisconnectProvider(existingByPeer);
		}

		if (!resolvedPeer.TryGetProviderOwner(out var element))
		{
			if (this.Log().IsEnabled(LogLevel.Debug))
			{
				this.Log().Debug($"[UIA] GetProviderForPeer: Could not resolve owner for {resolvedPeer.GetType().Name}");
			}
			return null;
		}

		var canonicalPeer = element.GetOrCreateAutomationPeer()?.ResolveProviderPeer(resolveEventsSource: true) ?? resolvedPeer;

		if (ReferenceEquals(resolvedPeer, canonicalPeer))
		{
			// Normal path: peer is the canonical peer for its owner element.
			if (!canonicalPeer.TryGetProviderOwner(out element))
			{
				if (this.Log().IsEnabled(LogLevel.Debug))
				{
					this.Log().Debug($"[UIA] GetProviderForPeer: Canonical owner resolution failed for {canonicalPeer.GetType().Name}");
				}
				return null;
			}

			if (_providers.TryGetValue(element, out var existingByElement))
			{
				if (existingByElement.HasCurrentPeer && existingByElement.RepresentsPeer(canonicalPeer))
				{
					_peerProviders.AddOrUpdate(canonicalPeer, existingByElement);
					return existingByElement;
				}
				DisconnectProvider(existingByElement);
			}

			if (_peerProviders.TryGetValue(canonicalPeer, out existingByPeer) && existingByPeer.HasCurrentPeer)
			{
				_providers.AddOrUpdate(element, existingByPeer);
				return existingByPeer;
			}

			var isRoot = ReferenceEquals(element, _rootProvider.Owner);
			var provider = new Win32RawElementProvider(element, _hwnd, isRoot, this, canonicalPeer);
			TrackProvider(provider);
			if (isRoot)
			{
				_rootProvider = provider;
			}
			_providers.AddOrUpdate(element, provider);
			_peerProviders.AddOrUpdate(canonicalPeer, provider);

			if (this.Log().IsEnabled(LogLevel.Debug))
			{
				this.Log().Debug($"[UIA] Created provider for {provider.DescribeElement()} (peer={canonicalPeer.GetType().Name})");
			}

			return provider;
		}
		else
		{
			// Virtual peer: shares its UIElement owner with other peers (e.g.,
			// DataGridItemAutomationPeer whose Owner is the DataGrid, not the row).
			// Create a provider keyed by this specific peer. Do NOT store in
			// _providers since the element is shared with the canonical peer.
			var provider = new Win32RawElementProvider(element, _hwnd, isRoot: false, this, resolvedPeer, isVirtualPeer: true);
			TrackProvider(provider);
			_peerProviders.AddOrUpdate(resolvedPeer, provider);

			if (this.Log().IsEnabled(LogLevel.Debug))
			{
				this.Log().Debug($"[UIA] Created virtual provider for {provider.DescribeElement()} (peer={resolvedPeer.GetType().Name})");
			}

			return provider;
		}
	}

	private void TrackProvider(Win32RawElementProvider provider)
	{
		_providersByOwner.GetOrCreateValue(provider.Owner).AddOrUpdate(provider, provider);
		TrackDeclaredAncestry(provider);
	}

	private void TrackDeclaredAncestry(Win32RawElementProvider provider)
	{
		foreach (var ancestor in provider.DeclaredAncestors)
		{
			if (ancestor.Peer.TryGetTarget(out var parent))
			{
				_materializedDescendants.GetOrCreateValue(parent).AddOrUpdate(provider, provider);
			}
		}
	}

	internal bool IsMaterializedPeerCurrent(AutomationPeer peer) =>
		!_peerProviders.TryGetValue(peer, out var provider) || provider.HasCurrentOwner;

	internal bool IsLogicalPeer(AutomationPeer peer)
	{
		if (_peerProviders.TryGetValue(peer, out var provider))
		{
			return provider.IsVirtualPeer;
		}
		return peer.TryGetProviderOwner(out var owner)
			&& owner.GetOrCreateAutomationPeer()?.ResolveProviderPeer(resolveEventsSource: true) is { } canonical
			&& !ReferenceEquals(peer, canonical);
	}

	private bool CanRepublishLogicalPeer(AutomationPeer peer)
	{
		var candidates = new Stack<(AutomationPeer Peer, AutomationPeer Parent)>();
		var visited = new HashSet<AutomationPeer>(ReferenceEqualityComparer.Instance) { peer };
		var current = peer;
		// A missing native provider is not a declaration boundary.
		while (current.GetParent()?.ResolveProviderPeer(resolveEventsSource: true) is { } parent)
		{
			if (!visited.Add(parent))
			{
				if (this.Log().IsEnabled(LogLevel.Warning))
				{
					this.Log().Warn("Cannot republish a logical automation peer with a cyclic parent chain.");
				}
				return false;
			}
			candidates.Push((current, parent));
			current = parent;
		}

		if (candidates.Count == 0 || (_peerProviders.TryGetValue(current, out var rootProvider) && !rootProvider.HasCurrentPeer))
		{
			return false;
		}

		foreach (var (candidate, parent) in candidates)
		{
			if (!ReferenceEquals(parent, candidate.GetParent()?.ResolveProviderPeer(resolveEventsSource: true)))
			{
				return false;
			}
			var children = _peerProviders.TryGetValue(parent, out var parentProvider) && parentProvider.HasCurrentPeer
				? parentProvider.GetAutomationChildren()
				: parent.GetChildren();
			var found = false;
			if (children is not null)
			{
				for (var i = 0; i < children.Count; i++)
				{
					if (ReferenceEquals(children[i]?.ResolveProviderPeer(resolveEventsSource: true), candidate))
					{
						found = true;
						break;
					}
				}
			}
			if (!found || !ReferenceEquals(parent, candidate.GetParent()?.ResolveProviderPeer(resolveEventsSource: true)))
			{
				return false;
			}
			if (!ReferenceEquals(candidate, peer) && GetOrCreateProviderForResolvedPeer(candidate) is null)
			{
				return false;
			}
		}
		foreach (var (candidate, parent) in candidates)
		{
			if (!ReferenceEquals(parent, candidate.GetParent()?.ResolveProviderPeer(resolveEventsSource: true)))
			{
				return false;
			}
		}
		return IsAccessibilityEnabled;
	}

	internal void InvalidateChildren(Win32RawElementProvider provider)
	{
		provider.ClearChildrenCache();
		if (provider.RepresentedPeer is { } peer)
		{
			InvalidatePeerChildren(peer);
		}
	}

	private void InvalidatePeerChildren(AutomationPeer peer)
	{
		var retired = new List<Win32RawElementProvider>();
		var visited = new HashSet<Win32RawElementProvider>(ReferenceEqualityComparer.Instance);
		CollectInvalidatedChildren(peer, retired, visited, retireDescendants: false);
		var resolved = peer.ResolveProviderPeer(resolveEventsSource: true);
		if (!ReferenceEquals(peer, resolved))
		{
			CollectInvalidatedChildren(resolved, retired, visited, retireDescendants: false);
		}
		DisconnectProviders(retired);
	}

	private void CollectInvalidatedChildren(AutomationPeer peer, List<Win32RawElementProvider> retired,
		HashSet<Win32RawElementProvider> visited, bool retireDescendants)
	{
		// The index includes unmaterialized ancestors, but only materialized descendants.
		if (!_materializedDescendants.TryGetValue(peer, out var children))
		{
			return;
		}
		foreach (var pair in children)
		{
			var child = pair.Value;
			if (!child.IsConnected)
			{
				continue;
			}
			child.ClearChildrenCache();
			if ((retireDescendants || child.DependsOnLogicalDeclaration(peer)) && visited.Add(child))
			{
				child.RetireLogicalChild();
				retired.Add(child);
			}
		}
	}

	private void DisconnectProvider(Win32RawElementProvider provider)
	{
		var providers = new List<Win32RawElementProvider> { provider };
		if (!provider.HasNativeDisconnectStarted && provider.RepresentedPeer is { } peer)
		{
			CollectInvalidatedChildren(peer, providers, new HashSet<Win32RawElementProvider> { provider }, retireDescendants: true);
		}
		DisconnectProviders(providers);
	}

	private void DisconnectProviders(List<Win32RawElementProvider> providers)
	{
		foreach (var provider in providers)
		{
			provider.Disconnect();
		}
		foreach (var provider in providers)
		{
			DisconnectProviderCore(provider);
		}
	}

	private void DisconnectProviderCore(Win32RawElementProvider provider)
	{
		_pendingStructureChanges.Remove(provider);
		if (_providers.TryGetValue(provider.Owner, out var current) && ReferenceEquals(current, provider))
		{
			_providers.Remove(provider.Owner);
		}
		if (!provider.IsRetiredLogicalChild && provider.RepresentedPeer is { } peer
			&& _peerProviders.TryGetValue(peer, out current) && ReferenceEquals(current, provider))
		{
			_peerProviders.Remove(peer);
		}
		if (_providersByOwner.TryGetValue(provider.Owner, out var providers))
		{
			providers.Remove(provider);
		}
		foreach (var ancestor in provider.DeclaredAncestors)
		{
			if (ancestor.Peer.TryGetTarget(out var parent) && _materializedDescendants.TryGetValue(parent, out var children))
			{
				children.Remove(provider);
			}
		}

		if (provider.TryBeginNativeDisconnect())
		{
			var result = _disconnectProvider(provider);
			if (result < 0 && this.Log().IsEnabled(LogLevel.Debug))
			{
				this.Log().Debug($"UiaDisconnectProvider failed with HRESULT 0x{result:X8}.");
			}
		}
	}

	// ──────────────────────────────────────────────────────────────
	//  Tree management — called from router via base.Route*
	// ──────────────────────────────────────────────────────────────

	protected override void OnChildAdded(UIElement parent, UIElement child, int? index)
	{
		// Raise structure changed event on the nearest ancestor that has a provider.
		// Child providers will be lazily created when UIA navigates to them.
		var ancestorProvider = FindNearestAncestorProvider(parent);
		if (ancestorProvider is not null)
		{
			RaiseStructureChanged(ancestorProvider);
		}
	}

	protected override void OnChildRemoved(UIElement parent, UIElement child)
	{
		// Clean up cached providers for the removed subtree
		CleanupProviders(child);

		// Raise structure changed event on the nearest ancestor
		var ancestorProvider = FindNearestAncestorProvider(parent);
		if (ancestorProvider is not null)
		{
			RaiseStructureChanged(ancestorProvider);
		}
	}

	protected override void OnSizeOrOffsetChanged(Visual visual)
	{
		// UIA pulls BoundingRectangle on demand, so we only need to notify
		// clients that the property has changed so they re-query it.
		if (visual is ContainerVisual containerVisual
			&& containerVisual.Owner?.Target is UIElement owner
			&& _providers.TryGetValue(owner, out var provider))
		{
			try
			{
				_ = Win32UIAutomationInterop.UiaRaiseAutomationPropertyChangedEvent(
					provider,
					Win32UIAutomationInterop.UIA_BoundingRectanglePropertyId,
					null,
					null);
			}
			catch (Exception ex)
			{
				if (this.Log().IsEnabled(LogLevel.Debug))
				{
					this.Log().Debug($"Failed to raise BoundingRectangle changed event: {ex.Message}");
				}
			}
		}
	}

	private void CleanupProviders(UIElement element)
	{
		// Use an explicit stack instead of recursion to prevent StackOverflow
		// on deep visual trees when subtrees are removed.
		var stack = new Stack<UIElement>();
		var removedProviders = new List<Win32RawElementProvider>();
		var visited = new HashSet<Win32RawElementProvider>(ReferenceEqualityComparer.Instance);
		stack.Push(element);

		while (stack.Count > 0)
		{
			var current = stack.Pop();

			if (_providersByOwner.TryGetValue(current, out var providers))
			{
				_providersByOwner.Remove(current);
				foreach (var pair in providers)
				{
					if (visited.Add(pair.Value))
					{
						removedProviders.Add(pair.Value);
					}
				}
			}

			foreach (var child in current.GetChildren())
			{
				stack.Push(child);
			}
		}

		var visualProviderCount = removedProviders.Count;
		for (var i = 0; i < visualProviderCount; i++)
		{
			var provider = removedProviders[i];
			var coveredByAncestor = false;
			foreach (var ancestor in provider.DeclaredAncestors)
			{
				if (ancestor.Peer.TryGetTarget(out var parent)
					&& _peerProviders.TryGetValue(parent, out var parentProvider) && visited.Contains(parentProvider))
				{
					coveredByAncestor = true;
					break;
				}
			}
			if (!coveredByAncestor && provider.RepresentedPeer is { } peer)
			{
				CollectInvalidatedChildren(peer, removedProviders, visited, retireDescendants: true);
			}
		}
		DisconnectProviders(removedProviders);
	}

	private Win32RawElementProvider? FindNearestAncestorProvider(UIElement element)
	{
		UIElement? current = element;
		while (current is not null)
		{
			if (_providers.TryGetValue(current, out var provider))
			{
				return provider;
			}
			current = VisualTreeHelper.GetParent(current) as UIElement;
		}
		return _rootProvider;
	}

	private void RaiseStructureChanged(Win32RawElementProvider provider)
	{
		// Invalidate the children cache so the next navigation rebuilds the list
		provider.InvalidateChildrenCache();

		// Coalesce rapid StructureChanged events into a single deferred dispatch.
		_pendingStructureChanges.Add(provider);
		if (_structureChangeFlushQueued)
		{
			return;
		}

		_structureChangeFlushQueued = true;
		_dispatcherQueue.TryEnqueue(() =>
		{
			_structureChangeFlushQueued = false;

			// Short-circuit the flush if the window was closed while this callback
			// was queued (edge case "Window close during dispatch").
			if (IsDisposed)
			{
				_pendingStructureChanges.Clear();
				return;
			}

			foreach (var pending in _pendingStructureChanges)
			{
				RaiseStructureChangedCore(pending);
			}
			_pendingStructureChanges.Clear();
		});
	}

	private void RaiseStructureChangedCore(Win32RawElementProvider provider)
	{
		try
		{
			_ = Win32UIAutomationInterop.UiaRaiseStructureChangedEvent(
				provider,
				StructureChangeType.ChildrenInvalidated,
				provider.GetRuntimeId());
		}
		catch (Exception ex)
		{
			if (this.Log().IsEnabled(LogLevel.Debug))
			{
				this.Log().Debug($"RaiseStructureChanged failed: {ex.Message}");
			}
		}
	}

	// ──────────────────────────────────────────────────────────────
	//  Helpers
	// ──────────────────────────────────────────────────────────────

	/// <summary>
	/// Looks up an existing provider for the given peer without creating one.
	/// Used by event notification methods to avoid eagerly creating providers
	/// for elements that UIA hasn't navigated to yet — creating providers in
	/// event paths registers COM callable wrappers with UIA, which hold strong
	/// references and prevent GC of the underlying UIElements.
	/// </summary>
	private Win32RawElementProvider? FindExistingProviderForPeer(AutomationPeer peer, bool resolveEventsSource = false)
	{
		var resolvedPeer = peer.ResolveProviderPeer(resolveEventsSource);

		if (_peerProviders.TryGetValue(resolvedPeer, out var providerByPeer))
		{
			return providerByPeer;
		}

		if (resolvedPeer.TryGetProviderOwner(out var element) && _providers.TryGetValue(element, out var providerByElement))
		{
			return providerByElement;
		}

		return null;
	}

	// ──────────────────────────────────────────────────────────────
	//  Automation peer listener — UIA-style dispatch overrides
	// ──────────────────────────────────────────────────────────────

	public override void NotifyPropertyChangedEvent(AutomationPeer peer, AutomationProperty automationProperty, object oldValue, object newValue)
	{
		if (!IsAccessibilityEnabled)
		{
			return;
		}

		var provider = FindExistingProviderForPeer(peer, resolveEventsSource: true);
		if (provider is null)
		{
			return;
		}

		var propertyId = MapAutomationPropertyToUia(automationProperty);
		if (propertyId is null)
		{
			return;
		}

		try
		{
			_ = Win32UIAutomationInterop.UiaRaiseAutomationPropertyChangedEvent(
				provider, propertyId.Value, oldValue, newValue);
		}
		catch (Exception ex)
		{
			if (this.Log().IsEnabled(LogLevel.Debug))
			{
				this.Log().Debug($"NotifyPropertyChangedEvent failed for {automationProperty}: {ex.Message}");
			}
		}
	}

	public override void NotifyInvalidatePeer(AutomationPeer peer)
	{
		if (!IsAccessibilityEnabled)
		{
			return;
		}

		var provider = FindExistingProviderForPeer(peer, resolveEventsSource: true);
		provider?.ClearChildrenCache();
		InvalidatePeerChildren(peer);

		// Revoke logical child generations before any native property event can reenter.
		// InvalidatePeer still does not synthesize a StructureChanged event.
		base.NotifyInvalidatePeer(peer);
	}

	public override void NotifyAutomationEvent(AutomationPeer peer, AutomationEvents eventId)
	{
		if (!IsAccessibilityEnabled)
		{
			return;
		}

		// Only look up existing providers for most events — eagerly creating
		// providers registers COM callable wrappers with UIA that prevent GC.
		// For focus and live region changes, create a provider so Narrator
		// can track focus or announce live region content.
		var provider = FindExistingProviderForPeer(peer, resolveEventsSource: true);
		if (provider is null)
		{
			if (eventId == AutomationEvents.StructureChanged)
			{
				InvalidatePeerChildren(peer);
				return;
			}
			if (eventId is AutomationEvents.AutomationFocusChanged or AutomationEvents.LiveRegionChanged)
			{
				provider = GetProviderForPeer(peer, resolveEventsSource: true);
			}

			if (provider is null)
			{
				return;
			}
		}

		try
		{
			switch (eventId)
			{
				case AutomationEvents.AutomationFocusChanged:
					_ = Win32UIAutomationInterop.UiaRaiseAutomationEvent(
						provider, Win32UIAutomationInterop.UIA_AutomationFocusChangedEventId);
					if (TryGetPeerOwner(peer, out var focusedElement))
					{
						TrackFocusedElement(focusedElement);
					}
					break;
				case AutomationEvents.InvokePatternOnInvoked:
					_ = Win32UIAutomationInterop.UiaRaiseAutomationEvent(
						provider, Win32UIAutomationInterop.UIA_Invoke_InvokedEventId);
					break;
				case AutomationEvents.SelectionPatternOnInvalidated:
					_ = Win32UIAutomationInterop.UiaRaiseAutomationEvent(
						provider, Win32UIAutomationInterop.UIA_Selection_InvalidatedEventId);
					break;
				case AutomationEvents.SelectionItemPatternOnElementSelected:
					_ = Win32UIAutomationInterop.UiaRaiseAutomationEvent(
						provider, Win32UIAutomationInterop.UIA_SelectionItem_ElementSelectedEventId);
					break;
				case AutomationEvents.SelectionItemPatternOnElementAddedToSelection:
					_ = Win32UIAutomationInterop.UiaRaiseAutomationEvent(
						provider, Win32UIAutomationInterop.UIA_SelectionItem_ElementAddedToSelectionEventId);
					break;
				case AutomationEvents.SelectionItemPatternOnElementRemovedFromSelection:
					_ = Win32UIAutomationInterop.UiaRaiseAutomationEvent(
						provider, Win32UIAutomationInterop.UIA_SelectionItem_ElementRemovedFromSelectionEventId);
					break;
				case AutomationEvents.TextPatternOnTextChanged:
					_ = Win32UIAutomationInterop.UiaRaiseAutomationEvent(
						provider, Win32UIAutomationInterop.UIA_Text_TextChangedEventId);
					break;
				case AutomationEvents.TextPatternOnTextSelectionChanged:
					_ = Win32UIAutomationInterop.UiaRaiseAutomationEvent(
						provider, Win32UIAutomationInterop.UIA_Text_TextSelectionChangedEventId);
					break;
				case AutomationEvents.StructureChanged:
					// Drop the cached subtree (cascading to virtual peers) and coalesce
					// the UIA notification on the dispatcher. Deferring rather than
					// raising synchronously avoids UIA re-entering GetChildren while a
					// peer is still computing its children — WCT's
					// DataGridItemAutomationPeer.GetChildrenCore calls
					// OwningRowPeer.InvalidatePeer() from inside that very call.
					RaiseStructureChanged(provider);
					break;
				case AutomationEvents.MenuOpened:
					_ = Win32UIAutomationInterop.UiaRaiseAutomationEvent(
						provider, Win32UIAutomationInterop.UIA_MenuOpenedEventId);
					break;
				case AutomationEvents.MenuClosed:
					_ = Win32UIAutomationInterop.UiaRaiseAutomationEvent(
						provider, Win32UIAutomationInterop.UIA_MenuClosedEventId);
					break;
				case AutomationEvents.ToolTipOpened:
					_ = Win32UIAutomationInterop.UiaRaiseAutomationEvent(
						provider, Win32UIAutomationInterop.UIA_ToolTipOpenedEventId);
					break;
				case AutomationEvents.ToolTipClosed:
					_ = Win32UIAutomationInterop.UiaRaiseAutomationEvent(
						provider, Win32UIAutomationInterop.UIA_ToolTipClosedEventId);
					break;
				case AutomationEvents.LiveRegionChanged:
					_ = Win32UIAutomationInterop.UiaRaiseAutomationEvent(
						provider, Win32UIAutomationInterop.UIA_LiveRegionChangedEventId);
					// Also announce the live region text for reliable Narrator delivery
					var label = peer.GetName();
					if (!string.IsNullOrEmpty(label))
					{
						var liveSetting = AutomationProperties.GetLiveSetting(provider.Owner);
						if (liveSetting == AutomationLiveSetting.Assertive)
						{
							AnnounceAssertive(label);
						}
						else
						{
							AnnouncePolite(label);
						}
					}
					break;
			}
		}
		catch (Exception ex)
		{
			if (this.Log().IsEnabled(LogLevel.Debug))
			{
				this.Log().Debug($"NotifyAutomationEvent failed for {eventId}: {ex.Message}");
			}
		}
	}

	public override void NotifyNotificationEvent(AutomationPeer peer, AutomationNotificationKind notificationKind, AutomationNotificationProcessing notificationProcessing, string displayString, string activityId)
	{
		if (!IsAccessibilityEnabled || string.IsNullOrEmpty(displayString))
		{
			return;
		}

		// Use specific provider if available, otherwise fall back to root
		IRawElementProviderSimple? target = _rootProvider;
		if (FindExistingProviderForPeer(peer, resolveEventsSource: true) is { } elementProvider)
		{
			target = elementProvider;
		}

		if (target is null)
		{
			return;
		}

		try
		{
			// Uno enum values match UIA values exactly, so cast directly
			_ = Win32UIAutomationInterop.UiaRaiseNotificationEvent(
				target,
				(int)notificationKind,
				(int)notificationProcessing,
				displayString,
				activityId);
		}
		catch (Exception ex)
		{
			if (this.Log().IsEnabled(LogLevel.Debug))
			{
				this.Log().Debug($"NotifyNotificationEvent failed: {ex.Message}");
			}
		}
	}

	// ──────────────────────────────────────────────────────────────
	//  Abstract no-op overrides — Win32 dispatches at the UIA layer
	//  via NotifyPropertyChangedEvent / NotifyAutomationEvent, not via
	//  the per-handle UpdateXxx methods used by the macOS path.
	// ──────────────────────────────────────────────────────────────

	protected override void UpdateName(nint handle, AutomationPeer peer, string? label) { }
	protected override void UpdateToggleState(nint handle, AutomationPeer peer, ToggleState newState) { }
	protected override void UpdateRangeValue(nint handle, AutomationPeer peer, double value) { }
	protected override void UpdateRangeBounds(nint handle, double min, double max) { }
	protected override void UpdateTextValue(nint handle, string? value) { }
	protected override void UpdateExpandCollapseState(nint handle, bool isExpanded) { }
	protected override void UpdateEnabled(nint handle, bool enabled) { }
	protected override void UpdateSelected(nint handle, bool selected) { }
	protected override void UpdateHelpText(nint handle, string? helpText) { }
	protected override void UpdateHeadingLevel(nint handle, int level) { }
	protected override void UpdateLandmark(nint handle, string? landmarkRole) { }
	protected override void UpdateIsReadOnly(nint handle, bool isReadOnly) { }
	protected override void UpdateFocusable(nint handle, bool focusable) { }
	protected override void UpdateIsOffscreen(nint handle, bool isOffscreen) { }
	protected override void SetNativeFocus(nint handle) { }
	protected override void OnNativeStructureChanged() { }

	// Forwarded by Win32RawElementProvider.AdviseEventAdded/Removed — currently a no-op
	// because UIA doesn't require explicit subscription management here.
	internal void OnAdviseEventAdded(int eventId, int[]? propertyIds) { }
	internal void OnAdviseEventRemoved(int eventId, int[]? propertyIds) { }

	// ──────────────────────────────────────────────────────────────
	//  Disposal — per-window provider cleanup
	// ──────────────────────────────────────────────────────────────

	protected override void DisposeCore()
	{
		if (this.Log().IsEnabled(LogLevel.Debug))
		{
			this.Log().Debug($"[UIA] Win32Accessibility disposing for window 0x{_hwnd:X}");
		}

		// Include peer-only providers, which can share their owner with canonical peers.
		var providers = new List<Win32RawElementProvider>();
		foreach (var pair in _providersByOwner)
		{
			foreach (var provider in pair.Value)
			{
				providers.Add(provider.Value);
			}
		}
		_providersByOwner.Clear();
		DisconnectProviders(providers);

		// Synthetic panes are not part of _providers (they wrap no UIElement),
		// so they must be disconnected separately.
		foreach (var pane in new IRawElementProviderSimple?[] { _outerPane, _innerPane })
		{
			if (pane is null)
			{
				continue;
			}
			try
			{
				_ = _disconnectProvider(pane);
			}
			catch (Exception ex)
			{
				if (this.Log().IsEnabled(LogLevel.Debug))
				{
					this.Log().Debug($"[UIA] UiaDisconnectProvider failed for synthetic pane during dispose: {ex.Message}");
				}
			}
		}

		_providers.Clear();
		_peerProviders.Clear();
		_materializedDescendants.Clear();
		_pendingStructureChanges.Clear();
	}

	// ──────────────────────────────────────────────────────────────
	//  Property mapping
	// ──────────────────────────────────────────────────────────────

	private static int? MapAutomationPropertyToUia(AutomationProperty property)
	{
		if (ReferenceEquals(property, AutomationElementIdentifiers.NameProperty))
		{
			return Win32UIAutomationInterop.UIA_NamePropertyId;
		}
		if (ReferenceEquals(property, TogglePatternIdentifiers.ToggleStateProperty))
		{
			return Win32UIAutomationInterop.UIA_ToggleToggleStatePropertyId;
		}
		if (ReferenceEquals(property, RangeValuePatternIdentifiers.ValueProperty))
		{
			return Win32UIAutomationInterop.UIA_RangeValueValuePropertyId;
		}
		if (ReferenceEquals(property, ValuePatternIdentifiers.ValueProperty))
		{
			return Win32UIAutomationInterop.UIA_ValueValuePropertyId;
		}
		if (ReferenceEquals(property, ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty))
		{
			return Win32UIAutomationInterop.UIA_ExpandCollapseExpandCollapseStatePropertyId;
		}
		if (ReferenceEquals(property, AutomationElementIdentifiers.IsEnabledProperty))
		{
			return Win32UIAutomationInterop.UIA_IsEnabledPropertyId;
		}
		if (ReferenceEquals(property, AutomationElementIdentifiers.HelpTextProperty))
		{
			return Win32UIAutomationInterop.UIA_HelpTextPropertyId;
		}
		if (ReferenceEquals(property, AutomationElementIdentifiers.HeadingLevelProperty))
		{
			return Win32UIAutomationInterop.UIA_HeadingLevelPropertyId;
		}
		if (ReferenceEquals(property, SelectionItemPatternIdentifiers.IsSelectedProperty))
		{
			return Win32UIAutomationInterop.UIA_SelectionItemIsSelectedPropertyId;
		}
		if (ReferenceEquals(property, AutomationElementIdentifiers.LandmarkTypeProperty))
		{
			return Win32UIAutomationInterop.UIA_LandmarkTypePropertyId;
		}
		if (ReferenceEquals(property, AutomationElementIdentifiers.LiveSettingProperty))
		{
			return Win32UIAutomationInterop.UIA_LiveSettingPropertyId;
		}
		if (ReferenceEquals(property, AutomationElementIdentifiers.IsOffscreenProperty))
		{
			return Win32UIAutomationInterop.UIA_IsOffscreenPropertyId;
		}
		if (ReferenceEquals(property, AutomationElementIdentifiers.AcceleratorKeyProperty))
		{
			return Win32UIAutomationInterop.UIA_AcceleratorKeyPropertyId;
		}
		if (ReferenceEquals(property, AutomationElementIdentifiers.AccessKeyProperty))
		{
			return Win32UIAutomationInterop.UIA_AccessKeyPropertyId;
		}
		if (ReferenceEquals(property, AutomationElementIdentifiers.ItemStatusProperty))
		{
			return Win32UIAutomationInterop.UIA_ItemStatusPropertyId;
		}
		if (ReferenceEquals(property, AutomationElementIdentifiers.ItemTypeProperty))
		{
			return Win32UIAutomationInterop.UIA_ItemTypePropertyId;
		}

		return null;
	}
}
