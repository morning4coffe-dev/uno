// Exercises compiled production StorageFolder JavaScript with a running app's real
// Emscripten IDBFS and browser IndexedDB. This does not replace the app's managed assembly.
// node storage-folder-browser.cjs <application-url>
// STORAGE_FOLDER_SCRIPT, PLAYWRIGHT_MODULE and PERSISTENCE_BROWSER_PROFILE select existing assets.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { randomUUID } = require("node:crypto");
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || "playwright-core");

(async () => {
    const script = fs.readFileSync(process.env.STORAGE_FOLDER_SCRIPT, "utf8");
    const profile = path.resolve(process.env.PERSISTENCE_BROWSER_PROFILE || "artifacts\\persistence-browser-profile");
    const context = await chromium.launchPersistentContext(profile, {
        channel: "msedge",
        headless: true,
        serviceWorkers: "block",
        viewport: null
    });
    const mount = `/uno-persistence-${randomUUID()}`;
    try {
        const page = await context.newPage();
        const readCheckpoint = name => page.evaluate(({ mount, name }) => new Promise((resolve, reject) => {
            const open = indexedDB.open(mount);
            open.onerror = () => reject(open.error);
            open.onsuccess = () => {
                const db = open.result;
                try {
                    const transaction = db.transaction("FILE_DATA", "readonly");
                    const request = transaction.objectStore("FILE_DATA").get(`${mount}/${name}`);
                    request.onsuccess = () => resolve(request.result
                        ? new TextDecoder().decode(request.result.contents)
                        : null);
                    transaction.onerror = () => reject(transaction.error);
                    transaction.oncomplete = () => db.close();
                    transaction.onabort = () => {
                        db.close();
                        reject(transaction.error);
                    };
                } catch (error) {
                    db.close();
                    reject(error);
                }
            };
        }), { mount, name });
        const initialize = async () => {
            await page.waitForFunction(() =>
                globalThis.DotnetExports && globalThis.FS &&
                globalThis.Windows?.Storage?.StorageFolder?._isInitialized);
            await page.addScriptTag({ content: script });
            await page.evaluate(async mount => {
                await Windows.Storage.StorageFolder.makePersistent([mount]);
            }, mount);
        };
        await page.goto(process.argv[2], { waitUntil: "domcontentloaded" });
        await initialize();
        const elapsed = await page.evaluate(async mount => {
            const start = performance.now();
            FS.writeFile(`${mount}/first.txt`, "acknowledged");
            const result = Windows.Storage.StorageFolder.synchronizeFileSystem(false);
            if (!(result instanceof Promise)) {
                throw new Error("synchronizeFileSystem must return an acknowledgement Promise");
            }
            await result;
            return performance.now() - start;
        }, mount);
        // Read IndexedDB before navigation: unload hooks must not rescue a missing checkpoint.
        assert.equal(await readCheckpoint("first.txt"), "acknowledged");
        await page.reload({ waitUntil: "domcontentloaded" });
        await initialize();
        assert.equal(await page.evaluate(mount =>
            FS.readFile(`${mount}/first.txt`, { encoding: "utf8" }), mount), "acknowledged");

        await page.evaluate(async mount => {
            FS.writeFile(`${mount}/second.txt`, "first queued checkpoint");
            const first = Windows.Storage.StorageFolder.synchronizeFileSystem(false);
            await Promise.resolve();
            FS.writeFile(`${mount}/third.txt`, "second queued checkpoint");
            const second = Windows.Storage.StorageFolder.synchronizeFileSystem(false);
            await Promise.all([first, second]);
        }, mount);
        assert.equal(await readCheckpoint("third.txt"), "second queued checkpoint");
        await page.reload({ waitUntil: "domcontentloaded" });
        await initialize();
        assert.deepEqual(await page.evaluate(mount => [
            FS.readFile(`${mount}/second.txt`, { encoding: "utf8" }),
            FS.readFile(`${mount}/third.txt`, { encoding: "utf8" })
        ], mount), ["first queued checkpoint", "second queued checkpoint"]);

        assert.equal(await page.evaluate(async mount => {
            FS.writeFile(`${mount}/unsaved.txt`, "do not overwrite");
            await Windows.Storage.StorageFolder.makePersistent([`${mount}-additional`]);
            return FS.readFile(`${mount}/unsaved.txt`, { encoding: "utf8" });
        }, mount), "do not overwrite");
        console.log(JSON.stringify({
            result: "PASS",
            backend: "real Emscripten IDBFS / Edge IndexedDB",
            immediateReloadAfterAcknowledgement: true,
            concurrentAcknowledgementsSurviveReload: true,
            indexedDBVerifiedBeforeNavigation: true,
            additionalMountPreservesUnsavedFiles: true,
            checkpointMilliseconds: elapsed,
            managedAssemblyRebuilt: false
        }, null, 2));
    } finally {
        await context.close();
    }
})().catch(error => {
    console.error(error);
    process.exitCode = 1;
});
