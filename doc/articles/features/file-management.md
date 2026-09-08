---
uid: Uno.Features.FileManagement
---

# File Management

File management allows shared reading and writing of files across all Uno Platform targets. This includes the ability to read files from the application package, as well as the ability to read and write files from the file system.

> [!TIP]
> This article covers Uno-specific information for file management. For a full description of the feature and instructions on using it, see [Files, folders, and libraries](https://learn.microsoft.com/windows/uwp/files/).

## Supported features

| Feature             | WinUI     | Android | iOS     | Web (WASM) | macOS   | Linux (Skia) | WPF (Skia) |
|---------------------|-----------|---------|---------|------------|---------|--------------|------------|
| `StorageFile`       | ✔         | ✔       | ✔       | ✔          | ✔       | ✔            | ✔          |
| `StorageFolder`     | ✔         | ✔       | ✔       | ✔          | ✔       | ✔            | ✔          |
| `CachedFileManager` | ✔         | partial | partial | partial    | partial | partial      | partial    |
| `StorageFileHelper` | ✔         | ✔       | ✔       | ✔          | ✔       | ✔            | ✔          |

## Overview

Uno supports some of the APIs from the `Windows.Storage` namespace, such as `Windows.Storage.StorageFile` and `Windows.Storage.StorageFolder` for all platforms.

Both `Windows.Storage` and `System.IO` APIs are available, with some platform specifics defined below. In general, it is best to use `Windows.Storage` APIs when available, as their asynchronous nature allows for transparent interactions with the underlying file system implementations. In addition, `System.IO` cannot work with files that are not owned by the application directly (e.g. files picked by a dialog).

Note that for file and folder metadata only `BasicProperties` are partially supported for now.
`FileAttributes` and all "advanced properties" (`StorageItemContentProperties`) related to the content of the file, including the thumbnail, are not yet supported.

## WebAssembly File System

WebAssembly file system APIs are built using [emscripten's POSIX file system APIs](https://emscripten.org/docs/api_reference/Filesystem-API.html). The persistence is done through the use of browser APIs, such as IndexedDB through [emscripten's IDBFS](https://emscripten.org/docs/api_reference/Filesystem-API.html#filesystem-api-idbfs).

While it is possible to write files in any paths, only some folders are persisted across browser refreshes:

- `ApplicationData.Current.LocalFolder`
- `ApplicationData.Current.RoamingFolder`
- `ApplicationData.Current.SharedLocalFolder`

### Waiting for initialization

Filesystem initialization is asynchronous. Application launch is not an initialization barrier: WebAssembly startup begins persistence initialization without awaiting it before scheduling `Application.OnLaunched`. Application construction occurs even earlier. Consequently, `System.IO` or SQLite can open a file before its persistent contents have been restored.

Before accessing an existing application-data directory through `System.IO` or SQLite, explicitly await `StorageFolder.GetFolderFromPathAsync`:

```csharp
var localFolder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(
    Windows.Storage.ApplicationData.Current.LocalFolder.Path);
var folder = await localFolder.CreateFolderAsync("myFolder", CreationCollisionOption.OpenIfExists);
File.WriteAllText(Path.Combine(folder.Path, "MyFile.txt"), DateTime.Now.ToLongDateString());
```

`GetFolderFromPathAsync` unconditionally awaits the storage-initialization gate before checking that the directory exists. Pass the directory actually used by the database; for example, a .NET MAUI application can pass `FileSystem.AppDataDirectory`. Perform this await **before constructing a SQLite connection or running schema initialization, migrations, queries, or writes**. Keep the barrier and database initialization under the same asynchronous initialization/operation lock used by database callers. Do not synchronously block an application constructor waiting for initialization.

Not every asynchronous `StorageFolder` API is a reliable barrier. In particular, `GetItemsAsync` and `GetFilesAsync` may return an empty enumeration without awaiting initialization. Do not use them to establish readiness. `CreateFolderAsync` also awaits initialization, but creating a different directory is unnecessary when opening an existing database.

Note that you can view the content of the **IndexedDB** in the Application tab of your browser, in the **Storage / IndexedDB** section.

### WebAssembly persistence checkpoints

File writes (including `FileStream.Flush`, `FileIO` operations, and SQLite commits) update an in-memory filesystem. They do **not** acknowledge a write to IndexedDB. Uno attempts a checkpoint every ten seconds and when the page becomes hidden or unloads. These attempts are best effort: browsers do not wait for asynchronous IndexedDB work when navigating, closing, or terminating a page. A successful file write can therefore be lost on an immediate reload.

When the application must acknowledge a checkpoint before navigating or displaying a persisted-save confirmation, finish the writes and await the existing JavaScript synchronization entry point through Uno's asynchronous interop:

```csharp
// WebAssembly only. Initialize the folder asynchronously before using System.IO or SQLite.
await Uno.Foundation.WebAssemblyRuntime.InvokeAsync(
    "Windows.Storage.StorageFolder.synchronizeFileSystem(false).then(() => 'checkpoint-complete')",
    cancellationToken);
```

The returned task completes after Emscripten reports completion of the IndexedDB synchronization, or throws on failure. The JavaScript method returns `Promise<void>`; the string projection above matches `InvokeAsync`'s `Promise<string>` contract. Invoking the unprojected promise completes with a `null` result, not a durability status. Cancelling the managed wait does not cancel an in-flight IndexedDB transaction and must not be interpreted as an acknowledgement.

Keep application writes serialized with the checkpoint; for databases, finish the transaction and prevent concurrent database changes during synchronization. The checkpoint covers all IDBFS mounts registered by Uno, not just one file. It does not acknowledge stores mounted directly by application JavaScript or other libraries. If no Uno-owned mounts exist, the checkpoint rejects instead of acknowledging a no-op; this validation failure does not prevent subsequent initialization. Do not pass `true`: that direction restores IndexedDB contents into memory and can overwrite unpersisted changes.

Synchronization requests are queued, including requests made during another checkpoint. Application-data initialization awaits restoration of all its folders and propagates restoration failures to asynchronous storage operations instead of leaving them waiting indefinitely. Initializing an unrelated mount does not complete the application-data initialization gate. Adding a persistent mount restores only that mount, without replacing existing in-memory files.

If backend synchronization fails or does not complete within one minute, subsequent requests fail as well until the page is reloaded. Emscripten cannot cancel an outstanding synchronization; starting another batch would risk overlapping operations. Handle the exception and do not report that changes were persisted. Reloading can discard unacknowledged changes.

When IDBFS is disabled or IndexedDB is unavailable at startup, storage initialization still succeeds for compatibility, but storage remains non-persistent. An explicit checkpoint rejects when persistence is unavailable instead of acknowledging a no-op. Even with persistence enabled, checkpoints do not protect against browser storage eviction, user-cleared data, conflicts with another tab writing the same files, or device failure. For critical data, use an appropriate remote persistence strategy.

## Support for `StorageFile.GetFileFromApplicationUriAsync`

Uno Platform supports the ability to get package files using the [`StorageFile.GetFileFromApplicationUriAsync(Uri)`](https://learn.microsoft.com/uwp/api/windows.storage.storagefile.getfilefromapplicationuriasync) method.

Support per platform may vary:

- On WebAssembly targets, the requested file is part of the application package on the remote server and is downloaded on demand to avoid increasing the initial application payload size. After it is requested for the first time, the file is then stored in the browser IndexedDB.
- Otherwise, the file is available directly as it is a part of the installed package.

### General usage instructions

Ensure that a declaration exists in your project file like the following:

```xml
<ItemGroup>
    <Content Include="MyPackageFile.xml" />
</ItemGroup>
```

A URI with the `ms-appx:///` scheme can then be used to read a file's content:

```csharp
var file = await Windows.Storage.StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///MyPackageFile.xml"));
var content = await FileIO.ReadTextAsync(file);
```

### Support for Library provided assets

Since Uno Platform 4.6, the `GetFileFromApplicationUriAsync` method supports reading assets provided by `ProjectReference` or `PackageReference` libraries, using the following syntax:

Given a library or package named `MyLibrary01`, the following format can be used to read assets:

```csharp
var file = await Windows.Storage.StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///MyLibrary01/MyPackageFile.xml"));
var content = await FileIO.ReadTextAsync(file);
```

Uno Platform also provides the ability to determine if an asset or resource exists in the application package by using `StorageFileHelper.ExistsInPackage`:

```csharp
var fileExists = await StorageFileHelper.ExistsInPackage("Assets/Fonts/uno-fluentui-assets.ttf");
```

## Support for `RandomAccessStreamReference.CreateFromUri`

Uno Platform supports the creation of a `RandomAccessStreamReference` from an `Uri` (`RandomAccessStreamReference.CreateFromUri`), but note that on WASM downloading a file from a server often causes issues with [CORS](https://developer.mozilla.org/en-US/docs/Web/HTTP/CORS).
Make sure the server that hosts the file is configured accordingly.

## Support for `CachedFileManager`

For all targets except WinUI and WebAssembly, the `CachedFileManager` does not provide any functionality, and its methods immediately return. This allows us to easily write code that requires deferring updates on Windows but is shared across all targets.

In the case of WebAssembly, the behavior of `CachedFileManager` depends on whether the app uses the **File System Access API** or **Download picker**. This is described extensively within the [documentation](xref:Uno.Features.WSPickers#webassembly) for storage pickers.
