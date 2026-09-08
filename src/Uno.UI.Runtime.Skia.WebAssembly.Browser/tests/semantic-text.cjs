// Run against a built Skia-WASM application: node semantic-text.cjs <application-url>.
// Uses an existing playwright-core installation (optionally selected by PLAYWRIGHT_MODULE).
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || "playwright-core");
const assert = require("node:assert/strict");

(async () => {
	const browser = await chromium.launch({ channel: "msedge", headless: true });
	try {
		const context = await browser.newContext({ serviceWorkers: "block" });
		const page = await context.newPage();
		await page.goto(process.argv[2], { waitUntil: "domcontentloaded" });
		await page.waitForFunction(() => globalThis.DotnetExports && globalThis.Uno?.UI?.Runtime?.Skia?.SemanticElements);
		const results = await page.evaluate(() => {
			const api = Uno.UI.Runtime.Skia;
			const parentHandle = 2147000000;
			const parent = document.createElement("div");
			parent.id = `uno-semantics-${parentHandle}`;
			document.body.append(parent);
			try {
				api.SemanticElements.createTextElement(parentHandle, parentHandle + 1, null, 0, 0, 100, 30, "old text", true, false);
				const paragraph = api.Accessibility.getSemanticElementByHandle(parentHandle + 1);
				api.Accessibility.updateAriaLabel(parentHandle + 1, "new text");
				const paragraphResult = { text: paragraph.textContent, label: paragraph.getAttribute("aria-label") };
				const child = document.createElement("a");
				child.textContent = "child link";
				paragraph.append(child);
				api.Accessibility.updateAriaLabel(parentHandle + 1, "changed ");
				const preservedChild = paragraph.contains(child);
				const ownText = paragraph.firstChild.textContent;
				api.Accessibility.updateAriaLabel(parentHandle + 1, "");
				const clearedText = paragraph.firstChild.textContent;
				api.SemanticElements.createHeadingElement(parentHandle, parentHandle + 2, null, 0, 0, 100, 30, 2, "old heading", false);
				api.Accessibility.updateAriaLabel(parentHandle + 2, " new heading ");
				const heading = api.Accessibility.getSemanticElementByHandle(parentHandle + 2);
				const button = document.createElement("button");
				button.id = `uno-semantics-${parentHandle + 3}`;
				button.textContent = "button content";
				parent.append(button);
				api.Accessibility.updateAriaLabel(parentHandle + 3, " button name ");
				return { paragraphResult, preservedChild, ownText, clearedText, headingText: heading.textContent, headingName: heading.getAttribute("aria-label"), buttonText: button.textContent, buttonName: button.getAttribute("aria-label") };
			} finally {
				parent.remove();
			}
		});
		assert.deepEqual(results.paragraphResult, { text: "new text", label: null });
		assert.equal(results.preservedChild, true);
		assert.equal(results.ownText, "changed ");
		assert.equal(results.clearedText, "");
		assert.equal(results.headingText, "new heading");
		assert.equal(results.headingName, "new heading");
		assert.equal(results.buttonText, "button content");
		assert.equal(results.buttonName, "button name");
		console.log("PASS: live semantic text, child preservation, clearing, heading and button contracts");
	} finally {
		await browser.close();
	}
})().catch(error => {
	console.error(error);
	process.exitCode = 1;
});
