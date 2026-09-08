// Compile StorageFolder.ts, then run:
// node --test storage-folder.cjs (STORAGE_FOLDER_SCRIPT selects the compiled JavaScript).
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const { test } = require("node:test");

const source = fs.readFileSync(process.env.STORAGE_FOLDER_SCRIPT, "utf8");
const tick = async () => {
    for (let i = 0; i < 12; i++) {
        await Promise.resolve();
    }
};

function createStorage({ indexedDB = {}, idbfs = true, syncThrows = false } = {}) {
    const requests = [];
    const events = {};
    const timers = new Map();
    const intervals = [];
    const errors = [];
    const mounts = [];
    let nextTimer = 0;
    const request = (populate, callback, path) => {
        if (syncThrows) {
            throw new Error("sync threw");
        }
        requests.push({ populate, callback, path });
    };
    const context = vm.createContext({
        Windows: { ApplicationModel: { Core: { CoreApplication: { waitForInitialized: async () => {} } } } },
        FS: {
            mkdir: () => {},
            mount: (type, options, path) => {
                const mount = { type, mountpoint: path };
                mounts.push(mount);
                return { mount };
            },
            syncfs: (populate, callback) => request(populate, callback)
        },
        window: {
            indexedDB,
            addEventListener: (name, callback) => { events[name] = callback; }
        },
        document: {
            visibilityState: "visible",
            addEventListener: (name, callback) => { events[name] = callback; }
        },
        setInterval: (callback, delay) => { intervals.push({ callback, delay }); },
        setTimeout: (callback, delay) => {
            timers.set(++nextTimer, { callback, delay });
            return nextTimer;
        },
        clearTimeout: id => timers.delete(id),
        console: { warn: () => {}, error: error => errors.push(error) },
        DotnetExports: { Uno: { Windows: { Storage: { StorageFolder: { DispatchStorageInitialized: () => {} } } } } }
    });
    if (idbfs) {
        context.IDBFS = { syncfs: (mount, populate, callback) => request(populate, callback, mount.mountpoint) };
    }
    vm.runInContext(source, context);
    return { storage: context.Windows.Storage.StorageFolder, requests, events, timers, intervals, errors, mounts, context };
}

test("makePersistent does not resolve before initial population", async () => {
    const h = createStorage();
    let completed = false;
    const result = h.storage.makePersistent(["/local"]).then(() => { completed = true; });
    await tick();
    assert.equal(completed, false);
    assert.equal(h.requests[0].populate, true);
    h.requests[0].callback(null);
    await result;
    assert.equal(completed, true);
});

test("concurrent mixed requests retain order and every callback", async () => {
    const h = createStorage();
    h.storage.setupStorage("/local");
    const callbacks = [];
    const results = [true, false, true].map((populate, index) =>
        h.storage.synchronizeFileSystem(populate, error => {
            assert.equal(error, undefined);
            callbacks.push(index);
        }));
    await tick();
    assert.equal(h.requests.length, 1);
    h.requests[0].callback(null);
    await tick();
    assert.equal(h.requests.length, 2);
    h.requests[1].callback(null);
    await tick();
    assert.equal(h.requests.length, 3);
    h.requests[2].callback(null);
    await Promise.all(results);
    assert.deepEqual(h.requests.map(request => request.populate), [true, false, true]);
    assert.deepEqual(callbacks, [0, 1, 2]);
});

test("callback errors do not corrupt the backend queue", async () => {
    const h = createStorage();
    h.storage.setupStorage("/local");
    const first = h.storage.synchronizeFileSystem(false, () => { throw new Error("callback failed"); });
    const rejected = assert.rejects(first, /callback failed/);
    const second = h.storage.synchronizeFileSystem(false);
    await tick();
    h.requests[0].callback(null);
    await tick();
    h.requests[1].callback(null);
    await Promise.all([rejected, second]);
});

test("sync failure rejects current and queued requests without overlapping a failed batch", async () => {
    const h = createStorage();
    h.storage.setupStorage("/local");
    const error = new Error("quota exceeded");
    const callbacks = [];
    const first = h.storage.synchronizeFileSystem(false, error => callbacks.push(error));
    const second = h.storage.synchronizeFileSystem(true, error => callbacks.push(error));
    const rejected = [first, second].map(result => assert.rejects(result, /quota exceeded/));
    await tick();
    h.requests[0].callback(error);
    await Promise.all(rejected);
    assert.deepEqual(callbacks, [error, error]);
    assert.equal(h.requests.length, 1);
    await assert.rejects(h.storage.synchronizeFileSystem(false), /quota exceeded/);
});

test("synchronous backend exceptions reject rather than wedge the queue", async () => {
    const h = createStorage({ syncThrows: true });
    h.storage.setupStorage("/local");
    await assert.rejects(h.storage.synchronizeFileSystem(false), /sync threw/);
    await assert.rejects(h.storage.synchronizeFileSystem(true), /sync threw/);
});

test("falsy thrown values are failures, not successful callback completions", async () => {
    for (const reason of [undefined, null, false, 0, ""]) {
        const h = createStorage();
        h.storage.setupStorage("/local");
        h.context.IDBFS.syncfs = () => { throw reason; };
        await assert.rejects(h.storage.synchronizeFileSystem(false));
        await assert.rejects(h.storage.synchronizeFileSystem(true));
        assert.equal(h.timers.size, 0);
    }
});

test("initialization failure is not success and does not start background writes", async () => {
    const h = createStorage();
    const result = h.storage.makePersistent(["/local"]);
    const rejected = assert.rejects(result, /restore failed/);
    await tick();
    h.requests[0].callback(new Error("restore failed"));
    await rejected;
    assert.equal(h.intervals.length, 0);
    assert.deepEqual(Object.keys(h.events), []);
    await assert.rejects(h.storage.makePersistent(["/local"]), /restore failed/);
});

test("a stalled backend rejects pending requests; a late callback cannot start another sync", async () => {
    const h = createStorage();
    h.storage.setupStorage("/local");
    const first = h.storage.synchronizeFileSystem(false);
    const second = h.storage.synchronizeFileSystem(true);
    const rejected = [first, second].map(result => assert.rejects(result, /timed out/i));
    await tick();
    assert.equal(h.timers.size, 1);
    h.timers.values().next().value.callback();
    await Promise.all(rejected);
    h.requests[0].callback(null);
    await tick();
    assert.equal(h.requests.length, 1);
});

test("disabled IDBFS preserves non-persistent startup without background work", async () => {
    const h = createStorage({ idbfs: false });
    await h.storage.makePersistent(["/local", "/roaming"]);
    assert.equal(h.requests.length, 0);
    assert.equal(h.mounts.length, 0);
    assert.equal(h.intervals.length, 0);
    await assert.rejects(h.storage.synchronizeFileSystem(false), /persistence is unavailable/);
});

test("an absent or throwing IndexedDB API is unavailable", async () => {
    const h = createStorage({ indexedDB: null });
    assert.equal(h.storage.isIndexDBAvailable(), false);
    await h.storage.makePersistent(["/local"]);
    assert.equal(h.requests.length, 0);
    await assert.rejects(h.storage.synchronizeFileSystem(false), /persistence is unavailable/);
    Object.defineProperty(h.context.window, "indexedDB", { get: () => { throw new Error("denied"); } });
    assert.equal(h.storage.isIndexDBAvailable(), false);
});

test("new mounts are populated without overwriting existing unsynchronized mounts", async () => {
    const h = createStorage();
    const first = h.storage.makePersistent(["/local"]);
    await tick();
    h.requests[0].callback(null);
    await first;
    const second = h.storage.makePersistent(["/local", "/cache"]);
    await tick();
    assert.equal(h.requests[1].path, "/cache");
    assert.equal(h.requests[1].populate, true);
    assert.deepEqual(h.mounts.map(mount => mount.mountpoint), ["/local", "/cache"]);
    h.requests[1].callback(null);
    await second;
    assert.equal(h.intervals.length, 1);
});

test("all initial mounts finish before queued flushes and additional initialization", async () => {
    const h = createStorage();
    const initial = h.storage.makePersistent(["/local", "/roaming"]);
    await tick();
    const flush = h.storage.synchronizeFileSystem(false);
    const additional = h.storage.makePersistent(["/cache"]);
    for (let i = 0; i < 5; i++) {
        await tick();
        assert.equal(h.requests.length, i + 1);
        h.requests[i].callback(null);
    }
    await Promise.all([initial, flush, additional]);
    assert.deepEqual(h.requests.map(request => [request.path, request.populate]), [
        ["/local", true], ["/roaming", true], ["/local", false], ["/roaming", false], ["/cache", true]
    ]);
    assert.equal(h.timers.size, 0);
    assert.equal(h.intervals.length, 1);
});

test("no owned mount rejects a checkpoint without preventing later initialization", async () => {
    const h = createStorage();
    await assert.rejects(h.storage.synchronizeFileSystem(false), /No Uno-owned IDBFS mounts/);
    assert.equal(h.requests.length, 0);
    const initialized = h.storage.makePersistent(["/local"]);
    await tick();
    h.requests[0].callback(null);
    await initialized;
});

test("external mounts are not acknowledged by Uno checkpoints", async () => {
    const h = createStorage();
    h.context.FS.mount(h.context.IDBFS, {}, "/external");
    await assert.rejects(h.storage.synchronizeFileSystem(false), /No Uno-owned IDBFS mounts/);
    h.storage.setupStorage("/local");
    const checkpoint = h.storage.synchronizeFileSystem(false);
    await tick();
    assert.deepEqual(h.requests.map(request => request.path), ["/local"]);
    h.requests[0].callback(null);
    await checkpoint;
});

test("lifecycle synchronization is best effort and handles errors", async () => {
    const h = createStorage();
    const initialized = h.storage.makePersistent(["/local"]);
    await tick();
    h.requests[0].callback(null);
    await initialized;
    assert.equal(h.intervals[0].delay, 10000);
    assert.equal(typeof h.events.pagehide, "function");
    h.context.document.visibilityState = "hidden";
    h.events.visibilitychange();
    await tick();
    assert.equal(h.requests[1].populate, false);
    h.requests[1].callback(new Error("background failure"));
    await tick();
    assert.equal(h.errors.length, 1);
});
