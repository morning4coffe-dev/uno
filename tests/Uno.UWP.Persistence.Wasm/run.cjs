const assert = require("node:assert/strict");
const path = require("node:path");
const { randomUUID } = require("node:crypto");
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || "playwright-core");

(async () => {
    const context = await chromium.launchPersistentContext(
        path.resolve(process.env.PERSISTENCE_BROWSER_PROFILE || "artifacts\\persistence-fixture-profile"),
        { channel: "msedge", headless: true, viewport: null, serviceWorkers: "block" });
    const results = [];
    try {
        const page = await context.newPage();
        const errors = [];
        page.on("pageerror", error => errors.push(error.message));
        const token = randomUUID();
        for (const mode of ["checkpoint", "restore", "no-mounts", "external", "concurrent-initialization", "startup-failure", "disabled"]) {
            await page.goto(`${process.argv[2]}?mode=${mode}&token=${token}`, { waitUntil: "domcontentloaded" });
            await page.waitForFunction(() => globalThis.fixtureResult, null, { timeout: 90000 });
            const result = await page.evaluate(() => fixtureResult);
            assert.ok(result.startsWith("PASS|"), `${mode}: ${result}`);
            results.push({ mode, result });
            if (mode === "checkpoint") {
                const persisted = await page.evaluate(() => new Promise((resolve, reject) => {
                    const open = indexedDB.open("/local");
                    open.onerror = () => reject(open.error);
                    open.onsuccess = () => {
                        const db = open.result;
                        const transaction = db.transaction("FILE_DATA", "readonly");
                        const read = transaction.objectStore("FILE_DATA").get("/local/fixture-second.txt");
                        read.onsuccess = () => resolve(read.result ? new TextDecoder().decode(read.result.contents) : null);
                        transaction.oncomplete = () => db.close();
                        transaction.onerror = () => reject(transaction.error);
                    };
                }));
                assert.equal(persisted, token, "The C# acknowledgement must reach IndexedDB before navigation.");
            }
        }
        assert.deepEqual(errors, [], "Unexpected browser exceptions");
        console.log(JSON.stringify({ result: "PASS", compiledManagedBridge: true, indexedDBVerifiedBeforeNavigation: true, results }, null, 2));
    } finally {
        await context.close();
    }
})().catch(error => {
    console.error(error);
    process.exitCode = 1;
});
