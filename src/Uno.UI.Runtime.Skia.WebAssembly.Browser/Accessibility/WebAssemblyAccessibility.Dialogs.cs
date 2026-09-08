#nullable enable

using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Uno.Disposables;
using Uno.Extensions;
using Uno.UI.Accessibility;
using Uno.UI.Dispatching;

namespace Uno.UI.Runtime.Skia;

internal partial class WebAssemblyAccessibility
{
	private readonly Dictionary<ContentDialog, ModalDialogRegistration> _modalRegistrations = new();

	private static ContentDialog? FindAncestorDialog(UIElement element)
	{
		for (UIElement? current = element; current is not null; current = current.GetParent() as UIElement)
		{
			if (current is ContentDialog dialog)
			{
				return dialog;
			}
		}
		return null;
	}

	private static string ResolveDialogName(ContentDialog dialog)
	{
		var name = AutomationProperties.GetName(dialog);
		if (!string.IsNullOrWhiteSpace(name))
		{
			return name;
		}
		if (AutomationProperties.GetLabeledBy(dialog) is UIElement label &&
			label.GetOrCreateAutomationPeer()?.GetName() is { Length: > 0 } labelName)
		{
			return labelName;
		}
		return dialog.Title as string
			?? (dialog.GetTemplateChild("Title") is FrameworkElement title ? TemplateAutomationText.GetName(title) : null)
			?? string.Empty;
	}

	private void TryRegisterModalDialog(UIElement element)
	{
		if (element is ContentDialog dialog && !_modalRegistrations.ContainsKey(dialog))
		{
			var registration = new ModalDialogRegistration(this, dialog);
			_modalRegistrations.Add(dialog, registration);
			registration.Attach();
		}
	}

	private void TryUnregisterModalDialog(UIElement element)
	{
		if (element is ContentDialog dialog && _modalRegistrations.Remove(dialog, out var registration))
		{
			registration.Dispose();
		}
	}

	private void QueueModalRefresh(UIElement element)
	{
		if (FindAncestorDialog(element) is { } dialog && _modalRegistrations.TryGetValue(dialog, out var registration))
		{
			registration.QueueRefresh();
		}
	}

	private void QueueModalLabelRefresh(UIElement element)
	{
		foreach (var registration in _modalRegistrations.Values)
		{
			if (registration.UsesLabel(element))
			{
				registration.QueueRefresh();
			}
		}
	}

	private IntPtr FindSemanticText(UIElement element)
	{
		if (element is TextBlock && HasSemanticElement(element.Visual.Handle))
		{
			return element.Visual.Handle;
		}
		foreach (var child in element.GetChildren())
		{
			var handle = FindSemanticText(child);
			if (handle != IntPtr.Zero)
			{
				return handle;
			}
		}
		return IntPtr.Zero;
	}

	private sealed class ModalDialogRegistration : IDisposable
	{
		private readonly WebAssemblyAccessibility _owner;
		private readonly ContentDialog _dialog;
		private readonly CompositeDisposable _subscriptions = new();
		private readonly IntPtr _triggerHandle;
		private ModalFocusScope? _scope;
		private bool _pending;
		private bool _disposed;

		internal ModalDialogRegistration(WebAssemblyAccessibility owner, ContentDialog dialog)
		{
			_owner = owner;
			_dialog = dialog;
			_triggerHandle = owner._focusSynchronizer?.CurrentFocusedHandle ?? IntPtr.Zero;
		}

		internal void Attach()
		{
			_dialog.Opened += OnOpened;
			_dialog.Closed += OnClosed;
			_dialog.Unloaded += OnUnloaded;
			_subscriptions.Add(Disposable.Create(() =>
			{
				_dialog.Opened -= OnOpened;
				_dialog.Closed -= OnClosed;
				_dialog.Unloaded -= OnUnloaded;
			}));
			foreach (var property in new[] { ContentDialog.TitleProperty, ContentDialog.TitleTemplateProperty,
				ContentControl.ContentProperty, AutomationProperties.NameProperty, AutomationProperties.LabeledByProperty })
			{
				var token = _dialog.RegisterPropertyChangedCallback(property, OnPropertyChanged);
				_subscriptions.Add(Disposable.Create(() => _dialog.UnregisterPropertyChangedCallback(property, token)));
			}
			if (_dialog._popup.IsOpen)
			{
				QueueRefresh();
			}
		}

		private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args) => QueueRefresh();
		private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args) => Deactivate();
		private void OnUnloaded(object sender, RoutedEventArgs args) => _owner.TryUnregisterModalDialog(_dialog);
		private void OnPropertyChanged(DependencyObject sender, DependencyProperty property) => QueueRefresh();

		internal bool UsesLabel(UIElement element) => ReferenceEquals(AutomationProperties.GetLabeledBy(_dialog), element);

		internal void QueueRefresh()
		{
			if (_disposed || _pending)
			{
				return;
			}

			_pending = true;
			NativeDispatcher.Main.Enqueue(() =>
			{
				_pending = false;
				if (!_disposed && _owner.IsAccessibilityEnabled && _dialog._popup.IsOpen)
				{
					Refresh();
				}
			});
		}

		private void Refresh()
		{
			var handle = _dialog.Visual.Handle;
			if (!_owner.HasSemanticElement(handle) && _dialog.GetParent() is UIElement parent)
			{
				_owner.OnChildAdded(parent, _dialog, null);
			}
			if (!_owner.HasSemanticElement(handle))
			{
				return;
			}
			_owner.ReconcileBodyTextSubtree(_dialog);
			NativeMethods.UpdateAriaLabel(handle, ResolveDialogName(_dialog));
			var titleHandle = _dialog.GetTemplateChild("Title") is UIElement titleRoot
				? _owner.FindSemanticText(titleRoot)
				: IntPtr.Zero;
			if (titleHandle != IntPtr.Zero && _dialog.GetTemplateChild("Content") is UIElement contentRoot)
			{
				NativeMethods.MoveSemanticElementBefore(titleHandle, _owner.FindSemanticText(contentRoot));
			}

			var labelHandle = IntPtr.Zero;
			if (string.IsNullOrWhiteSpace(AutomationProperties.GetName(_dialog)))
			{
				if (AutomationProperties.GetLabeledBy(_dialog) is UIElement label)
				{
					if (_owner.HasSemanticElement(label.Visual.Handle))
					{
						labelHandle = label.Visual.Handle;
					}
				}
				else
				{
					labelHandle = titleHandle;
				}
			}
			NativeMethods.UpdateAriaLabelledBy(handle, labelHandle == IntPtr.Zero ? string.Empty : $"uno-semantics-{labelHandle}");

			var children = new List<IntPtr>();
			EnumerateFocusableChildren(_dialog, children);
			if (_scope is not { IsActive: true })
			{
				_scope = new ModalFocusScope(handle, _triggerHandle, children);
				_scope.Activate(_owner.ActiveModalScope);
				_owner.ActiveModalScope = _scope;
				if (_owner._liveRegionManager is { } manager)
				{
					manager.ActiveModalHandle = handle;
				}
			}
			else
			{
				_scope.UpdateChildren(children);
			}
			_owner.QueueSemanticSubtreeGeometry(_dialog);
		}

		private void Deactivate()
		{
			if (_scope is { IsActive: true } scope)
			{
				scope.Deactivate();
				if (ReferenceEquals(_owner.ActiveModalScope, scope))
				{
					_owner.ActiveModalScope = scope.ParentScope;
					if (_owner._liveRegionManager is { } manager)
					{
						manager.ActiveModalHandle = scope.ParentScope?.ModalHandle ?? IntPtr.Zero;
					}
				}
			}
		}

		public void Dispose()
		{
			if (!_disposed)
			{
				_disposed = true;
				_subscriptions.Dispose();
				Deactivate();
			}
		}
	}
}
