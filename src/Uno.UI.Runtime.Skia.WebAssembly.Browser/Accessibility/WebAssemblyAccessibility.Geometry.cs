#nullable enable

using System;
using Microsoft.UI.Xaml;
using Uno.Extensions;
using Uno.Disposables;
using Uno.UI.Dispatching;
using Windows.Foundation;

namespace Uno.UI.Runtime.Skia;

internal partial class WebAssemblyAccessibility
{
	private IDisposable? _initialGeometrySubscription;

	private void InitializeSemanticGeometry(UIElement root)
	{
		_initialGeometrySubscription?.Dispose();
		_initialGeometrySubscription = null;
		if (root is FrameworkElement frameworkElement)
		{
			IDisposable? subscription = null;
			void Release()
			{
				subscription?.Dispose();
				if (ReferenceEquals(_initialGeometrySubscription, subscription))
				{
					_initialGeometrySubscription = null;
				}
			}
			void OnLayoutUpdated(object? sender, object args)
			{
				Release();
				if (IsAccessibilityEnabled && IsAttachedToSemanticRoot(root))
				{
					UpdateSemanticSubtreeGeometry(root);
				}
			}
			void OnUnloaded(object sender, RoutedEventArgs args) => Release();
			frameworkElement.LayoutUpdated += OnLayoutUpdated;
			frameworkElement.Unloaded += OnUnloaded;
			subscription = Disposable.Create(() =>
			{
				frameworkElement.LayoutUpdated -= OnLayoutUpdated;
				frameworkElement.Unloaded -= OnUnloaded;
			});
			_initialGeometrySubscription = subscription;
		}
		QueueSemanticSubtreeGeometry(root);
	}

	private bool TryGetSemanticParentHandle(IntPtr handle, out IntPtr parentHandle)
	{
		if (_semanticParentMap.TryGetValue(handle, out parentHandle))
		{
			return true;
		}
		foreach (var region in _virtualizedRegions)
		{
			if (region.ContainsRealizedHandle(handle))
			{
				parentHandle = region.ContainerHandle;
				return true;
			}
		}
		parentHandle = IntPtr.Zero;
		return false;
	}

	private static void UpdateSemanticElementGeometry(IntPtr handle, UIElement element, IntPtr parentHandle)
	{
		var parent = FindUIElementByHandle(element, parentHandle);
		var rect = new Rect(0, 0, element.Visual.Size.X, element.Visual.Size.Y);
		var transformed = UIElement.GetTransform(element, parent).Transform(rect);
		NativeMethods.UpdateSemanticElementPositioning(handle, (float)transformed.Width, (float)transformed.Height,
			(float)transformed.X, (float)transformed.Y);
	}

	private void UpdateNearestSemanticDescendantsGeometry(UIElement element)
	{
		foreach (var child in element.GetChildren())
		{
			if (IsPeerExcluded(child))
			{
				continue;
			}
			if (TryGetSemanticParentHandle(child.Visual.Handle, out var parent))
			{
				UpdateSemanticElementGeometry(child.Visual.Handle, child, parent);
			}
			else
			{
				UpdateNearestSemanticDescendantsGeometry(child);
			}
		}
	}

	private void QueueSemanticSubtreeGeometry(UIElement element)
	{
		NativeDispatcher.Main.Enqueue(() =>
		{
			if (!IsDisposed && IsAccessibilityEnabled && IsAttachedToSemanticRoot(element) && !IsPeerExcluded(element))
			{
				UpdateSemanticSubtreeGeometry(element);
			}
		});
	}

	private void UpdateSemanticSubtreeGeometry(UIElement element)
	{
		if (IsPeerExcluded(element))
		{
			return;
		}
		if (TryGetSemanticParentHandle(element.Visual.Handle, out var parent))
		{
			UpdateSemanticElementGeometry(element.Visual.Handle, element, parent);
		}
		foreach (var child in element.GetChildren())
		{
			UpdateSemanticSubtreeGeometry(child);
		}
	}
}
