#nullable enable

using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Automation.Provider;

namespace Uno.UI.Runtime.Skia.Win32;

[ComImport, Guid("54fcb24b-e18e-47a2-b4d3-eccbe77599a2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUiaInvokeProvider
{
	void Invoke();
}

[ComVisible(true)]
internal sealed class UiaInvokeProviderWrapper : IUiaInvokeProvider
{
	internal const int ElementNotAvailableHResult = unchecked((int)0x80040201);
	internal const int ElementNotEnabledHResult = unchecked((int)0x80040200);
	private const int TimeoutHResult = unchecked((int)0x80131505);

	private readonly IInvokeProvider _inner;
	private readonly DispatcherQueue _dispatcherQueue;
	private readonly Func<bool> _isAvailable;
	private readonly Func<bool> _isEnabled;

	internal UiaInvokeProviderWrapper(IInvokeProvider inner, DispatcherQueue dispatcherQueue, Func<bool> isAvailable, Func<bool> isEnabled)
	{
		_inner = inner;
		_dispatcherQueue = dispatcherQueue;
		_isAvailable = isAvailable;
		_isEnabled = isEnabled;
	}

	public void Invoke()
	{
		ValidateInvocation();
		// UIA can call from a COM worker. Return before the action runs, since closing
		// a window can disconnect the very provider that is servicing this call.
		if (!_isAvailable() || !_dispatcherQueue.TryEnqueue(() =>
		{
			if (_isAvailable())
			{
				_inner.Invoke();
			}
		}))
		{
			throw new COMException("The automation element is no longer available.", ElementNotAvailableHResult);
		}
	}

	private void ValidateInvocation()
	{
		if (!_isAvailable())
		{
			throw new COMException("The automation element is no longer available.", ElementNotAvailableHResult);
		}
		if (_dispatcherQueue.HasThreadAccess)
		{
			ValidateOnDispatcher();
			return;
		}

		var state = new PreflightState();
		if (!_dispatcherQueue.TryEnqueue(() =>
		{
			lock (state)
			{
				if (state.Abandoned)
				{
					return;
				}
			}
			ExceptionDispatchInfo? error = null;
			try
			{
				ValidateOnDispatcher();
			}
			catch (Exception exception)
			{
				error = ExceptionDispatchInfo.Capture(exception);
			}
			lock (state)
			{
				state.Error = error;
				state.Completed = true;
				Monitor.Pulse(state);
			}
		}))
		{
			throw new COMException("The automation dispatcher is unavailable.", ElementNotAvailableHResult);
		}

		// Only the side-effect-free preflight is synchronous; the control action
		// must remain asynchronous so closing cannot deadlock the COM caller.
		var started = Stopwatch.GetTimestamp();
		lock (state)
		{
			while (!state.Completed)
			{
				var remaining = TimeSpan.FromSeconds(5) - Stopwatch.GetElapsedTime(started);
				if (remaining <= TimeSpan.Zero)
				{
					state.Abandoned = true;
					throw new COMException("The automation dispatcher did not respond.", TimeoutHResult);
				}
				Monitor.Wait(state, remaining);
			}
		}
		state.Error?.Throw();
	}

	private void ValidateOnDispatcher()
	{
		if (!_isAvailable())
		{
			throw new COMException("The automation element is no longer available.", ElementNotAvailableHResult);
		}
		if (!_isEnabled())
		{
			throw new COMException("The automation element is disabled.", ElementNotEnabledHResult);
		}
	}

	private sealed class PreflightState
	{
		internal bool Completed;
		internal bool Abandoned;
		internal ExceptionDispatchInfo? Error;
	}
}
