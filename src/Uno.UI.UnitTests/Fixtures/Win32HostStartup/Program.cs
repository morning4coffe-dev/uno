#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Uno.UI.Hosting;
using Uno.UI.Runtime.Skia.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Uno.UI.Runtime.Skia.Win32.StartupFixture;

internal static class Program
{
	private const string FailureMessage = "Deterministic pre-window startup failure";
	private static readonly CancellationToken CanceledToken = new(canceled: true);

	public static async Task<int> Main(string[] args)
	{
		if (args.Length != 1 || !StartupMode.TryParse(args[0], out var mode))
		{
			Console.Error.WriteLine("Expected one startup fixture mode.");
			return 64;
		}

		var host = UnoPlatformHostBuilder.Create()
			.App(() => new FixtureApplication(mode))
			.UseWin32()
			.Build();
		Console.WriteLine($"HOST_MODULE:{Path.GetFullPath(typeof(Win32Host).Assembly.Location)}");

		var outcome = StartupOutcome.Completed;
		try
		{
			if (mode.RunAsync)
			{
				await host.RunAsync();
			}
			else
			{
				host.Run();
			}
		}
		catch (FixtureStartupException exception) when (exception.Message == FailureMessage)
		{
			outcome = StartupOutcome.Failure;
		}
		catch (OperationCanceledException exception) when (exception.CancellationToken == CanceledToken)
		{
			outcome = StartupOutcome.Cancellation;
		}

		if (mode.Kind is StartupKind.HiddenWindowFailure or StartupKind.HiddenWindowLeakControl)
		{
			var hwnd = FixtureApplication.HiddenWindowHwnd;
			if (hwnd == HWND.Null)
			{
				Console.Error.WriteLine($"HIDDEN_WINDOW_NOT_CAPTURED:{mode.Name}");
				return 74;
			}

			if (PInvoke.IsWindow(hwnd))
			{
				Console.Error.WriteLine($"HIDDEN_WINDOW_ASSERTION_FAILED:{mode.Name}");
				_ = PInvoke.DestroyWindow(hwnd);
				return 75;
			}

			Console.WriteLine($"HIDDEN_WINDOW_DESTROYED:{mode.Name}");
		}

		switch (outcome)
		{
			case StartupOutcome.Failure:
				Console.WriteLine($"PROPAGATED_FAILURE:{mode.Name}");
				return mode.ExpectedExitCode;
			case StartupOutcome.Cancellation:
				Console.WriteLine($"PROPAGATED_CANCELLATION:{mode.Name}");
				return mode.ExpectedExitCode;
			default:
				Console.WriteLine($"COMPLETED:{mode.Name}");
				return mode.ExpectedExitCode;
		}
	}

	private sealed class FixtureApplication : Application
	{
		private readonly StartupMode _mode;

		internal static HWND HiddenWindowHwnd { get; private set; }

		internal FixtureApplication(StartupMode mode)
		{
			_mode = mode;
			if (mode.Kind == StartupKind.HandledFailure)
			{
				UnhandledException += OnUnhandledException;
			}
		}

		protected override void OnLaunched(LaunchActivatedEventArgs args)
		{
			switch (_mode.Kind)
			{
				case StartupKind.NormalExit:
					Exit();
					return;
				case StartupKind.Failure:
				case StartupKind.HandledFailure:
					throw new FixtureStartupException(FailureMessage);
				case StartupKind.HiddenWindowFailure:
					CreateHiddenWindow();
					throw new FixtureStartupException(FailureMessage);
				case StartupKind.HiddenWindowLeakControl:
					CreateHiddenWindow();
					Win32Host.ForceAllWindowsClosed();
					return;
				case StartupKind.Cancellation:
					throw new OperationCanceledException(CanceledToken);
				default:
					throw new InvalidOperationException($"Unsupported startup kind {_mode.Kind}.");
			}
		}

		private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
		{
			if (args.Exception is FixtureStartupException { Message: FailureMessage })
			{
				args.Handled = true;
				Exit();
			}
		}

		private static void CreateHiddenWindow()
		{
			var existingWindows = new HashSet<HWND>(Win32WindowWrapper.GetHwnds());
			_ = new Window();
			HiddenWindowHwnd = Win32WindowWrapper.GetHwnds().Single(hwnd => !existingWindows.Contains(hwnd));
		}
	}

	private sealed class FixtureStartupException(string message) : Exception(message);

	private enum StartupKind
	{
		NormalExit,
		Failure,
		HandledFailure,
		HiddenWindowFailure,
		HiddenWindowLeakControl,
		Cancellation,
	}

	private enum StartupOutcome
	{
		Completed,
		Failure,
		Cancellation,
	}

	private sealed record StartupMode(string Name, bool RunAsync, StartupKind Kind, int ExpectedExitCode)
	{
		internal static bool TryParse(string value, out StartupMode mode)
		{
			mode = value switch
			{
				"run-normal" => new(value, false, StartupKind.NormalExit, 0),
				"runasync-normal" => new(value, true, StartupKind.NormalExit, 0),
				"run-failure" => new(value, false, StartupKind.Failure, 31),
				"runasync-failure" => new(value, true, StartupKind.Failure, 32),
				"run-hidden-window-failure" => new(value, false, StartupKind.HiddenWindowFailure, 33),
				"runasync-hidden-window-failure" => new(value, true, StartupKind.HiddenWindowFailure, 34),
				"run-hidden-window-leak-control" => new(value, false, StartupKind.HiddenWindowLeakControl, 75),
				"run-handled" => new(value, false, StartupKind.HandledFailure, 0),
				"runasync-handled" => new(value, true, StartupKind.HandledFailure, 0),
				"run-cancellation" => new(value, false, StartupKind.Cancellation, 41),
				"runasync-cancellation" => new(value, true, StartupKind.Cancellation, 42),
				_ => null!,
			};

			return mode is not null;
		}
	}
}
