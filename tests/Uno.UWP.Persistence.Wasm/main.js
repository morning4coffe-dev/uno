import { dotnet } from "./_framework/dotnet.js";

const query = new URLSearchParams(location.search);
const mode = query.get("mode") || "checkpoint";
let sharedReady;
const whenSharedReady = new Promise(resolve => { sharedReady = resolve; });
let releaseShared;
globalThis.fixture = {
    report(result) {
        globalThis.fixtureResult = result;
        document.getElementById("result").textContent = result;
    },
    waitForShared: () => whenSharedReady.then(() => "ready"),
    releaseShared: () => releaseShared()
};

try {
    const runtime = await dotnet.create();
    globalThis.Module = runtime.Module;
    Module.getAssemblyExports = runtime.getAssemblyExports;
    globalThis.FS = Module.FS;
    globalThis.IDBFS = Module.IDBFS;
    if (!FS || !IDBFS) {
        throw new Error("The fixture must be built with real Emscripten FS and IDBFS exports.");
    }
    await Windows.ApplicationModel.Core.CoreApplication.initializeExports();
    // This non-UI fixture has no NativeDispatcher. Release the real export-ready gate
    // after loading the same assemblies/exports used by CoreApplication initialization.
    Windows.ApplicationModel.Core.CoreApplication._initializedExportsResolve();

    if (mode === "startup-failure") {
        const open = indexedDB.open.bind(indexedDB);
        indexedDB.open = (name, ...args) => {
            if (name === "/local") {
                throw new DOMException("controlled IndexedDB open failure", "SecurityError");
            }
            return open(name, ...args);
        };
    } else if (mode === "disabled") {
        globalThis.IDBFS = undefined;
    } else if (mode === "external") {
        FS.mkdir("/external");
        FS.mount(IDBFS, {}, "/external");
    } else if (mode === "concurrent-initialization") {
        const syncfs = IDBFS.syncfs.bind(IDBFS);
        IDBFS.syncfs = (mount, populate, callback) => syncfs(mount, populate, error => {
            if (populate && mount.mountpoint === "/shared") {
                releaseShared = () => callback(error);
                sharedReady();
            } else {
                callback(error);
            }
        });
    }
    await runtime.runMain("Uno.UWP.Persistence.Wasm", [mode, query.get("token") || "fixture"]);
} catch (error) {
    fixture.report("FAIL|" + error.stack);
}
