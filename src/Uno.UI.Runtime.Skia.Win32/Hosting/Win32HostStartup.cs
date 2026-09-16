#nullable enable

using System;
using System.Threading.Tasks;

namespace Uno.UI.Runtime.Skia.Win32;

internal sealed class Win32HostStartup
{
	private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

	internal Task Completion => _completion.Task;

	internal Exception? ExceptionToLog { get; private set; }

	internal void Run(
		Action startApplication,
		Func<Exception, bool> tryHandleException,
		Func<bool> hasVisibleWindows)
	{
		try
		{
			startApplication();
			_completion.TrySetResult();
		}
		catch (Exception exception)
		{
			CompleteFromException(exception, tryHandleException, hasVisibleWindows);
		}
	}

	private void CompleteFromException(
		Exception exception,
		Func<Exception, bool> tryHandleException,
		Func<bool> hasVisibleWindows)
	{
		try
		{
			if (exception is OperationCanceledException cancellation
				&& cancellation.CancellationToken.IsCancellationRequested)
			{
				if (hasVisibleWindows())
				{
					_completion.TrySetResult();
				}
				else
				{
					_completion.TrySetCanceled(cancellation.CancellationToken);
				}

				return;
			}

			if (tryHandleException(exception))
			{
				_completion.TrySetResult();
				return;
			}

			if (hasVisibleWindows())
			{
				ExceptionToLog = exception;
				_completion.TrySetResult();
				return;
			}

			ExceptionToLog = exception;
			_completion.TrySetException(exception);
		}
		catch (Exception exceptionHandlingFailure)
		{
			var aggregate = new AggregateException(
				"Failed to process an exception raised during Win32 application startup.",
				exception,
				exceptionHandlingFailure);

			ExceptionToLog = aggregate;
			_completion.TrySetException(aggregate);
		}
	}
}
