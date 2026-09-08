using System.Reflection;
using System.Runtime.InteropServices.JavaScript;
using Uno.Foundation;
using Windows.Storage;

internal static partial class Program
{
	private static readonly BindingFlags NonPublicStatic = BindingFlags.NonPublic | BindingFlags.Static;

	private static async Task Main(string[] args)
	{
		try
		{
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
			var ct = timeout.Token;
			var mode = args[0];
			var token = args[1];
			var app = ApplicationData.Current;
			var checks = new List<string>();
			if (mode == "no-mounts" || mode == "external")
			{
				await ExpectFailure(Checkpoint(ct), "No Uno-owned IDBFS mounts");
				checks.Add("no-noop-acknowledgement");
			}

			async Task WaitForStorage()
				=> await app.LocalFolder.CreateFolderAsync("fixture-ready", CreationCollisionOption.OpenIfExists);

			var waiting = WaitForStorage();
			await MakePersistent(app.LocalCacheFolder);
			var gate = WaitForInitializationGate();
			Require(!gate.IsCompleted, "An unrelated mount completed global storage initialization.");
			checks.Add("unrelated-mount-does-not-initialize-app-data");

			var initializing = InitializeApplicationData(app);
			if (mode == "startup-failure")
			{
				await initializing.WaitAsync(ct);
				await ExpectFailure(waiting.WaitAsync(ct), "controlled IndexedDB open failure");
				await ExpectFailure(gate.WaitAsync(ct), "controlled IndexedDB open failure");
				await ExpectFailure(Checkpoint(ct), "controlled IndexedDB open failure");
				checks.Add("startup-failure-faults-waiters-and-checkpoints");
				Report("PASS|" + string.Join("|", checks));
				return;
			}

			if (mode == "concurrent-initialization")
			{
				var second = InitializeApplicationData(app);
				await WebAssemblyRuntime.InvokeAsync("fixture.waitForShared()", ct);
				Require(!initializing.IsCompleted && !second.IsCompleted && !waiting.IsCompleted && !gate.IsCompleted,
					"Application-data initialization completed before the final mount was restored.");
				WebAssemblyRuntime.InvokeJS("fixture.releaseShared()");
				await Task.WhenAll(initializing, second, waiting, gate).WaitAsync(ct);
				checks.Add("concurrent-initialization-awaits-all-app-mounts");
			}
			else
			{
				await Task.WhenAll(initializing, waiting, gate).WaitAsync(ct);
			}

			if (mode == "disabled")
			{
				await ExpectFailure(Checkpoint(ct), "persistence is unavailable");
				checks.Add("disabled-startup-compatible-but-checkpoint-rejects");
			}
			else if (mode == "restore")
			{
				Require(File.ReadAllText(Path.Combine(app.LocalFolder.Path, "fixture-checkpoint.txt")) == token,
					"An acknowledged file did not survive reload.");
				Require(File.ReadAllText(Path.Combine(app.LocalFolder.Path, "fixture-second.txt")) == token,
					"A queued acknowledged file did not survive reload.");
				checks.Add("acknowledged-files-restored-after-reload");
			}
			else
			{
				File.WriteAllText(Path.Combine(app.LocalFolder.Path, "fixture-checkpoint.txt"), token);
				var voidResult = await WebAssemblyRuntime.InvokeAsync(
					"Windows.Storage.StorageFolder.synchronizeFileSystem(false)", ct);
				Require(voidResult is null, "The public bridge changed its Promise<void> result contract.");
				checks.Add("promise-void-acknowledges-as-null");

				File.WriteAllText(Path.Combine(app.LocalFolder.Path, "fixture-second.txt"), token);
				var first = Checkpoint(ct);
				var second = Checkpoint(ct);
				var results = await Task.WhenAll(first, second);
				Require(results.All(result => result == "checkpoint-complete"), "String acknowledgement was lost.");
				checks.Add("concurrent-compiled-csharp-checkpoints");
			}
			Report("PASS|" + string.Join("|", checks));
		}
		catch (Exception error)
		{
			Report("FAIL|" + error);
		}
	}

	private static Task<string> Checkpoint(CancellationToken ct)
		=> WebAssemblyRuntime.InvokeAsync(
			"Windows.Storage.StorageFolder.synchronizeFileSystem(false).then(() => 'checkpoint-complete')", ct);

	private static Task MakePersistent(StorageFolder folder)
		=> (Task)typeof(StorageFolder).GetMethod("MakePersistentAsync", NonPublicStatic)!.Invoke(null, new object[] { new[] { folder } })!;

	private static Task WaitForInitializationGate()
		=> (Task)typeof(StorageFolder).GetMethod("TryInitializeStorage", NonPublicStatic)!.Invoke(null, null)!;

	private static Task InitializeApplicationData(ApplicationData app)
		=> (Task)typeof(ApplicationData).GetMethod("EnablePersistenceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, null)!;

	private static async Task ExpectFailure(Task operation, string message)
	{
		try
		{
			await operation;
		}
		catch (Exception error) when (error.ToString().Contains(message, StringComparison.Ordinal))
		{
			return;
		}
		throw new InvalidOperationException("Expected failure: " + message);
	}

	private static void Require(bool condition, string message)
	{
		if (!condition)
		{
			throw new InvalidOperationException(message);
		}
	}

	[JSImport("globalThis.fixture.report")]
	private static partial void Report(string result);
}
