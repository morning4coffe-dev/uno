
// eslint-disable-next-line @typescript-eslint/no-namespace
namespace Windows.Storage {

	interface IDBFSMount {
		readonly mountpoint: string;
		readonly type: IDBFSFileSystem;
	}

	interface IDBFSFileSystem {
		syncfs(mount: IDBFSMount, populate: boolean, callback: (error?: unknown) => void): void;
	}

	export class StorageFolder {
		private static _isInitialized = false;
		private static _synchronization: Promise<void> = Promise.resolve();
		private static _persistentMounts = new Map<string, IDBFSMount>();

		/**
		 * Determine if IndexedDB is available, some browsers and modes disable it.
		 * */
		public static isIndexDBAvailable(): boolean {
			try {
				// IndexedDB may not be available in private mode
				return !!window.indexedDB;
			} catch (err) {
				return false;
			}
		}

		/**
		 * Setup the storage persistence of a given set of paths.
		 * */
		private static async makePersistent(paths: string[]): Promise<void> {
			await Windows.ApplicationModel.Core.CoreApplication.waitForInitialized();

			await this.queueSynchronization(async () => {
				for (const path of paths) {
					const mount = this.setupStorage(path);
					if (mount) {
						// Populating all mounts again could overwrite unsaved files in an existing mount.
						await this.runSynchronization(callback => mount.type.syncfs(mount, true, callback));
					}
				}
			});

			if (!this._isInitialized && this._persistentMounts.size > 0) {
				const synchronize = () => {
					this.synchronizeFileSystem(false).catch((error: unknown) => {
						console.error(`Error synchronizing filesystem to IndexedDB: ${error}`);
					});
				};

				// Lifecycle events are best effort: browsers do not await IndexedDB work at unload.
				window.addEventListener("beforeunload", synchronize);
				window.addEventListener("pagehide", synchronize);
				document.addEventListener("visibilitychange", () => {
					if (document.visibilityState === "hidden") {
						synchronize();
					}
				});
				setInterval(synchronize, 10000);

				this._isInitialized = true;
			}
		}

		/**
		 * Setup the storage persistence of a given path.
		 * */
		public static setupStorage(path: string): IDBFSMount | null {
			if (this._persistentMounts.has(path)) {
				return null;
			}

			if (!this.isIndexDBAvailable()) {
				console.warn("IndexedDB is not available (private mode or uri starts with file:// ?), changes will not be persisted.");
				return null;
			}

			if (typeof IDBFS === 'undefined') {
				console.warn(`IDBFS is not enabled in the project configuration, persistence is disabled. See https://aka.platform.uno/wasm-idbfs for more details`);

				return null;
			}

			FS.mkdir(path);

			const root = FS.mount(IDBFS, {}, path) as { mount: IDBFSMount };
			this._persistentMounts.set(path, root.mount);
			return root.mount;
		}

		/**
		 * Synchronize Uno-owned IDBFS mounts. Await false to acknowledge an IndexedDB checkpoint.
		 * populate: replace the memory cache with IndexedDB contents (initialization only).
		 * onSynchronized: receives an error on failure; the returned promise also rejects.
		 * */
		public static synchronizeFileSystem(populate: boolean, onSynchronized?: (error?: unknown) => void): Promise<void> {
			let unavailableReason: string | undefined;
			const result = this.queueSynchronization(async () => {
				if (typeof IDBFS === "undefined" || !this.isIndexDBAvailable()) {
					unavailableReason = "IndexedDB filesystem persistence is unavailable.";
					return;
				}
				if (this._persistentMounts.size === 0) {
					unavailableReason = "No Uno-owned IDBFS mounts are available for a persistence checkpoint.";
					return;
				}
				for (const mount of this._persistentMounts.values()) {
					await this.runSynchronization(callback => mount.type.syncfs(mount, populate, callback));
				}
			}).then(() => {
				// Reject a no-op without poisoning later initialization; no backend work was started.
				if (unavailableReason) {
					throw new Error(unavailableReason);
				}
			});

			return result.then(
				() => onSynchronized?.(),
				(error: unknown) => {
					onSynchronized?.(error);
					throw error;
				});
		}

		private static queueSynchronization(operation: () => Promise<void>): Promise<void> {
			// Fail closed after a backend error or timeout: outstanding IndexedDB work cannot
			// be cancelled, so starting another batch could overlap it.
			this._synchronization = this._synchronization.then(operation);
			return this._synchronization;
		}

		private static runSynchronization(start: (callback: (error?: unknown) => void) => void): Promise<void> {
			return new Promise<void>((resolve, reject) => {
				const timeout = setTimeout(() => reject(new Error(
					"IndexedDB synchronization timed out. Further synchronization is disabled until the page is reloaded.")), 60000);
				const complete = (error?: unknown) => {
					clearTimeout(timeout);
					if (error) {
						reject(error);
					} else {
						resolve();
					}
				};
				try {
					start(complete);
				} catch (error: unknown) {
					clearTimeout(timeout);
					reject(error instanceof Error ? error : new Error(String(error)));
				}
			});
		}
	}
}
