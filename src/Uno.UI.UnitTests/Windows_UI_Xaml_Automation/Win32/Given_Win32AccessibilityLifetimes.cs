#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Uno.UI.Runtime.Skia.Win32;

namespace Uno.UI.Tests.Windows_UI_Xaml_Automation;

public partial class Given_Win32AccessibilityPatterns
{
	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	public void When_Logical_Children_Change_Queued_Invoke_Is_Revoked_Without_Removing_Owner(bool replace)
	{
		var parent = _owner.Peer;
		var child = new PatternPeer(_owner);
		parent.DeclaredChildren = new List<AutomationPeer> { child };
		var parentProvider = _accessibility.GetOrCreateProvider(_owner)!;
		var raw = (Win32RawElementProvider)parentProvider.GetFirstChildCore()!;
		var invoke = (IUiaInvokeProvider)raw.GetPatternProvider(Win32UIAutomationInterop.UIA_InvokePatternId)!;
		invoke.Invoke();

		parent.DeclaredChildren.Clear();
		var replacement = new PatternPeer(_owner);
		if (replace)
		{
			parent.DeclaredChildren.Add(replacement);
		}
		_accessibility.NotifyInvalidatePeer(parent);
		Drain();

		Assert.IsTrue(parentProvider.HasCurrentPeer, "The shared visual owner and its provider must remain alive.");
		Assert.AreEqual(0, child.Mutations);
		AssertUnavailable(invoke.Invoke);
		Assert.IsNull(_accessibility.GetProviderForPeer(child), "An obsolete logical peer must not get a fresh live provider.");
		if (replace)
		{
			var replacementProvider = (Win32RawElementProvider)parentProvider.GetFirstChildCore()!;
			((IUiaToggleProvider)replacementProvider.GetPatternProvider(Win32UIAutomationInterop.UIA_TogglePatternId)!).Toggle();
			Assert.AreEqual(1, replacement.Mutations);
		}

		parent.DeclaredChildren.Clear();
		parent.DeclaredChildren.Add(child);
		_accessibility.NotifyInvalidatePeer(parent);
		var reattached = (Win32RawElementProvider)parentProvider.GetFirstChildCore()!;
		Assert.AreNotSame(raw, reattached);
		AssertUnavailable(invoke.Invoke);
		((IUiaInvokeProvider)reattached.GetPatternProvider(Win32UIAutomationInterop.UIA_InvokePatternId)!).Invoke();
		Drain();
		Assert.AreEqual(1, child.Mutations);
	}

	[TestMethod]
	[DataRow("Toggle")]
	[DataRow("Value")]
	[DataRow("SetRangeValue")]
	[DataRow("Select")]
	[DataRow("Scroll")]
	public async Task When_Logical_Child_Is_Removed_Synchronous_Pattern_Is_Unavailable(string operation)
	{
		var parent = _owner.Peer;
		var child = new PatternPeer(_owner);
		parent.DeclaredChildren = new List<AutomationPeer> { child };
		var parentProvider = _accessibility.GetOrCreateProvider(_owner)!;
		var raw = (Win32RawElementProvider)parentProvider.GetFirstChildCore()!;
		var call = GetOperation(raw, operation);
		var error = await Assert.ThrowsExactlyAsync<COMException>(() => DispatchWorker(call, () =>
		{
			parent.DeclaredChildren = new List<AutomationPeer>();
			_accessibility.NotifyInvalidatePeer(parent);
		}));
		Assert.AreEqual(UiaInvokeProviderWrapper.ElementNotAvailableHResult, error.HResult);
		Assert.AreEqual(0, child.Mutations);
		Assert.IsTrue(parentProvider.HasCurrentPeer);
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	public void When_Logical_Ancestor_Is_Invalidated_Materialized_Descendants_Are_Revoked(bool canonicalDescendant)
	{
		var child = new PatternPeer(_owner);
		var descendantOwner = canonicalDescendant ? new PatternControl() : _owner;
		var grandchild = canonicalDescendant ? descendantOwner.Peer : new PatternPeer(descendantOwner);
		_owner.Peer.DeclaredChildren = new List<AutomationPeer> { child };
		child.DeclaredChildren = new List<AutomationPeer> { grandchild };
		var parentProvider = _accessibility.GetOrCreateProvider(_owner)!;
		var childProvider = (Win32RawElementProvider)parentProvider.GetFirstChildCore()!;
		var grandchildProvider = (Win32RawElementProvider)childProvider.GetFirstChildCore()!;
		Assert.AreEqual(!canonicalDescendant, grandchildProvider.IsVirtualPeer);
		Assert.IsTrue(grandchildProvider.HasCurrentPeer);
		var invoke = (IUiaInvokeProvider)grandchildProvider.GetPatternProvider(Win32UIAutomationInterop.UIA_InvokePatternId)!;
		invoke.Invoke();
		_owner.Peer.DeclaredChildren.Clear();
		_accessibility.NotifyInvalidatePeer(_owner.Peer);
		Drain();
		Assert.AreEqual(0, grandchild.Mutations);
		AssertUnavailable(invoke.Invoke);
		Assert.IsFalse(childProvider.HasCurrentPeer);
		Assert.IsFalse(grandchildProvider.HasCurrentPeer);
		Assert.IsNull(_accessibility.GetProviderForPeer(grandchild));
		_owner.Peer.DeclaredChildren.Add(child);
		_accessibility.NotifyInvalidatePeer(_owner.Peer);
		var republished = _accessibility.GetProviderForPeer(grandchild)!;
		Assert.AreNotSame(grandchildProvider, republished);
		Assert.IsTrue(republished.HasCurrentPeer);
		Assert.AreSame(descendantOwner, republished.Owner);
		((IUiaToggleProvider)republished.GetPatternProvider(Win32UIAutomationInterop.UIA_TogglePatternId)!).Toggle();
		Assert.AreEqual(1, grandchild.Mutations);
		AssertUnavailable(invoke.Invoke);
		Assert.AreEqual(1, _nativeDisconnects.Count(provider => ReferenceEquals(provider, grandchildProvider)));
		if (canonicalDescendant)
		{
			Assert.AreSame(republished, _accessibility.GetOrCreateProvider(descendantOwner));
		}
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	public void When_Invalidating_Unqueried_Data_Children_Only_Exposed_Peers_Are_Retired(bool structureChanged)
	{
		var parent = _owner.Peer;
		parent.DeclaredChildren = new AutomationPeer[100_000];
		var child = new PatternPeer(_owner);
		child.SetParent(parent);
		var raw = _accessibility.GetProviderForPeer(child)!;
		var toggle = (IUiaToggleProvider)raw.GetPatternProvider(Win32UIAutomationInterop.UIA_TogglePatternId)!;

		if (structureChanged)
		{
			_accessibility.NotifyAutomationEvent(parent, AutomationEvents.StructureChanged);
		}
		else
		{
			_accessibility.NotifyInvalidatePeer(parent);
		}

		Assert.AreEqual(0, parent.ChildrenQueries, "Invalidation must not enumerate a data-backed child collection, even without a parent provider.");
		AssertUnavailable(toggle.Toggle);
		Assert.AreEqual(0, child.Mutations);
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	public void When_Unmaterialized_Logical_Ancestor_Is_Invalidated_Exposed_Grandchild_Is_Revoked(bool canonicalDescendant)
	{
		var parent = _owner.Peer;
		var intermediate = new PatternPeer(_owner);
		var descendantOwner = canonicalDescendant ? new PatternControl() : _owner;
		var child = canonicalDescendant ? descendantOwner.Peer : new PatternPeer(descendantOwner);
		parent.DeclaredChildren = new List<AutomationPeer> { intermediate };
		intermediate.DeclaredChildren = new List<AutomationPeer> { child };
		parent.GetChildren();
		intermediate.GetChildren();
		var raw = _accessibility.GetProviderForPeer(child)!;
		Assert.AreEqual(!canonicalDescendant, raw.IsVirtualPeer);
		Assert.IsTrue(raw.HasCurrentPeer);
		var toggle = (IUiaToggleProvider)raw.GetPatternProvider(Win32UIAutomationInterop.UIA_TogglePatternId)!;
		var invoke = (IUiaInvokeProvider)raw.GetPatternProvider(Win32UIAutomationInterop.UIA_InvokePatternId)!;
		invoke.Invoke();
		parent.DeclaredChildren.Clear();
		_accessibility.NotifyInvalidatePeer(parent);
		Drain();
		AssertUnavailable(toggle.Toggle);
		AssertUnavailable(invoke.Invoke);
		Assert.AreEqual(0, child.Mutations);
		Assert.AreEqual(1, parent.ChildrenQueries, "Invalidation must not query declarations.");
		Assert.AreEqual(1, intermediate.ChildrenQueries);
		Assert.AreEqual(1, _nativeDisconnects.Count, "Only C was materialized, not L or P.");
		Assert.IsNull(_accessibility.GetProviderForPeer(child), "L still declares C, but P no longer declares L.");
		Assert.IsNull(_accessibility.GetProviderForPeer(child), "Repeated lookup must not publish or cache a detached descendant.");
		AssertUnavailable(toggle.Toggle);

		parent.DeclaredChildren.Add(intermediate);
		_accessibility.NotifyInvalidatePeer(parent);
		var recovered = _accessibility.GetProviderForPeer(child)!;
		Assert.AreNotSame(raw, recovered);
		Assert.IsTrue(recovered.HasCurrentPeer);
		Assert.AreSame(descendantOwner, recovered.Owner);
		((IUiaToggleProvider)recovered.GetPatternProvider(Win32UIAutomationInterop.UIA_TogglePatternId)!).Toggle();
		Assert.AreEqual(1, child.Mutations);
		AssertUnavailable(toggle.Toggle);
		Assert.AreEqual(1, _nativeDisconnects.Count(provider => ReferenceEquals(provider, raw)));

		parent.DeclaredChildren.Clear();
		_accessibility.NotifyInvalidatePeer(parent);
		Assert.IsNull(_accessibility.GetProviderForPeer(child));
		Assert.IsFalse(recovered.HasCurrentPeer);
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	public void When_Unmaterialized_Ancestor_Is_Replaced_Recovery_Requires_Current_Declaration(bool canonicalDescendant)
	{
		var parent = _owner.Peer;
		var original = new PatternPeer(_owner);
		var replacement = new PatternPeer(_owner);
		var descendantOwner = canonicalDescendant ? new PatternControl() : _owner;
		var child = canonicalDescendant ? descendantOwner.Peer : new PatternPeer(descendantOwner);
		parent.DeclaredChildren = new List<AutomationPeer> { original };
		original.DeclaredChildren = new List<AutomationPeer> { child };
		replacement.DeclaredChildren = new List<AutomationPeer> { child };
		parent.GetChildren();
		original.GetChildren();
		var raw = _accessibility.GetProviderForPeer(child)!;
		var toggle = (IUiaToggleProvider)raw.GetPatternProvider(Win32UIAutomationInterop.UIA_TogglePatternId)!;

		parent.DeclaredChildren[0] = replacement;
		_accessibility.NotifyInvalidatePeer(parent);
		Assert.IsNull(_accessibility.GetProviderForPeer(child), "An equal owner does not make the original declaration current.");
		AssertUnavailable(toggle.Toggle);

		parent.GetChildren();
		replacement.GetChildren();
		var recovered = _accessibility.GetProviderForPeer(child)!;
		Assert.AreNotSame(raw, recovered);
		Assert.AreSame(descendantOwner, recovered.Owner);
		Assert.IsTrue(recovered.HasCurrentPeer);
		((IUiaToggleProvider)recovered.GetPatternProvider(Win32UIAutomationInterop.UIA_TogglePatternId)!).Toggle();
		Assert.AreEqual(1, child.Mutations);
		AssertUnavailable(toggle.Toggle);
	}

	[TestMethod]
	public void When_Logical_Ancestor_Reparents_Queued_Grandchild_Cannot_Follow_New_Owner()
	{
		var parent = _owner.Peer;
		var intermediate = new PatternPeer(_owner);
		var child = new PatternPeer(_owner);
		intermediate.SetParent(parent);
		child.SetParent(intermediate);
		var raw = _accessibility.GetProviderForPeer(child)!;
		var invoke = (IUiaInvokeProvider)raw.GetPatternProvider(Win32UIAutomationInterop.UIA_InvokePatternId)!;
		invoke.Invoke();
		intermediate.SetParent(new PatternPeer(_owner));
		Drain();
		Assert.AreEqual(0, child.Mutations);
		AssertUnavailable(invoke.Invoke);
	}

	[TestMethod]
	public void When_Visual_Subtree_Is_Removed_Each_Generation_Is_Disconnected_Once()
	{
		var (subtree, providers) = CreateProviderSubtree();
		_accessibility.RouteChildRemoved(_root, subtree);
		foreach (var provider in providers)
		{
			Assert.AreEqual(1, _nativeDisconnects.Count(disconnected => ReferenceEquals(disconnected, provider)));
		}
	}

	[TestMethod]
	public void When_Visual_Subtree_Is_Removed_All_Generations_Are_Revoked_Before_Native_Cleanup()
	{
		var (subtree, providers) = CreateProviderSubtree();
		_beforeNativeDisconnect = _ =>
		{
			foreach (var provider in providers)
			{
				Assert.IsFalse(provider.HasCurrentPeer, "Native cleanup may reenter; every descendant must already be revoked.");
			}
		};
		_accessibility.RouteChildRemoved(_root, subtree);
		Assert.AreEqual(providers.Length, _nativeDisconnects.Count);
	}

	private (PatternPanel Subtree, Win32RawElementProvider[] Providers) CreateProviderSubtree()
	{
		var other = new PatternControl();
		var nested = new PatternPanel { Children = { other } };
		var subtree = new PatternPanel { Children = { _owner, nested } };
		return (subtree, new[]
		{
			_accessibility.GetOrCreateProvider(subtree)!,
			_accessibility.GetOrCreateProvider(nested)!,
			_accessibility.GetOrCreateProvider(_owner)!,
			_accessibility.GetProviderForPeer(new PatternPeer(_owner))!,
			_accessibility.GetOrCreateProvider(other)!,
			_accessibility.GetProviderForPeer(new PatternPeer(other))!,
		});
	}

	[TestMethod]
	[DataRow(false, false)]
	[DataRow(true, false)]
	[DataRow(false, true)]
	[DataRow(true, true)]
	public async Task When_Realized_ListView_Item_Is_Disabled_Selection_Properties_Are_Readable(bool selected, bool disableParent)
	{
		var style = new Style(typeof(ListViewItem));
		style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(() => new ContentPresenter())));
		var list = new ListView
		{
			Template = new ControlTemplate(() => new ItemsPresenter()),
			ItemsPanel = new ItemsPanelTemplate(() => new StackPanel()),
			ItemContainerStyle = style,
			ItemsSource = new[] { "One" },
		};
		list.ForceLoaded();
		Drain();
		list.SelectedIndex = selected ? 0 : -1;
		Assert.AreEqual(selected ? "One" : null, list.SelectedItem, "Establish selection after container realization.");
		var container = (ListViewItem)list.ContainerFromIndex(0);
		Assert.IsNotNull(container, "This regression requires a realized container, not a stand-in provider.");
		var listPeer = new ListViewAutomationPeer(list);
		var peer = (SelectorItemAutomationPeer)listPeer.GetChildren()![0];
		var raw = _accessibility.GetProviderForPeer(peer)!;
		var selection = (IUiaSelectionItemProvider)raw.GetPatternProvider(Win32UIAutomationInterop.UIA_SelectionItemPatternId)!;
		Assert.AreEqual(selected, await DispatchWorker(() => selection.IsSelected));
		if (disableParent)
		{
			list.IsEnabled = false;
		}
		else
		{
			container.IsEnabled = false;
		}

		Assert.IsFalse(peer.IsEnabled());
		Assert.AreEqual(selected, await DispatchWorker(() => selection.IsSelected));
		var selectionContainer = (IRawElementProviderSimple)(await DispatchWorker(() => selection.SelectionContainer))!;
		Assert.IsNotNull(selectionContainer);
		Assert.IsNotNull(await DispatchWorker(() => selectionContainer.GetPatternProvider(Win32UIAutomationInterop.UIA_SelectionPatternId)));
		var error = await Assert.ThrowsExactlyAsync<COMException>(() => DispatchWorker(AsOperation(selection.Select)));
		Assert.AreEqual(UiaInvokeProviderWrapper.ElementNotEnabledHResult, error.HResult);
		Assert.AreEqual(selected ? 0 : -1, list.SelectedIndex);
		list.IsEnabled = true;
		container.IsEnabled = true;
		await DispatchWorker(AsOperation(selection.Select));
		Assert.AreEqual(0, list.SelectedIndex);
		Assert.AreEqual(true, await DispatchWorker(() => selection.IsSelected));
		await DispatchWorker(AsOperation(selection.RemoveFromSelection));
		Assert.AreEqual(-1, list.SelectedIndex);
		await DispatchWorker(AsOperation(selection.AddToSelection));
		Assert.AreEqual(0, list.SelectedIndex);
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	public async Task When_Started_Synchronous_Operation_Crosses_Timeout_It_Owns_Completion(bool fails)
	{
		var raw = _accessibility.GetOrCreateProvider(_owner)!;
		var call = GetOperation(raw, "Toggle");
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var release = new ManualResetEventSlim();
		var expected = new InvalidOperationException("The started provider operation failed.");
		_owner.Peer.OnMutation = () =>
		{
			entered.SetResult();
			Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(20)));
			if (fails)
			{
				throw expected;
			}
		};
		var invocation = StartWorker(call);
		Assert.IsTrue(await _pending.Reader.WaitToReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
		var pump = Task.Factory.StartNew(Drain, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
		try
		{
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
			var elapsed = Stopwatch.StartNew();
			while (true)
			{
				var remaining = UiaProviderDispatcher.ResponseTimeout - elapsed.Elapsed;
				if (remaining <= TimeSpan.Zero)
				{
					break;
				}
				await Task.Delay(remaining);
			}
			Assert.IsFalse(invocation.IsCompleted, "A started synchronous operation must not report a timeout or success before completion.");
		}
		finally
		{
			release.Set();
		}
		await pump.WaitAsync(TimeSpan.FromSeconds(10));
		if (fails)
		{
			var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => invocation.WaitAsync(TimeSpan.FromSeconds(10)));
			Assert.AreSame(expected, error);
		}
		else
		{
			await invocation.WaitAsync(TimeSpan.FromSeconds(10));
		}
		Assert.AreEqual(1, _owner.Peer.Mutations);
	}

	[TestMethod]
	[DataRow("Toggle", false)]
	[DataRow("SetValue", false)]
	[DataRow("SetRangeValue", false)]
	[DataRow("Select", false)]
	[DataRow("Scroll", false)]
	[DataRow("Toggle", true)]
	[DataRow("SetValue", true)]
	[DataRow("SetRangeValue", true)]
	[DataRow("Select", true)]
	[DataRow("Scroll", true)]
	public async Task When_Synchronous_Callback_Removes_Content_Or_Closes_Window_Call_Completes(string operation, bool close)
	{
		var window = new Window { Content = _owner };
		var native = (UnitTestsApp.TestNativeWindowWrapper)window.NativeWrapper!;
		try
		{
			var raw = _accessibility.GetOrCreateProvider(_owner)!;
			var call = GetOperation(raw, operation);
			_owner.Peer.OnMutation = () =>
			{
				window.Content = null;
				_accessibility.RouteChildRemoved(_root, _owner);
				if (close)
				{
					window.Close();
					_accessibility.Dispose();
				}
			};
			await DispatchWorker(call);
			Assert.AreEqual(1, _owner.Peer.Mutations);
			Assert.IsNull(window.Content);
			Assert.AreEqual(close ? 1 : 0, native.CloseCount);
			AssertUnavailable(() => call());
		}
		finally
		{
			window.Close();
		}
	}

	private sealed class PatternPanel : Panel
	{
		protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
	}
}
