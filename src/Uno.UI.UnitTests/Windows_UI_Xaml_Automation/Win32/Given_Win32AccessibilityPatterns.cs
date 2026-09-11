#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Uno.UI.Dispatching;
using Uno.UI.Runtime.Skia.Win32;

namespace Uno.UI.Tests.Windows_UI_Xaml_Automation;

[TestClass]
[DoNotParallelize]
public partial class Given_Win32AccessibilityPatterns
{
	private readonly Channel<Action> _pending = Channel.CreateUnbounded<Action>();
	private Action<Action, NativeDispatcherPriority>? _previousDispatch;
	private Func<bool>? _previousAccess;
	private int _comThreadId;
	private Button _root = null!;
	private PatternControl _owner = null!;
	private Win32Accessibility _accessibility = null!;
	private readonly List<Win32RawElementProvider> _nativeDisconnects = new();
	private Action<Win32RawElementProvider>? _beforeNativeDisconnect;

	[TestInitialize]
	public void Initialize()
	{
		var previousContext = SynchronizationContext.Current;
		try
		{
			UnitTestsApp.App.EnsureApplication();
		}
		finally
		{
			// The pump must not await its own UI context installed by Application.Start.
			SynchronizationContext.SetSynchronizationContext(previousContext);
		}
		_previousDispatch = NativeDispatcher.DispatchOverride;
		_previousAccess = NativeDispatcher.HasThreadAccessOverride;
		NativeDispatcher.HasThreadAccessOverride = () => Environment.CurrentManagedThreadId != Volatile.Read(ref _comThreadId);
		NativeDispatcher.DispatchOverride = (action, _) =>
		{
			Assert.IsTrue(_pending.Writer.TryWrite(action));
		};
		_root = new Button();
		_owner = new PatternControl();
		_accessibility = new Win32Accessibility(1, _root, _root.DispatcherQueue, provider =>
		{
			if (provider is Win32RawElementProvider raw)
			{
				_nativeDisconnects.Add(raw);
				_beforeNativeDisconnect?.Invoke(raw);
			}
			var result = Win32UIAutomationInterop.UiaDisconnectProvider(provider);
			Assert.IsTrue(result >= 0, $"Native provider cleanup failed with HRESULT 0x{result:X8}.");
			return result;
		});
	}

	[TestCleanup]
	public void Cleanup()
	{
		try
		{
			_beforeNativeDisconnect = null;
			_accessibility.Dispose();
			Drain();
		}
		finally
		{
			NativeDispatcher.DispatchOverride = _previousDispatch;
			NativeDispatcher.HasThreadAccessOverride = _previousAccess;
		}
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	public void When_Removed_And_Reattached_Queued_Invoke_Cannot_Use_Old_Generation(bool virtualPeer)
	{
		var peer = virtualPeer ? new PatternPeer(_owner) : _owner.Peer;
		var raw = _accessibility.GetProviderForPeer(peer)!;
		var invoke = (IUiaInvokeProvider)raw.GetPatternProvider(Win32UIAutomationInterop.UIA_InvokePatternId)!;
		invoke.Invoke();
		_accessibility.RouteChildRemoved(_root, _owner);
		_accessibility.RouteChildAdded(_root, _owner, null);
		var replacement = _accessibility.GetProviderForPeer(peer)!;
		Drain();

		Assert.IsTrue(_accessibility.IsAccessibilityEnabled, "The window remains open.");
		Assert.AreEqual(0, peer.Mutations, "Disconnect must revoke an already queued callback.");
		Assert.AreNotSame(raw, replacement, "Reattachment needs a fresh provider generation.");
		AssertUnavailable(invoke.Invoke);
		((IUiaInvokeProvider)replacement.GetPatternProvider(Win32UIAutomationInterop.UIA_InvokePatternId)!).Invoke();
		Drain();
		Assert.AreEqual(1, peer.Mutations);
	}

	[TestMethod]
	public void When_Canonical_Peer_Is_Replaced_Queued_Invoke_Cannot_Use_Old_Peer()
	{
		var oldPeer = _owner.Peer;
		var raw = _accessibility.GetOrCreateProvider(_owner)!;
		var invoke = (IUiaInvokeProvider)raw.GetPatternProvider(Win32UIAutomationInterop.UIA_InvokePatternId)!;
		invoke.Invoke();
		var replacementPeer = new PatternPeer(_owner);
		oldPeer.EventsSource = replacementPeer;
		var replacement = _accessibility.GetOrCreateProvider(_owner)!;
		Drain();

		Assert.AreEqual(0, oldPeer.Mutations);
		Assert.AreNotSame(raw, replacement);
		AssertUnavailable(invoke.Invoke);
		((IUiaInvokeProvider)replacement.GetPatternProvider(Win32UIAutomationInterop.UIA_InvokePatternId)!).Invoke();
		Drain();
		Assert.AreEqual(1, replacementPeer.Mutations);
	}

	[TestMethod]
	public void When_Queued_Invoke_Observes_Replaced_Peer_Restoring_Identity_Cannot_Revive_It()
	{
		var peer = _owner.Peer;
		var raw = _accessibility.GetOrCreateProvider(_owner)!;
		var invoke = (IUiaInvokeProvider)raw.GetPatternProvider(Win32UIAutomationInterop.UIA_InvokePatternId)!;
		invoke.Invoke();
		peer.EventsSource = new PatternPeer(_owner);
		Drain();
		Assert.AreEqual(0, peer.Mutations);
		peer.EventsSource = null;
		AssertUnavailable(invoke.Invoke);
		Assert.AreNotSame(raw, _accessibility.GetOrCreateProvider(_owner));
	}

	[TestMethod]
	public async Task When_Pattern_Discovery_Comes_From_Worker_It_Uses_Owner_Dispatcher()
	{
		var raw = _accessibility.GetOrCreateProvider(_owner)!;
		var result = await DispatchWorker(() => raw.GetPatternProvider(Win32UIAutomationInterop.UIA_TogglePatternId));
		Assert.IsInstanceOfType<IUiaToggleProvider>(result);
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	public void When_Host_Disposes_All_Provider_Generations_Are_Revoked(bool virtualPeer)
	{
		var peer = virtualPeer ? new PatternPeer(_owner) : _owner.Peer;
		var raw = _accessibility.GetProviderForPeer(peer)!;
		var invoke = (IUiaInvokeProvider)raw.GetPatternProvider(Win32UIAutomationInterop.UIA_InvokePatternId)!;
		var toggle = GetOperation(raw, "Toggle");
		invoke.Invoke();
		_accessibility.Dispose();
		Drain();
		Assert.AreEqual(0, peer.Mutations);
		AssertUnavailable(invoke.Invoke);
		AssertUnavailable(() => toggle());
		AssertUnavailable(() => raw.GetPatternProvider(Win32UIAutomationInterop.UIA_InvokePatternId));
		Assert.IsNull(_accessibility.GetProviderForPeer(peer));
	}

	[TestMethod]
	public void When_Disabled_After_Invoke_Preflight_Callback_Does_Not_Run()
	{
		var raw = _accessibility.GetOrCreateProvider(_owner)!;
		((IUiaInvokeProvider)raw.GetPatternProvider(Win32UIAutomationInterop.UIA_InvokePatternId)!).Invoke();
		_owner.IsEnabled = false;
		Drain();
		Assert.AreEqual(0, _owner.Peer.Mutations);
	}

	[TestMethod]
	public async Task When_Real_TextBox_Is_ReadOnly_Error_Is_Synchronous()
	{
		var textBox = new TextBox { IsReadOnly = true };
		var raw = _accessibility.GetOrCreateProvider(textBox)!;
		var value = (IUiaValueProvider)raw.GetPatternProvider(Win32UIAutomationInterop.UIA_ValuePatternId)!;
		Assert.IsTrue((bool)(await DispatchWorker(() => value.IsReadOnly))!);
		await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => DispatchWorker(AsOperation(() => value.SetValue("changed"))));
		Assert.AreEqual(string.Empty, textBox.Text);
	}

	[TestMethod]
	public async Task When_Real_Slider_Value_Is_Out_Of_Range_Error_Is_Synchronous()
	{
		var slider = new Slider { Minimum = 0, Maximum = 10, Value = 5 };
		var raw = _accessibility.GetOrCreateProvider(slider)!;
		var range = (IUiaRangeValueProvider)raw.GetPatternProvider(Win32UIAutomationInterop.UIA_RangeValuePatternId)!;
		await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => DispatchWorker(AsOperation(() => range.SetValue(11))));
		Assert.AreEqual(5d, slider.Value);
	}

	[TestMethod]
	[DataRow("Toggle")]
	[DataRow("ToggleState")]
	[DataRow("SetValue")]
	[DataRow("Value")]
	[DataRow("ValueIsReadOnly")]
	[DataRow("SetRangeValue")]
	[DataRow("RangeValue")]
	[DataRow("RangeIsReadOnly")]
	[DataRow("Maximum")]
	[DataRow("Minimum")]
	[DataRow("LargeChange")]
	[DataRow("SmallChange")]
	[DataRow("Select")]
	[DataRow("AddToSelection")]
	[DataRow("RemoveFromSelection")]
	[DataRow("IsSelected")]
	[DataRow("SelectionContainer")]
	[DataRow("Scroll")]
	[DataRow("SetScrollPercent")]
	[DataRow("HorizontalScrollPercent")]
	[DataRow("VerticalScrollPercent")]
	[DataRow("HorizontalViewSize")]
	[DataRow("VerticalViewSize")]
	[DataRow("HorizontallyScrollable")]
	[DataRow("VerticallyScrollable")]
	public async Task When_Pattern_Is_Called_From_Worker_Result_Is_Synchronous_On_Owner(string operation)
	{
		var call = GetOperation(_accessibility.GetOrCreateProvider(_owner)!, operation);
		var expected = call();
		var expectedMutations = _owner.Peer.Mutations * 2;
		var result = await DispatchWorker(call);
		Assert.AreEqual(expected, result);
		Assert.AreEqual(expectedMutations, _owner.Peer.Mutations, "Mutations must complete before their COM call returns.");
	}

	[TestMethod]
	[DataRow("Toggle")]
	[DataRow("SetValue")]
	[DataRow("SetRangeValue")]
	[DataRow("Select")]
	[DataRow("AddToSelection")]
	[DataRow("RemoveFromSelection")]
	[DataRow("Scroll")]
	[DataRow("SetScrollPercent")]
	public async Task When_Disabled_Mutation_Is_Rejected_Synchronously(string operation)
	{
		var call = GetOperation(_accessibility.GetOrCreateProvider(_owner)!, operation);
		_owner.IsEnabled = false;
		var error = await Assert.ThrowsExactlyAsync<COMException>(() => DispatchWorker(call));
		Assert.AreEqual(UiaInvokeProviderWrapper.ElementNotEnabledHResult, error.HResult);
		Assert.AreEqual(0, _owner.Peer.Mutations);
	}

	[TestMethod]
	[DataRow("Toggle")]
	[DataRow("SetValue")]
	[DataRow("SetRangeValue")]
	[DataRow("Select")]
	[DataRow("AddToSelection")]
	[DataRow("RemoveFromSelection")]
	[DataRow("Scroll")]
	[DataRow("SetScrollPercent")]
	public async Task When_Inner_Validation_Fails_Error_Reaches_Com_Caller(string operation)
	{
		var call = GetOperation(_accessibility.GetOrCreateProvider(_owner)!, operation);
		var expected = new ArgumentException("Invalid automation argument.");
		_owner.Peer.MutationError = expected;
		var error = await Assert.ThrowsExactlyAsync<ArgumentException>(() => DispatchWorker(call));
		Assert.AreSame(expected, error);
		Assert.AreEqual(0, _owner.Peer.Mutations);
	}

	[TestMethod]
	[DataRow("Toggle", false)]
	[DataRow("Value", false)]
	[DataRow("SetRangeValue", false)]
	[DataRow("Select", false)]
	[DataRow("SelectionContainer", false)]
	[DataRow("Scroll", false)]
	[DataRow("Toggle", true)]
	[DataRow("Value", true)]
	[DataRow("SetRangeValue", true)]
	[DataRow("Select", true)]
	[DataRow("SelectionContainer", true)]
	[DataRow("Scroll", true)]
	public async Task When_Removed_Before_Worker_Drains_Old_Pattern_Is_Unavailable(string operation, bool virtualPeer)
	{
		var peer = virtualPeer ? new PatternPeer(_owner) : _owner.Peer;
		var raw = _accessibility.GetProviderForPeer(peer)!;
		var call = GetOperation(raw, operation);
		var error = await Assert.ThrowsExactlyAsync<COMException>(() => DispatchWorker(call, () =>
		{
			_accessibility.RouteChildRemoved(_root, _owner);
			_accessibility.RouteChildAdded(_root, _owner, null);
			Assert.AreNotSame(raw, _accessibility.GetProviderForPeer(peer));
		}));
		Assert.AreEqual(UiaInvokeProviderWrapper.ElementNotAvailableHResult, error.HResult);
		Assert.AreEqual(0, peer.Mutations);
	}

	[TestMethod]
	[DataRow("Toggle")]
	[DataRow("Value")]
	[DataRow("SetRangeValue")]
	[DataRow("Select")]
	[DataRow("Scroll")]
	public async Task When_Worker_Times_Out_Late_Dispatch_Does_Not_Access_Peer(string operation)
	{
		var call = GetOperation(_accessibility.GetOrCreateProvider(_owner)!, operation);
		var accesses = _owner.Peer.Accesses;
		var invocation = StartWorker(call);
		var error = await Assert.ThrowsExactlyAsync<COMException>(() => invocation.WaitAsync(TimeSpan.FromSeconds(10)));
		Assert.AreEqual(unchecked((int)0x80131505), error.HResult);
		Drain();
		Assert.AreEqual(accesses, _owner.Peer.Accesses);
		Assert.AreEqual(0, _owner.Peer.Mutations);
	}

	[TestMethod]
	[DataRow("ToggleState")]
	[DataRow("ValueIsReadOnly")]
	[DataRow("RangeIsReadOnly")]
	[DataRow("IsSelected")]
	[DataRow("HorizontallyScrollable")]
	public async Task When_Disabled_Pattern_Properties_Remain_Readable(string operation)
	{
		var call = GetOperation(_accessibility.GetOrCreateProvider(_owner)!, operation);
		_owner.IsEnabled = false;
		Assert.AreEqual(call(), await DispatchWorker(call));
	}

	private Task<object?> StartWorker(Func<object?> call) => Task.Factory.StartNew(() =>
	{
		Volatile.Write(ref _comThreadId, Environment.CurrentManagedThreadId);
		return call();
	}, CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Default);

	private async Task<object?> DispatchWorker(Func<object?> call, Action? beforeDrain = null)
	{
		var invocation = StartWorker(call);
		using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		try
		{
			var beforeDrainPending = true;
			while (!invocation.IsCompleted)
			{
				var queued = _pending.Reader.WaitToReadAsync(cancellation.Token).AsTask();
				if (await Task.WhenAny(queued, invocation).WaitAsync(cancellation.Token) == invocation)
				{
					break;
				}
				Assert.IsTrue(await queued);
				if (beforeDrainPending)
				{
					beforeDrain?.Invoke();
					beforeDrainPending = false;
				}
				Assert.IsTrue(_pending.Reader.TryRead(out var action));
				action();
			}
			return await invocation.WaitAsync(cancellation.Token);
		}
		finally
		{
			await cancellation.CancelAsync();
		}
	}

	private void Drain()
	{
		while (_pending.Reader.TryRead(out var action))
		{
			action();
		}
	}

	private static void AssertUnavailable(Action action)
	{
		var error = Assert.ThrowsExactly<COMException>(action);
		Assert.AreEqual(UiaInvokeProviderWrapper.ElementNotAvailableHResult, error.HResult);
	}

	private static Func<object?> GetOperation(Win32RawElementProvider raw, string operation)
	{
		var toggle = (IUiaToggleProvider)raw.GetPatternProvider(Win32UIAutomationInterop.UIA_TogglePatternId)!;
		var value = (IUiaValueProvider)raw.GetPatternProvider(Win32UIAutomationInterop.UIA_ValuePatternId)!;
		var range = (IUiaRangeValueProvider)raw.GetPatternProvider(Win32UIAutomationInterop.UIA_RangeValuePatternId)!;
		var selection = (IUiaSelectionItemProvider)raw.GetPatternProvider(Win32UIAutomationInterop.UIA_SelectionItemPatternId)!;
		var scroll = (IUiaScrollProvider)raw.GetPatternProvider(Win32UIAutomationInterop.UIA_ScrollPatternId)!;
		return operation switch
		{
			"Toggle" => AsOperation(toggle.Toggle),
			"ToggleState" => () => toggle.ToggleState,
			"SetValue" => AsOperation(() => value.SetValue("updated")),
			"Value" => () => value.Value,
			"ValueIsReadOnly" => () => value.IsReadOnly,
			"SetRangeValue" => AsOperation(() => range.SetValue(50)),
			"RangeValue" => () => range.Value,
			"RangeIsReadOnly" => () => range.IsReadOnly,
			"Maximum" => () => range.Maximum,
			"Minimum" => () => range.Minimum,
			"LargeChange" => () => range.LargeChange,
			"SmallChange" => () => range.SmallChange,
			"Select" => AsOperation(selection.Select),
			"AddToSelection" => AsOperation(selection.AddToSelection),
			"RemoveFromSelection" => AsOperation(selection.RemoveFromSelection),
			"IsSelected" => () => selection.IsSelected,
			"SelectionContainer" => () => selection.SelectionContainer,
			"Scroll" => AsOperation(() => scroll.Scroll(ScrollAmount.SmallIncrement, ScrollAmount.NoAmount)),
			"SetScrollPercent" => AsOperation(() => scroll.SetScrollPercent(25, 50)),
			"HorizontalScrollPercent" => () => scroll.HorizontalScrollPercent,
			"VerticalScrollPercent" => () => scroll.VerticalScrollPercent,
			"HorizontalViewSize" => () => scroll.HorizontalViewSize,
			"VerticalViewSize" => () => scroll.VerticalViewSize,
			"HorizontallyScrollable" => () => scroll.HorizontallyScrollable,
			"VerticallyScrollable" => () => scroll.VerticallyScrollable,
			_ => throw new ArgumentOutOfRangeException(nameof(operation)),
		};
	}

	private static Func<object?> AsOperation(Action action) => () =>
	{
		action();
		return null;
	};

	private sealed class PatternControl : Control
	{
		internal PatternPeer Peer => (PatternPeer)GetOrCreateAutomationPeer()!;
		protected override AutomationPeer OnCreateAutomationPeer() => new PatternPeer(this);
	}

	private sealed class PatternPeer(PatternControl owner) : FrameworkElementAutomationPeer(owner),
		IInvokeProvider, IToggleProvider, IValueProvider, IRangeValueProvider, ISelectionItemProvider, IScrollProvider
	{
		internal int Mutations;
		internal int Accesses;
		internal Exception? MutationError;
		internal Action? OnMutation;
		internal IList<AutomationPeer>? DeclaredChildren;
		internal int ChildrenQueries;

		protected override IList<AutomationPeer>? GetChildrenCore()
		{
			ChildrenQueries++;
			return DeclaredChildren;
		}

		protected override object? GetPatternCore(PatternInterface patternInterface)
		{
			CheckAccess();
			return patternInterface is PatternInterface.Invoke or PatternInterface.Toggle or PatternInterface.Value
				or PatternInterface.RangeValue or PatternInterface.SelectionItem or PatternInterface.Scroll ? this : null;
		}

		private void CheckAccess()
		{
			Assert.IsTrue(owner.DispatcherQueue.HasThreadAccess, "Peer access must run on its owner dispatcher.");
			Accesses++;
		}
		private T Read<T>(T value)
		{
			CheckAccess();
			return value;
		}
		private void Mutate()
		{
			CheckAccess();
			if (MutationError is { } error)
			{
				throw error;
			}
			Mutations++;
			OnMutation?.Invoke();
		}

		public void Invoke() => Mutate();
		public void Toggle() => Mutate();
		public ToggleState ToggleState => Read(ToggleState.On);
		public void SetValue(string value) => Mutate();
		string IValueProvider.Value => Read("value");
		public bool IsReadOnly => Read(false);
		public void SetValue(double value) => Mutate();
		double IRangeValueProvider.Value => Read(50d);
		public double Maximum => Read(100d);
		public double Minimum => Read(0d);
		public double LargeChange => Read(10d);
		public double SmallChange => Read(1d);
		public void Select() => Mutate();
		public void AddToSelection() => Mutate();
		public void RemoveFromSelection() => Mutate();
		public bool IsSelected => Read(true);
		Microsoft.UI.Xaml.Automation.Provider.IRawElementProviderSimple ISelectionItemProvider.SelectionContainer => Read(ProviderFromPeer(this));
		public void Scroll(ScrollAmount horizontalAmount, ScrollAmount verticalAmount) => Mutate();
		public void SetScrollPercent(double horizontalPercent, double verticalPercent) => Mutate();
		public double HorizontalScrollPercent => Read(25d);
		public double VerticalScrollPercent => Read(50d);
		public double HorizontalViewSize => Read(10d);
		public double VerticalViewSize => Read(20d);
		public bool HorizontallyScrollable => Read(true);
		public bool VerticallyScrollable => Read(true);
	}
}
