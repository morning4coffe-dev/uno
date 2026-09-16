#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Uno.UI.Dispatching;
using Uno.UI.Runtime.Skia.Win32;

namespace Uno.UI.Tests.Windows_UI_Xaml_Automation;

[TestClass]
[DoNotParallelize]
public class Given_Win32HostStartup
{
	[TestMethod]
	public void When_Startup_Succeeds_Completion_Succeeds()
	{
		var startup = new Win32HostStartup();
		var started = false;

		startup.Run(
			() => started = true,
			_ => false,
			() => false);

		Assert.IsTrue(started);
		Assert.IsTrue(startup.Completion.IsCompletedSuccessfully);
		Assert.IsNull(startup.ExceptionToLog);
	}

	[TestMethod]
	public void When_Startup_Exception_Is_Handled_Completion_Succeeds()
	{
		var startup = new Win32HostStartup();
		var failure = new InvalidOperationException("Handled startup failure");
		var visibilityChecked = false;

		startup.Run(
			() => throw failure,
			exception =>
			{
				Assert.AreSame(failure, exception);
				return true;
			},
			() =>
			{
				visibilityChecked = true;
				return false;
			});

		Assert.IsFalse(visibilityChecked);
		Assert.IsTrue(startup.Completion.IsCompletedSuccessfully);
		Assert.IsNull(startup.ExceptionToLog);
	}

	[TestMethod]
	public void When_Startup_Fails_After_Window_Is_Visible_Completion_Succeeds()
	{
		var startup = new Win32HostStartup();
		var failure = new InvalidOperationException("Post-window startup failure");

		startup.Run(
			() => throw failure,
			_ => false,
			() => true);

		Assert.IsTrue(startup.Completion.IsCompletedSuccessfully);
		Assert.AreSame(failure, startup.ExceptionToLog);
	}

	[TestMethod]
	public async Task When_Startup_Fails_Before_Window_Completion_Faults()
	{
		var startup = new Win32HostStartup();
		var failure = new InvalidOperationException("Pre-window startup failure");

		startup.Run(
			() => throw failure,
			_ => false,
			() => false);

		var observed = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => startup.Completion);
		Assert.AreSame(failure, observed);
		Assert.AreSame(failure, startup.ExceptionToLog);
	}

	[TestMethod]
	public async Task When_Startup_Failure_Runs_In_Native_Callback_Completion_Faults()
	{
		var startup = new Win32HostStartup();
		var failure = new InvalidOperationException("Native callback startup failure");

		Win32EventLoop.Schedule(
			() => startup.Run(
				() => throw failure,
				_ => false,
				() => false),
			NativeDispatcherPriority.Normal);

		Win32EventLoop.RunOnce();

		var observed = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => startup.Completion);
		Assert.AreSame(failure, observed);
	}

	[TestMethod]
	public async Task When_Startup_Is_Canceled_Before_Window_Completion_Is_Canceled()
	{
		var startup = new Win32HostStartup();
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		var exceptionHandlerCalled = false;

		startup.Run(
			() => throw new OperationCanceledException(cancellation.Token),
			_ =>
			{
				exceptionHandlerCalled = true;
				return false;
			},
			() => false);

		var observed = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => startup.Completion);
		Assert.AreEqual(cancellation.Token, observed.CancellationToken);
		Assert.IsFalse(exceptionHandlerCalled);
		Assert.IsNull(startup.ExceptionToLog);
	}

	[TestMethod]
	public async Task When_Exception_Handler_Throws_Completion_Faults_Without_Callback_Escape()
	{
		var startup = new Win32HostStartup();
		var startupFailure = new InvalidOperationException("Startup failure");
		var handlerFailure = new InvalidOperationException("Handler failure");

		startup.Run(
			() => throw startupFailure,
			_ => throw handlerFailure,
			() => false);

		var observed = await Assert.ThrowsExactlyAsync<AggregateException>(() => startup.Completion);
		CollectionAssert.AreEqual(
			new Exception[] { startupFailure, handlerFailure },
			observed.InnerExceptions);
	}

	[TestMethod]
	[DataRow("run-failure", 31)]
	[DataRow("runasync-failure", 32)]
	[DataRow("run-hidden-window-failure", 33)]
	[DataRow("runasync-hidden-window-failure", 34)]
	public async Task When_PreWindow_Startup_Fails_Host_Propagates_To_Caller(string mode, int expectedExitCode)
	{
		var result = await RunFixture(mode);

		Assert.AreEqual(expectedExitCode, result.ExitCode, result.ToString());
		StringAssert.Contains(result.StandardOutput, $"PROPAGATED_FAILURE:{mode}");
		if (mode.Contains("hidden-window", StringComparison.Ordinal))
		{
			StringAssert.Contains(result.StandardOutput, $"HIDDEN_WINDOW_DESTROYED:{mode}");
		}
	}

	[TestMethod]
	[DataRow("run-cancellation", 41)]
	[DataRow("runasync-cancellation", 42)]
	public async Task When_PreWindow_Startup_Is_Canceled_Host_Propagates_Cancellation(string mode, int expectedExitCode)
	{
		var result = await RunFixture(mode);

		Assert.AreEqual(expectedExitCode, result.ExitCode, result.ToString());
		StringAssert.Contains(result.StandardOutput, $"PROPAGATED_CANCELLATION:{mode}");
	}

	[TestMethod]
	[DataRow("run-normal")]
	[DataRow("runasync-normal")]
	[DataRow("run-handled")]
	[DataRow("runasync-handled")]
	public async Task When_Startup_Completes_Or_Handles_Exception_Host_Exits_Normally(string mode)
	{
		var result = await RunFixture(mode);

		Assert.AreEqual(0, result.ExitCode, result.ToString());
		StringAssert.Contains(result.StandardOutput, $"COMPLETED:{mode}");
	}

	[TestMethod]
	public async Task When_Hidden_Window_Is_Not_Destroyed_Fixture_Rejects_Control()
	{
		var result = await RunFixture("run-hidden-window-leak-control");

		Assert.AreEqual(75, result.ExitCode, result.ToString());
		StringAssert.Contains(result.StandardError, "HIDDEN_WINDOW_ASSERTION_FAILED:run-hidden-window-leak-control");
	}

	private static async Task<FixtureResult> RunFixture(string mode)
	{
		var fixturePath = GetFixturePath();
		Assert.IsTrue(File.Exists(fixturePath), $"The Win32 startup fixture was not built at {fixturePath}.");
		var fixtureDirectory = Path.GetDirectoryName(fixturePath)!;
		var hostAssemblyPath = Path.Combine(fixtureDirectory, "Uno.UI.Runtime.Skia.Win32.dll");
		var supportAssemblyPath = Path.Combine(fixtureDirectory, "Uno.UI.Runtime.Skia.Win32.Support.dll");
		Assert.IsTrue(File.Exists(hostAssemblyPath), $"The Win32 host module was not built at {hostAssemblyPath}.");
		Assert.IsTrue(File.Exists(supportAssemblyPath), $"The Win32 support module was not built at {supportAssemblyPath}.");

		var dotnetPath =
			Environment.GetEnvironmentVariable("UNO_WIN32_STARTUP_TEST_DOTNET")
			?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
		Assert.IsFalse(string.IsNullOrWhiteSpace(dotnetPath), "The test runner did not provide a dotnet host path.");
		Assert.IsTrue(File.Exists(dotnetPath), $"The dotnet host was not found at {dotnetPath}.");

		var startInfo = new ProcessStartInfo
		{
			FileName = dotnetPath,
			WorkingDirectory = fixtureDirectory,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		startInfo.ArgumentList.Add(fixturePath);
		startInfo.ArgumentList.Add(mode);

		using var process = Process.Start(startInfo);
		Assert.IsNotNull(process);
		var standardOutput = process.StandardOutput.ReadToEndAsync();
		var standardError = process.StandardError.ReadToEndAsync();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

		try
		{
			await process.WaitForExitAsync(timeout.Token);
		}
		catch (OperationCanceledException)
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
			}

			await process.WaitForExitAsync();
			throw new AssertFailedException(
				$"The Win32 startup fixture '{mode}' did not exit within 15 seconds.{Environment.NewLine}" +
				$"stdout:{Environment.NewLine}{await standardOutput}{Environment.NewLine}" +
				$"stderr:{Environment.NewLine}{await standardError}");
		}

		var result = new FixtureResult(
			process.ExitCode,
			await standardOutput,
			await standardError);
		StringAssert.Contains(result.StandardOutput, $"HOST_MODULE:{hostAssemblyPath}");
		return result;
	}

	private static string GetFixturePath()
	{
		var testOutput = new DirectoryInfo(AppContext.BaseDirectory);
		var configuration = testOutput.Parent!.Name;
		var testProjectDirectory = testOutput.Parent.Parent!.Parent!.Parent!.Parent!;

		return Path.Combine(
			testProjectDirectory.FullName,
			"Fixtures",
			"Win32HostStartup",
			"bin",
			configuration,
			testOutput.Name,
			"Uno.UI.Runtime.Skia.Win32.StartupFixture.dll");
	}

	private sealed record FixtureResult(int ExitCode, string StandardOutput, string StandardError);
}
