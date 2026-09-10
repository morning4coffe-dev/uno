#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Uno.Extensions;
using Uno.UI.Accessibility;
using Uno.UI.Dispatching;

namespace Uno.UI.Runtime.Skia;

internal partial class WebAssemblyAccessibility
{
	private readonly HashSet<UIElement> _pendingItemNameRefresh = new();

	private bool IsAttachedToSemanticRoot(UIElement element)
	{
		for (UIElement? current = element; current is not null; current = current.GetParent() as UIElement)
		{
			if (current.Visibility == Visibility.Collapsed)
			{
				return false;
			}
			if (current.Visual.Handle == _rootElementHandle)
			{
				return true;
			}
		}
		return FindAncestorDialog(element) is { } dialog && dialog._popup.IsOpen &&
			element.IsInLiveTree && element.XamlRoot?.VisualTree.RootElement?.Visual.Handle == _rootElementHandle;
	}

	private void ReconcileBodyTextMembership(UIElement element)
	{
		if (element is not TextBlock || _isCreatingAOM || !IsAttachedToSemanticRoot(element))
		{
			return;
		}

		var handle = element.Visual.Handle;
		var exists = _semanticParentMap.TryGetValue(handle, out var semanticParent);
		var shouldExist = IsSemanticElement(element);
		if (exists && !shouldExist)
		{
			RemoveSemanticElement(semanticParent, handle);
			_semanticParentMap.Remove(handle);
		}
		else if (!exists && shouldExist && element.GetParent() is UIElement parent)
		{
			OnChildAdded(parent, element, null);
			if (_semanticParentMap.TryGetValue(handle, out semanticParent))
			{
				NativeMethods.MoveSemanticElementBefore(handle, FindFollowingSemanticSibling(element, semanticParent));
			}
		}
	}

	private IntPtr FindFollowingSemanticSibling(UIElement element, IntPtr semanticParent)
	{
		for (var current = element; current.GetParent() is UIElement parent; current = parent)
		{
			bool afterCurrent = false;
			foreach (var sibling in parent.GetChildren())
			{
				if (afterCurrent && FindFirstSemanticChild(sibling, semanticParent) is var handle && handle != IntPtr.Zero)
				{
					return handle;
				}
				afterCurrent |= ReferenceEquals(sibling, current);
			}
			if (parent.Visual.Handle == semanticParent)
			{
				break;
			}
		}
		return IntPtr.Zero;
	}

	private IntPtr FindFirstSemanticChild(UIElement element, IntPtr semanticParent)
	{
		if (_semanticParentMap.TryGetValue(element.Visual.Handle, out var parent))
		{
			return parent == semanticParent ? element.Visual.Handle : IntPtr.Zero;
		}
		foreach (var child in element.GetChildren())
		{
			var handle = FindFirstSemanticChild(child, semanticParent);
			if (handle != IntPtr.Zero)
			{
				return handle;
			}
		}
		return IntPtr.Zero;
	}

	private void RefreshVirtualizedAncestorName(UIElement element)
	{
		if (!IsAttachedToSemanticRoot(element) || IsPeerExcluded(element))
		{
			return;
		}
		for (UIElement? parent = element; parent is not null; parent = parent.GetParent() as UIElement)
		{
			if (IsRealizedVirtualizedItem(parent.Visual.Handle))
			{
				NativeMethods.UpdateAriaLabel(parent.Visual.Handle, GetVirtualizedItemName(parent));
				ReconcileBodyTextSubtree(parent);
				return;
			}
		}
	}

	private static string GetVirtualizedItemName(UIElement item)
	{
		var name = item.GetOrCreateAutomationPeer()?.GetName();
		if (string.IsNullOrWhiteSpace(name) && item is FrameworkElement root)
		{
			name = TemplateAutomationText.GetName(root);
			if (string.IsNullOrWhiteSpace(name))
			{
				name = GetVisualTreeAutomationText(root);
			}
		}
		if (string.IsNullOrWhiteSpace(name) &&
			item is ContentControl { Content: not UIElement and not null } contentControl)
		{
			var content = contentControl.Content;
			var contentText = content.ToString();
			var typeName = content.GetType().ToString();
			if (!string.IsNullOrWhiteSpace(contentText) &&
				!string.Equals(contentText, typeName, StringComparison.Ordinal))
			{
				name = contentText;
			}
		}
		return name ?? string.Empty;
	}

	private static string? GetVisualTreeAutomationText(FrameworkElement root)
	{
		StringBuilder? text = null;
		Append(root, true);
		return text?.ToString();

		void Append(UIElement element, bool isRoot)
		{
			if (element.Visibility == Visibility.Collapsed ||
				(!isRoot && element is ButtonBase or TextBox or RangeBase or Selector) ||
				(!isRoot && element is Control { IsTabStop: true }))
			{
				return;
			}

			if (AutomationProperties.GetAccessibilityView(element) != AccessibilityView.Raw)
			{
				var name = AutomationProperties.GetName(element);
				if (string.IsNullOrWhiteSpace(name) && element is TextBlock textBlock)
				{
					name = textBlock.Text;
				}
				if (!string.IsNullOrWhiteSpace(name))
				{
					text ??= new StringBuilder();
					if (text.Length > 0)
					{
						text.Append(", ");
					}
					text.Append(name);
					return;
				}
			}

			for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
			{
				if (VisualTreeHelper.GetChild(element, i) is UIElement child)
				{
					Append(child, false);
				}
			}
		}
	}

	private void QueueVirtualizedAncestorNameRefresh(UIElement element)
	{
		for (UIElement? current = element; current is not null; current = current.GetParent() as UIElement)
		{
			if (IsRealizedVirtualizedItem(current.Visual.Handle))
			{
				if (_pendingItemNameRefresh.Add(current))
				{
					var item = current;
					NativeDispatcher.Main.Enqueue(() =>
					{
						_pendingItemNameRefresh.Remove(item);
						RefreshVirtualizedAncestorName(item);
					});
				}
				return;
			}
		}
	}

	private void ReconcileBodyTextSubtree(UIElement item)
	{
		foreach (var child in item.GetChildren())
		{
			if (child is TextBlock)
			{
				ReconcileBodyTextMembership(child);
			}
			else
			{
				ReconcileBodyTextSubtree(child);
			}
		}
	}

	private static partial class NativeMethods
	{
		[JSImport("globalThis.Uno.UI.Runtime.Skia.Accessibility.moveSemanticElementBefore")]
		internal static partial void MoveSemanticElementBefore(IntPtr handle, IntPtr nextHandle);

		[JSImport("globalThis.Uno.UI.Runtime.Skia.Accessibility.reparentSemanticElement")]
		internal static partial bool ReparentSemanticElement(IntPtr handle, IntPtr parentHandle, int? index);
	}
}
