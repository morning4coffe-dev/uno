#nullable enable

using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;

namespace Uno.UI.Runtime.Skia.Win32;

internal sealed class UiaProviderDispatcher(
	DispatcherQueue dispatcherQueue,
	Func<bool> isAvailable,
	Func<bool> isEnabled)
{
	internal const int ElementNotAvailableHResult = unchecked((int)0x80040201);
	internal const int ElementNotEnabledHResult = unchecked((int)0x80040200);
	internal const int TimeoutHResult = unchecked((int)0x80131505);
	internal static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);

	private int _disconnected;

	internal bool IsConnected => Volatile.Read(ref _disconnected) == 0;

	internal void Disconnect() => Interlocked.Exchange(ref _disconnected, 1);

	internal T Run<T>(Func<T> action, bool requiresEnabled = false) =>
		Run(action, requiresEnabled, isPreflight: false);

	private T Run<T>(Func<T> action, bool requiresEnabled, bool isPreflight)
	{
		ValidateConnection();
		if (dispatcherQueue.HasThreadAccess)
		{
			ValidateOnDispatcher(requiresEnabled);
			return action();
		}

		var state = new DispatchState<T>();
		if (!dispatcherQueue.TryEnqueue(() =>
		{
			lock (state)
			{
				if (state.Abandoned)
				{
					return;
				}
				state.Started = true;
			}

			try
			{
				ValidateOnDispatcher(requiresEnabled);
				state.Result = action();
			}
			catch (Exception exception)
			{
				// Transport the original exception back to the synchronous COM caller.
				state.Error = ExceptionDispatchInfo.Capture(exception);
			}
			finally
			{
				lock (state)
				{
					state.Completed = true;
					Monitor.Pulse(state);
				}
			}
		}))
		{
			throw Unavailable();
		}

		var started = Stopwatch.GetTimestamp();
		lock (state)
		{
			while (!state.Completed)
			{
				var remaining = ResponseTimeout - Stopwatch.GetElapsedTime(started);
				if (remaining <= TimeSpan.Zero && (!state.Started || isPreflight))
				{
					state.Abandoned = true;
					throw new COMException("The automation dispatcher did not respond.", TimeoutHResult);
				}

				// A synchronous operation that has started owns its result and errors.
				// Only work still waiting for the dispatcher can be abandoned.
				if (state.Started && !isPreflight)
				{
					Monitor.Wait(state);
				}
				else
				{
					Monitor.Wait(state, remaining);
				}
			}
		}
		state.Error?.Throw();
		return state.Result!;
	}

	internal void Run(Action action, bool requiresEnabled = false) => Run(() =>
	{
		action();
		return true;
	}, requiresEnabled);

	internal void Invoke(Action action)
	{
		Run(static () => true, requiresEnabled: true, isPreflight: true);
		ValidateConnection();
		// Invoke must return before a callback can close its own COM provider's window.
		if (!dispatcherQueue.TryEnqueue(() =>
		{
			if (IsAvailableOnDispatcher() && isEnabled())
			{
				action();
			}
		}))
		{
			throw Unavailable();
		}
	}

	private void ValidateConnection()
	{
		if (!IsConnected)
		{
			throw Unavailable();
		}
	}

	private void ValidateOnDispatcher(bool requiresEnabled)
	{
		if (!IsAvailableOnDispatcher())
		{
			throw Unavailable();
		}
		if (requiresEnabled && !isEnabled())
		{
			throw new COMException("The automation element is disabled.", ElementNotEnabledHResult);
		}
	}

	private bool IsAvailableOnDispatcher()
	{
		if (!IsConnected)
		{
			return false;
		}
		if (!isAvailable())
		{
			Disconnect();
			return false;
		}
		return true;
	}

	private static COMException Unavailable() =>
		new("The automation element is no longer available.", ElementNotAvailableHResult);

	private sealed class DispatchState<T>
	{
		internal bool Started;
		internal bool Completed;
		internal bool Abandoned;
		internal T? Result;
		internal ExceptionDispatchInfo? Error;
	}
}
