#nullable enable

using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Uno.UI.Dispatching;
using Uno.UI.Runtime.Skia.Win32;

namespace Uno.UI.Tests.Windows_UI_Xaml_Automation;

[TestClass]
[DoNotParallelize]
public class Given_UiaInvokeProviderWrapper
{
	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	public async Task When_Menu_Item_Is_Disabled_Invoke_Is_Rejected_Before_Queueing(bool background)
	{
		UnitTestsApp.App.EnsureApplication();
		var item = new MenuFlyoutItem { IsEnabled = false };
		var peer = new MenuFlyoutItemAutomationPeer(item);
		var pending = new ConcurrentQueue<Action>();
		var previousDispatch = NativeDispatcher.DispatchOverride;
		var previousAccess = NativeDispatcher.HasThreadAccessOverride;
		var comThreadId = 0;
		try
		{
			NativeDispatcher.HasThreadAccessOverride = () => Environment.CurrentManagedThreadId != Volatile.Read(ref comThreadId);
			NativeDispatcher.DispatchOverride = (action, _) => pending.Enqueue(action);
			var provider = new UiaInvokeProviderWrapper((IInvokeProvider)peer, item.DispatcherQueue, () => true, peer.IsEnabled);
			COMException error;
			if (background)
			{
				var invocation = OnComThread(() =>
				{
					Volatile.Write(ref comThreadId, Environment.CurrentManagedThreadId);
					provider.Invoke();
				});
				while (pending.IsEmpty && !invocation.IsCompleted)
				{
					await Task.Delay(1).ConfigureAwait(false);
				}
				Assert.IsTrue(pending.TryDequeue(out var preflight));
				preflight();
				error = await Assert.ThrowsExactlyAsync<COMException>(() => invocation.WaitAsync(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
			}
			else
			{
				error = Assert.ThrowsExactly<COMException>(provider.Invoke);
			}
			Assert.AreEqual(unchecked((int)0x80040200), error.HResult);
			Assert.IsTrue(pending.IsEmpty);
		}
		finally
		{
			NativeDispatcher.DispatchOverride = previousDispatch;
			NativeDispatcher.HasThreadAccessOverride = previousAccess;
		}
	}

	[TestMethod]
	public async Task When_Preflight_Times_Out_Late_Work_Does_Not_Invoke()
	{
		var app = UnitTestsApp.App.EnsureApplication();
		var previousDispatch = NativeDispatcher.DispatchOverride;
		var previousAccess = NativeDispatcher.HasThreadAccessOverride;
		var pending = new ConcurrentQueue<Action>();
		var invoked = 0;
		var validated = 0;
		var comThreadId = 0;
		try
		{
			NativeDispatcher.HasThreadAccessOverride = () => Environment.CurrentManagedThreadId != Volatile.Read(ref comThreadId);
			NativeDispatcher.DispatchOverride = (action, _) => pending.Enqueue(action);
			var provider = new UiaInvokeProviderWrapper(new InvokeProvider(() => invoked++), app.MainWindow.DispatcherQueue,
				() => true, () => { validated++; return true; });
			var error = await Assert.ThrowsExactlyAsync<COMException>(
				() => OnComThread(() =>
				{
					Volatile.Write(ref comThreadId, Environment.CurrentManagedThreadId);
					provider.Invoke();
				}).WaitAsync(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
			Assert.AreEqual(unchecked((int)0x80131505), error.HResult);
			Assert.AreEqual(1, pending.Count);
			while (pending.TryDequeue(out var action))
			{
				action();
			}
			Assert.AreEqual(0, validated);
			Assert.AreEqual(0, invoked);
		}
		finally
		{
			NativeDispatcher.DispatchOverride = previousDispatch;
			NativeDispatcher.HasThreadAccessOverride = previousAccess;
		}
	}

	[TestMethod]
	public async Task When_Background_Invoke_Closes_Window_Callback_Runs_On_Dispatcher()
	{
		UnitTestsApp.App.EnsureApplication();
		var window = new Window { Content = new Border() };
		var native = (UnitTestsApp.TestNativeWindowWrapper)window.NativeWrapper!;
		var pending = new ConcurrentQueue<Action>();
		var comThreadId = 0;
		var previousAccess = NativeDispatcher.HasThreadAccessOverride;
		var previousDispatch = NativeDispatcher.DispatchOverride;
		var closed = 0;
		window.Closed += (_, _) =>
		{
			Assert.AreNotEqual(Volatile.Read(ref comThreadId), Environment.CurrentManagedThreadId,
				"The native automation provider must not invoke Closed on the COM caller thread.");
			window.Content = null;
			closed++;
		};
		try
		{
			NativeDispatcher.HasThreadAccessOverride = () => Environment.CurrentManagedThreadId != Volatile.Read(ref comThreadId);
			NativeDispatcher.DispatchOverride = (action, _) => pending.Enqueue(action);
			var provider = new UiaInvokeProviderWrapper(new InvokeProvider(window.Close), window.DispatcherQueue, () => native.CloseCount == 0, () => true);
			var invoke = OnComThread(() =>
			{
				Volatile.Write(ref comThreadId, Environment.CurrentManagedThreadId);
				provider.Invoke();
			});
			while (pending.IsEmpty && !invoke.IsCompleted)
			{
				await Task.Delay(1).ConfigureAwait(false);
			}
			Assert.IsTrue(pending.TryDequeue(out var preflight));
			preflight();
			await invoke.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
			Assert.AreEqual(0, closed, "Invoke must return before executing the UI action.");
			Assert.IsFalse(pending.IsEmpty);
			while (pending.TryDequeue(out var action))
			{
				action();
			}
			Assert.AreEqual(1, closed);
			Assert.AreEqual(1, native.CloseCount);
			Assert.IsNull(window.Content);
		}
		finally
		{
			NativeDispatcher.HasThreadAccessOverride = previousAccess;
			NativeDispatcher.DispatchOverride = previousDispatch;
			window.Close();
		}
	}

	[TestMethod]
	[DataRow(false)]
	[DataRow(true)]
	public void When_Owner_Closes_Invoke_Does_Not_Run(bool closedBeforeInvoke)
	{
		var app = UnitTestsApp.App.EnsureApplication();
		var previousDispatch = NativeDispatcher.DispatchOverride;
		var pending = new ConcurrentQueue<Action>();
		var available = !closedBeforeInvoke;
		var invoked = 0;
		try
		{
			NativeDispatcher.DispatchOverride = (action, _) => pending.Enqueue(action);
			var provider = new UiaInvokeProviderWrapper(new InvokeProvider(() => invoked++), app.MainWindow.DispatcherQueue, () => available, () => true);
			if (closedBeforeInvoke)
			{
				var exception = Assert.ThrowsExactly<COMException>(provider.Invoke);
				Assert.AreEqual(UiaInvokeProviderWrapper.ElementNotAvailableHResult, exception.HResult);
				Assert.IsTrue(pending.IsEmpty);
			}
			else
			{
				provider.Invoke();
				Assert.AreEqual(0, invoked);
				available = false;
				while (pending.TryDequeue(out var action))
				{
					action();
				}
			}
			Assert.AreEqual(0, invoked);
		}
		finally
		{
			NativeDispatcher.DispatchOverride = previousDispatch;
		}
	}

	// Dispatcher thread access is cached per thread; do not mark a reusable pool thread as a COM caller.
	private static Task OnComThread(Action action) =>
		Task.Factory.StartNew(action, CancellationToken.None,
			TaskCreationOptions.LongRunning | TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Default);

	private sealed class InvokeProvider(Action action) : IInvokeProvider
	{
		public void Invoke() => action();
	}
}
