#nullable enable

using System;
using Uno.Extensions;
using System.Threading.Tasks;
using Uno.Foundation.Logging;
using NativeMethods = __Windows.Storage.StorageFolder.NativeMethods;

namespace Windows.Storage
{
	partial class StorageFolder
	{
		private static readonly TaskCompletionSource<bool> _storageInitialized = new(TaskCreationOptions.RunContinuationsAsynchronously);

		internal async Task MakePersistentAsync()
			=> await MakePersistentAsync(this);

		private static async Task TryInitializeStorage()
		{
			if (typeof(StorageFolder).Log().IsEnabled(Uno.Foundation.Logging.LogLevel.Debug))
			{
				typeof(StorageFolder).Log().Debug("Waiting for emscripten storage initialization");
			}

			await _storageInitialized.Task;

			if (typeof(StorageFolder).Log().IsEnabled(Uno.Foundation.Logging.LogLevel.Debug))
			{
				typeof(StorageFolder).Log().Debug("Emscripten storage initialized");
			}
		}

		internal static async Task MakePersistentAsync(params StorageFolder[] folders)
			=> await NativeMethods.MakePersistentAsync(folders.SelectToArray(f => f.Path));

		internal static async Task InitializeApplicationDataAsync(params StorageFolder[] folders)
		{
			try
			{
				await MakePersistentAsync(folders);
				_storageInitialized.TrySetResult(true);
			}
			catch (Exception error)
			{
				_storageInitialized.TrySetException(error);
				throw;
			}
		}

	}
}
