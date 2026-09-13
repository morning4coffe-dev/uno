#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Uno.Extensions;

namespace Uno.UI.Runtime.Skia;

internal sealed class SemanticNameRefreshQueue(
	IReadOnlyDictionary<IntPtr, IntPtr> semanticParents,
	Func<IntPtr, bool> isRealizedItem,
	Func<UIElement, bool> isAttachedAndIncluded)
{
	internal const int MaxAncestorDepth = 16;
	private readonly HashSet<UIElement> _pending = new(ReferenceEqualityComparer.Instance);
	private readonly Queue<UIElement> _order = new();

	internal bool Enqueue(UIElement element)
	{
		if (!isAttachedAndIncluded(element))
		{
			return false;
		}
		var added = false;
		UIElement? current = element;
		for (var depth = 0; current is not null && depth <= MaxAncestorDepth; depth++, current = current.GetParent() as UIElement)
		{
			// Data peers are not the realized visual frontier and must not be queried for item names.
			if (current is ListViewBase or ItemsRepeater)
			{
				break;
			}
			if (isRealizedItem(current.Visual.Handle))
			{
				added |= Add(current);
				break;
			}
			if (semanticParents.ContainsKey(current.Visual.Handle))
			{
				added |= Add(current);
			}
		}
		return added;
	}

	private bool Add(UIElement element)
	{
		if (!_pending.Add(element))
		{
			return false;
		}
		_order.Enqueue(element);
		return true;
	}

	internal bool TryDequeue([NotNullWhen(true)] out UIElement? element)
	{
		while (_order.TryDequeue(out var candidate))
		{
			if (TryClaim(candidate))
			{
				element = candidate;
				return true;
			}
		}
		element = null;
		return false;
	}

	internal bool HasPending => _pending.Count != 0;

	internal void DrainBatch(Action<UIElement> refresh)
	{
		// Reconciliation can enqueue the same owner before its deferred child insertion runs.
		var remaining = _order.Count;
		while (remaining-- > 0 && _order.TryDequeue(out var element))
		{
			if (TryClaim(element))
			{
				refresh(element);
			}
		}
	}

	private bool TryClaim(UIElement element)
		=> _pending.Remove(element) && isAttachedAndIncluded(element) &&
			(semanticParents.ContainsKey(element.Visual.Handle) || isRealizedItem(element.Visual.Handle));

	internal void Remove(UIElement element) => _pending.Remove(element);

	internal void Clear()
	{
		_pending.Clear();
		_order.Clear();
	}
}
