#nullable enable

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Uno.Extensions;
using Uno.UI.Dispatching;

namespace Uno.UI.Runtime.Skia;

internal partial class WebAssemblyAccessibility
{
	private static readonly IEqualityComparer<UIElement> _elementIdentity = ReferenceEqualityComparer.Instance;

	private sealed class PeerChildrenState
	{
		internal HashSet<UIElement> ExcludedRoots { get; set; } = new(_elementIdentity);
		internal HashSet<UIElement> AddedRoots { get; } = new(_elementIdentity);
		internal bool Pending { get; set; }
		internal bool HasSnapshot { get; set; }
	}

	private readonly Dictionary<UIElement, PeerChildrenState> _peerChildren = new(_elementIdentity);
	private readonly Dictionary<UIElement, int> _peerExcludedRoots = new(_elementIdentity);
	private bool _isReconcilingPeerChildren;

	private static bool TryGetOrdinaryElementPeer(UIElement owner, [NotNullWhen(true)] out FrameworkElementAutomationPeer? peer)
	{
		// Their peer children are data items, not the bounded realized visual frontier.
		peer = owner is not (ListViewBase or ItemsRepeater)
			&& owner.GetOrCreateAutomationPeer() is FrameworkElementAutomationPeer candidate
			&& ReferenceEquals(candidate.Owner, owner)
				? candidate
				: null;
		return peer is not null;
	}

	private static bool IsVisualDescendantOf(UIElement child, UIElement ancestor)
	{
		for (var parent = child.GetParent() as UIElement; parent is not null; parent = parent.GetParent() as UIElement)
		{
			if (ReferenceEquals(parent, ancestor))
			{
				return true;
			}
		}
		return false;
	}

	private bool IsPeerExcluded(UIElement element)
	{
		for (UIElement? current = element; current is not null; current = current.GetParent() as UIElement)
		{
			if (_peerExcludedRoots.ContainsKey(current))
			{
				return true;
			}
		}
		return false;
	}

	private static HashSet<UIElement> GetPeerExcludedRoots(UIElement owner, FrameworkElementAutomationPeer peer)
	{
		var ordinary = peer.GetAutomationPeersForChildrenOfElement(owner);
		var declared = peer.GetChildren();
		var includedOwners = new HashSet<UIElement>(_elementIdentity);
		if (declared is not null)
		{
			foreach (var childPeer in declared)
			{
				// An ownerless/data peer or parent-owned proxy cannot identify an omitted visual branch.
				if (childPeer is not FrameworkElementAutomationPeer { Owner: { } childOwner }
					|| !IsVisualDescendantOf(childOwner, owner))
				{
					return new(_elementIdentity);
				}
				includedOwners.Add(childOwner);
			}
		}

		var candidates = new Dictionary<UIElement, int>(_elementIdentity);
		foreach (var childPeer in ordinary)
		{
			if (childPeer is FrameworkElementAutomationPeer { Owner: { } childOwner }
				&& IsVisualDescendantOf(childOwner, owner))
			{
				candidates.TryGetValue(childOwner, out var count);
				candidates[childOwner] = count + 1;
			}
		}

		var excluded = new HashSet<UIElement>(_elementIdentity);
		foreach (var candidate in candidates)
		{
			if (candidate.Value == 1 && !includedOwners.Contains(candidate.Key)
				&& !includedOwners.Any(included => IsVisualDescendantOf(included, candidate.Key)))
			{
				excluded.Add(candidate.Key);
			}
		}
		return excluded;
	}

	private void ReplacePeerExclusions(PeerChildrenState state, HashSet<UIElement> excluded)
	{
		foreach (var root in state.ExcludedRoots)
		{
			if (!excluded.Contains(root))
			{
				RemovePeerExclusion(root);
			}
		}
		foreach (var root in excluded)
		{
			if (!state.ExcludedRoots.Contains(root))
			{
				_peerExcludedRoots.TryGetValue(root, out var count);
				_peerExcludedRoots[root] = count + 1;
			}
		}
		state.ExcludedRoots = excluded;
		state.HasSnapshot = true;
	}

	private void RemovePeerExclusion(UIElement root)
	{
		if (_peerExcludedRoots.TryGetValue(root, out var count))
		{
			if (count == 1)
			{
				_peerExcludedRoots.Remove(root);
			}
			else
			{
				_peerExcludedRoots[root] = count - 1;
			}
		}
	}

	private void EnsurePeerChildren(UIElement owner)
	{
		if (!TryGetOrdinaryElementPeer(owner, out var peer))
		{
			return;
		}
		if (!_peerChildren.TryGetValue(owner, out var state))
		{
			state = new();
			_peerChildren.Add(owner, state);
		}
		if (!state.HasSnapshot)
		{
			ReplacePeerExclusions(state, GetPeerExcludedRoots(owner, peer));
		}
	}

	private bool QueuePeerChildrenForAncestor(UIElement parent, UIElement? added = null)
	{
		if (_isReconcilingPeerChildren || !IsAttachedToSemanticRoot(parent) || IsPeerExcluded(parent))
		{
			return false;
		}
		var inVirtualizedBranch = false;
		for (UIElement? ancestor = parent; ancestor is not null; ancestor = ancestor.GetParent() as UIElement)
		{
			if (ancestor is (ListViewBase or ItemsRepeater) && IsSemanticElement(ancestor))
			{
				inVirtualizedBranch = true;
				break;
			}
		}
		for (UIElement? current = parent; current is not null; current = current.GetParent() as UIElement)
		{
			if (current is (ListViewBase or ItemsRepeater) && IsSemanticElement(current))
			{
				return false;
			}
			if (TryGetOrdinaryElementPeer(current, out _))
			{
				// Realized-item descendants already have an insertion lifecycle; do not replay it
				// after the option container is registered and change their semantic parent.
				QueuePeerChildren(current, inVirtualizedBranch ? null : added);
				return !inVirtualizedBranch;
			}
		}
		return false;
	}

	private void QueuePeerChildren(AutomationPeer peer)
	{
		if (IsDisposed || !IsAccessibilityEnabled)
		{
			return;
		}
		if (peer is FrameworkElementAutomationPeer { Owner: { } owner }
			&& TryGetOrdinaryElementPeer(owner, out var ownerPeer) && ReferenceEquals(peer, ownerPeer))
		{
			QueuePeerChildren(owner);
		}
	}

	private void QueuePeerChildren(UIElement owner, UIElement? added = null)
	{
		if (IsDisposed || !IsAccessibilityEnabled || !IsAttachedToSemanticRoot(owner) || IsPeerExcluded(owner))
		{
			return;
		}
		if (!_peerChildren.TryGetValue(owner, out var state))
		{
			state = new();
			_peerChildren.Add(owner, state);
		}
		if (added is not null)
		{
			state.AddedRoots.Add(added);
		}
		if (state.Pending)
		{
			return;
		}
		state.Pending = true;
		NativeDispatcher.Main.Enqueue(() =>
		{
			if (IsDisposed || !IsAccessibilityEnabled
				|| !_peerChildren.TryGetValue(owner, out var current) || !ReferenceEquals(current, state))
			{
				return;
			}
			state.Pending = false;
			if (!IsAttachedToSemanticRoot(owner) || IsPeerExcluded(owner))
			{
				return;
			}
			ReconcilePeerChildren(owner, state);
		});
	}

	private void ReconcilePeerChildren(UIElement owner, PeerChildrenState state)
	{
		var previous = state.ExcludedRoots;
		var excluded = TryGetOrdinaryElementPeer(owner, out var peer) ? GetPeerExcludedRoots(owner, peer) : new(_elementIdentity);
		ReplacePeerExclusions(state, excluded);
		var restore = new HashSet<UIElement>(state.AddedRoots, _elementIdentity);
		state.AddedRoots.Clear();
		restore.UnionWith(previous.Where(root => !excluded.Contains(root)));

		var wasReconciling = _isReconcilingPeerChildren;
		_isReconcilingPeerChildren = true;
		try
		{
			foreach (var root in excluded)
			{
				if (!previous.Contains(root) && root.GetParent() is UIElement parent)
				{
					OnChildRemoved(parent, root);
				}
			}
			foreach (var root in restore)
			{
				if (IsAttachedToSemanticRoot(root) && !IsPeerExcluded(root) && root.GetParent() is UIElement parent)
				{
					OnChildAdded(parent, root, null);
					RestorePeerChildOrder(root);
				}
			}
		}
		finally
		{
			_isReconcilingPeerChildren = wasReconciling;
		}
	}

	private void RestorePeerChildOrder(UIElement element)
	{
		if (IsPeerExcluded(element))
		{
			return;
		}
		if (_semanticParentMap.TryGetValue(element.Visual.Handle, out var semanticParent))
		{
			NativeMethods.MoveSemanticElementBefore(element.Visual.Handle, FindFollowingSemanticSibling(element, semanticParent));
			return;
		}
		// A transparent branch can emit several siblings. Place the trailing one first
		// so each preceding sibling has its final anchor, regardless of replay order.
		var children = element.GetChildren();
		for (var index = children.Count - 1; index >= 0; index--)
		{
			RestorePeerChildOrder(children[index]);
		}
	}

	private void ForgetPeerChildren(UIElement owner)
	{
		if (_peerChildren.Remove(owner, out var state))
		{
			foreach (var root in state.ExcludedRoots)
			{
				RemovePeerExclusion(root);
			}
			state.AddedRoots.Clear();
		}
	}

	public override void NotifyInvalidatePeer(AutomationPeer peer)
	{
		base.NotifyInvalidatePeer(peer);
		QueuePeerChildren(peer);
	}
}
