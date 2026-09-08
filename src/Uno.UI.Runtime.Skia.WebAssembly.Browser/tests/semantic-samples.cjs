// node semantic-samples.cjs <application-url> <evidence-directory> [RpnCalculator|TodoSQLite|TodoSQLiteFailure|Xaminals] [Click|Enter|Space]
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || "playwright-core");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const crypto = require("node:crypto");

(async () => {
	const output = path.resolve(process.argv[3]);
	fs.mkdirSync(output, { recursive: true });
	const browser = await chromium.launch({ channel: "msedge", headless: true });
	const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, serviceWorkers: "block" });
	const page = await context.newPage();
	const requests = [];
	const errors = [];
	const consoleErrors = [];
	const imageResponses = [];
	const replacements = [
		[/\/_framework\/Uno\.UI\.[^.]+\.wasm$/, process.env.UNO_TEST_UI_DLL],
		[/\/_framework\/Uno\.UI\.Runtime\.Skia\.WebAssembly\.Browser\.[^.]+\.wasm$/, process.env.UNO_TEST_BROWSER_DLL],
		[/\/Uno\.Runtime\.Wasm\.js$/, process.env.UNO_TEST_BROWSER_SCRIPT]
	].filter(([, file]) => file);
	const replaced = [];
	if (replacements.length) {
		const integrity = replacements.map(([pattern, file]) => ({
			pattern: pattern.source,
			hash: `sha256-${crypto.createHash("sha256").update(fs.readFileSync(file)).digest("base64")}`
		}));
		await page.addInitScript(integrity => {
			const originalFetch = window.fetch.bind(window);
			window.fetch = (input, init) => {
				const url = new URL(typeof input === "string" ? input : input instanceof URL ? input.href : input.url, document.baseURI);
				const replacement = integrity.find(item => new RegExp(item.pattern).test(url.pathname));
				return originalFetch(input, replacement ? { ...init, integrity: replacement.hash } : init);
			};
		}, integrity);
		await page.route("**/*", async route => {
			const replacement = replacements.find(([pattern]) => pattern.test(new URL(route.request().url()).pathname));
			if (replacement) {
				replaced.push(route.request().url());
				await route.fulfill({ status: 200, path: path.resolve(replacement[1]),
					contentType: replacement[1].endsWith(".js") ? "application/javascript" : "application/octet-stream" });
			} else {
				await route.continue();
			}
		});
	}
	page.on("request", request => requests.push(request.url()));
	page.on("pageerror", error => errors.push(error.stack || error.message));
	page.on("response", response => {
		if (response.url().startsWith("https://upload.wikimedia.org/")) {
			imageResponses.push({ url: response.url(), status: response.status() });
		}
	});
	page.on("console", message => {
		if (message.type() === "error") consoleErrors.push(message.text());
	});
	try {
		await page.goto(process.argv[2], { waitUntil: "domcontentloaded" });
		await page.waitForFunction(() => !document.querySelector("#loading") && document.querySelector("#uno-canvas")?.hasAttribute("width"), null, { timeout: 120000 });
		const activation = process.argv[5] || "Click";
		assert(["Click", "Enter", "Space"].includes(activation));
		if (process.argv[4] === "Xaminals") {
			await page.evaluate(() => {
				const keyboard = Uno.UI.Runtime.Skia.BrowserKeyboardInputSource;
				if (typeof keyboard._exports?.OnNativeKeyboardEvent !== "function") throw new Error("Managed keyboard bridge is not ready");
				window.activationKeys = [];
				window.originalKeyboardExports = keyboard._exports;
				keyboard._exports = { OnNativeKeyboardEvent: (...args) => {
					window.activationKeys.push({ down: args[1], code: args[6], key: args[7] });
					return window.originalKeyboardExports.OnNativeKeyboardEvent(...args);
				}};
			});
		}
		if (activation === "Click") {
			await page.locator("#uno-enable-accessibility").evaluate(element => element.click());
		} else {
			await page.locator("#uno-enable-accessibility").focus();
			await page.keyboard.down(activation);
			await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
			await page.keyboard.down(activation);
			if (process.argv[4] === "Xaminals") {
				assert.equal(await page.getByRole("paragraph").filter({ hasText: /^Cats$/ }).count(), 1);
				assert.equal(await page.getByRole("dialog").count(), 0);
			}
			await page.keyboard.up(activation);
		}
		if (process.argv[4] === "Xaminals") {
			const forwardedKeys = await page.evaluate(() => {
				Uno.UI.Runtime.Skia.BrowserKeyboardInputSource._exports = window.originalKeyboardExports;
				return window.activationKeys;
			});
			fs.writeFileSync(path.join(output, "activation-forwarded-keys.json"), JSON.stringify(forwardedKeys, null, 2));
			assert.deepEqual(forwardedKeys, [], "Accessibility activation must not forward any key phase to the app");
			await exerciseCategories(page, output, activation);
			assert.deepEqual(errors, []);
			return;
		}
		if (process.argv[4] === "TodoSQLite") {
			await exerciseCollection(page, output);
			assert.deepEqual(errors, []);
			return;
		}
		if (process.argv[4] === "TodoSQLiteFailure") {
			await exerciseFailureDialog(page, output);
			assert.deepEqual(errors, []);
			return;
		}
		const resultText = page.getByRole("application").getByRole("paragraph").filter({ hasText: /^5$/ });
		const unary = page.getByRole("button", { name: "√", exact: true });
		for (let iteration = 0; iteration < 2; iteration++) {
			assert.equal(await resultText.count(), 0);
			assert.equal(await unary.isEnabled(), false);
			for (const name of ["C", "2", "ENTER", "3", "ENTER", "+"]) {
				await page.getByRole("button", { name, exact: true }).click({ force: true });
			}
			await page.waitForTimeout(500);
			await page.screenshot({ path: path.join(output, "result.png") });
			fs.writeFileSync(path.join(output, "result.txt"), await page.locator("body").ariaSnapshot());
			assert.equal(await resultText.count(), 1, "The initially-empty result must enter the semantic tree as 5");
			assert.equal(await unary.isEnabled(), true, "Unary operations must become semantically enabled");
			await page.getByRole("button", { name: "C", exact: true }).click({ force: true });
			await page.waitForTimeout(250);
			assert.equal(await resultText.count(), 0, "Clearing the result must remove its old value");
			assert.equal(await unary.isEnabled(), false, "Clearing must disable unary operations again");
		}
		assert.deepEqual(errors, []);
		console.log("PASS: RPN result membership and enabled-state transitions");
	} finally {
		await page.screenshot({ path: path.join(output, "final.png") });
		fs.writeFileSync(path.join(output, "final.txt"), await page.locator("body").ariaSnapshot());
		fs.writeFileSync(path.join(output, "final-rows.json"), JSON.stringify(await page.getByRole("listbox").getByRole("option").evaluateAll(elements =>
			elements.map(element => ({
				name: element.getAttribute("aria-label"),
				index: element.getAttribute("aria-posinset"),
				bounds: element.getBoundingClientRect().toJSON()
			}))), null, 2));
		fs.writeFileSync(path.join(output, "requests.json"), JSON.stringify(requests, null, 2));
		fs.writeFileSync(path.join(output, "errors.json"), JSON.stringify(errors, null, 2));
		fs.writeFileSync(path.join(output, "console-errors.json"), JSON.stringify(consoleErrors, null, 2));
		fs.writeFileSync(path.join(output, "image-responses.json"), JSON.stringify(imageResponses, null, 2));
		fs.writeFileSync(path.join(output, "network-overrides.json"), JSON.stringify({ replacements: replacements.map(([, file]) => file), replaced }, null, 2));
		await browser.close();
	}
})().catch(error => {
	console.error(error);
	process.exitCode = 1;
});

async function exerciseCollection(page, output) {
	async function replaceText(locator, text) {
		await locator.click({ force: true });
		await page.keyboard.press("ControlOrMeta+A");
		await page.keyboard.type(text);
		await page.keyboard.press("Tab");
		// TextBox.TextChanged is dispatched asynchronously, as in WinUI; MAUI updates its
		// bound value from that event. Await the normal dispatcher/render turn before Save.
		await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
		assert.equal(await locator.inputValue(), text, "The target input must contain the typed value before Save");
	}

	await page.getByRole("button", { name: "Add New Item", exact: true }).click({ force: true });
	for (const title of ["Accessible task", "Renamed task", "Task after reattach"]) {
		await page.getByRole("button", { name: "Save", exact: true }).waitFor();
		await replaceText(page.getByRole("textbox").first(), title);
		await page.getByRole("button", { name: "Save", exact: true }).click({ force: true });
		await page.getByRole("button", { name: "Add New Item", exact: true }).waitFor();
		const row = page.getByRole("listbox").getByRole("option", { name: title, exact: true });
		await row.waitFor({ timeout: 10000 });
		assert.equal(await page.getByRole("listbox").getByRole("option").count(), 1);
		assert.equal(await page.locator('[aria-label*="ItemTemplateContext"]').count(), 0);
		await page.screenshot({ path: path.join(output, "result.png") });
		fs.writeFileSync(path.join(output, "result.txt"), await page.locator("body").ariaSnapshot());
		await row.click({ force: true });
		await page.getByRole("button", { name: "Save", exact: true }).waitFor();
		assert.equal(await page.getByRole("textbox").first().inputValue(), title);
	}
	console.log("PASS: generic realized-template names, updates and repeated navigation/reattach");
}

async function exerciseCategories(page, output, activation) {
	const paragraphs = page.getByRole("paragraph");
	assert.equal(await paragraphs.filter({ hasText: /^Cats$/ }).count(), 1);
	const category = page.getByRole("option", { name: "Monkeys", exact: true });
	await category.waitFor();
	await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
	const bounds = await category.boundingBox();
	assert(bounds && bounds.width > 0 && bounds.height > 0);
	fs.writeFileSync(path.join(output, "category-bounds.json"), JSON.stringify({ activation, bounds }, null, 2));
	await page.mouse.click(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
	await paragraphs.filter({ hasText: /^Monkeys$/ }).waitFor({ timeout: 10000 });
	assert.equal(await paragraphs.filter({ hasText: /^Monkeys$/ }).count(), 1);
	assert.equal(await paragraphs.filter({ hasText: /^Cats$/ }).count(), 0);
	await page.waitForFunction(() => {
		const rows = [...document.querySelectorAll('[role="listbox"] > [role="option"]:not([hidden])')];
		return rows.length > 0 && rows.every(row =>
			row.getAttribute("aria-setsize") === "17" && row.getAttribute("aria-label")?.trim());
	}, null, { timeout: 10000 });
	const rows = page.getByRole("listbox").getByRole("option");
	assert(await rows.count() > 0);
	const rowState = await rows.evaluateAll(elements => elements.map(element => ({
		name: element.getAttribute("aria-label"),
		index: element.getAttribute("aria-posinset"),
		size: element.getAttribute("aria-setsize")
	})));
	assert(rowState.every(row => row.size === "17" && Number(row.index) >= 1 && Number(row.index) <= 17));
	assert(rowState.every(row => row.name?.trim() && !row.name.includes("ItemTemplateContext")));
	assert.equal(rowState.find(row => row.index === "1")?.name, "Baboon, Africa & Asia");
	assert.equal(new Set(rowState.map(row => row.index)).size, rowState.length);
	fs.writeFileSync(path.join(output, "row-state.json"), JSON.stringify(rowState, null, 2));
	assert.equal(await paragraphs.filter({ hasText: /^(Sri Lanka|Ethiopia)$/ }).count(), 0,
		"Detached item templates must not leak body text at the application root");
	await page.mouse.move(700, 500);
	await page.mouse.wheel(0, 1400);
	const last = page.getByRole("listbox").getByRole("option", { name: "Gelada, Ethiopia", exact: true });
	await last.waitFor({ timeout: 10000 });
	await page.waitForFunction(() => {
		const row = document.querySelector('[role="listbox"] > [aria-posinset="17"]:not([hidden])');
		const bounds = row?.getBoundingClientRect();
		return bounds && bounds.height > 0 && bounds.top >= 96 && bounds.bottom <= 900;
	}, null, { timeout: 10000 });
	assert.equal(await last.getAttribute("aria-posinset"), "17");
	assert.equal(await last.getAttribute("aria-setsize"), "17");
	const lastBounds = await last.boundingBox();
	assert(lastBounds && lastBounds.height > 0 && lastBounds.y >= 96 && lastBounds.y + lastBounds.height <= 900,
		"Native wheel input must bring the last logical item into the visual viewport");
	assert.equal(await paragraphs.filter({ hasText: /^(Sri Lanka|Ethiopia)$/ }).count(), 0);
	assert((await rows.evaluateAll(elements => elements.map(element => element.getAttribute("aria-label"))))
		.every(name => name?.trim() && !name.includes("ItemTemplateContext")));
	const indices = await rows.evaluateAll(elements => elements.map(element => Number(element.getAttribute("aria-posinset"))));
	assert.deepEqual(indices, [...indices].sort((a, b) => a - b), "Reading order must follow logical item order after realization");
	await page.screenshot({ path: path.join(output, "scrolled-monkeys.png") });
	const domesticBounds = await page.getByRole("option", { name: "Domestic", exact: true }).boundingBox();
	assert(domesticBounds && domesticBounds.width > 0 && domesticBounds.height > 0);
	await page.mouse.click(domesticBounds.x + domesticBounds.width / 2, domesticBounds.y + domesticBounds.height / 2);
	await page.getByRole("listbox").getByRole("option", { name: "Abyssinian, Ethopia", exact: true }).waitFor({ timeout: 10000 });
	assert.equal(await paragraphs.filter({ hasText: /^Cats$/ }).count(), 1);
	assert.equal(await paragraphs.filter({ hasText: /^Monkeys$/ }).count(), 0);
	assert((await rows.evaluateAll(elements => elements.map(element => element.getAttribute("aria-setsize"))))
		.every(size => size === "11"), "Returning to the prior page must restore its own logical item set");
	console.log(`PASS: ${activation} activation owns all key phases; named ordered rows survive scrolling and prior-page restoration`);
}

async function exerciseFailureDialog(page, output) {
	const title = "Save could not be confirmed";
	const value = "Unacknowledged accessible record";
	await page.getByRole("button", { name: "Add New Item", exact: true }).click({ force: true });
	await page.getByRole("button", { name: "Save", exact: true }).waitFor();
	await page.getByRole("textbox").first().click({ force: true });
	await page.keyboard.press("ControlOrMeta+A");
	await page.keyboard.type(value);
	await page.keyboard.press("Tab");
	await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
	assert.equal(await page.getByRole("textbox").first().inputValue(), value);
	await page.evaluate(() => {
		const original = IDBDatabase.prototype.transaction;
		window.restoreCheckpointTransaction = () => { IDBDatabase.prototype.transaction = original; };
		IDBDatabase.prototype.transaction = function (...args) {
			if (String(args[0]).includes("FILE_DATA")) {
				throw new DOMException("Injected checkpoint storage failure", "QuotaExceededError");
			}
			return original.apply(this, args);
		};
	});
	try {
		for (let iteration = 0; iteration < 2; iteration++) {
			await page.getByRole("button", { name: "Save", exact: true }).click({ force: true });
			await page.getByRole("dialog").waitFor();
			const dialog = page.getByRole("dialog", { name: title, exact: true });
			await dialog.waitFor({ timeout: 10000 });
			await dialog.getByText(title, { exact: true }).waitFor({ timeout: 10000 });
			await dialog.getByText("QuotaExceededError: Injected checkpoint storage failure", { exact: true }).waitFor({ timeout: 10000 });
			assert.equal(await page.getByRole("dialog").count(), 1);
			assert(await dialog.getByText(title, { exact: true }).evaluate(element => {
				const body = [...element.closest('[role="dialog"]').querySelectorAll("p")]
					.find(paragraph => paragraph.textContent === "QuotaExceededError: Injected checkpoint storage failure");
				return !!body && !!(element.compareDocumentPosition(body) & Node.DOCUMENT_POSITION_FOLLOWING);
			}), "Dialog title must precede its body in reading order");
			const ok = dialog.getByRole("button", { name: "OK", exact: true });
			await ok.focus();
			await page.keyboard.press("Tab");
			assert(await dialog.evaluate(element => element.contains(document.activeElement)), "Tab must stay inside the modal");
			await page.keyboard.press("Shift+Tab");
			assert(await dialog.evaluate(element => element.contains(document.activeElement)), "Shift+Tab must stay inside the modal");
			await page.screenshot({ path: path.join(output, `dialog-${iteration + 1}.png`) });
			fs.writeFileSync(path.join(output, `dialog-${iteration + 1}.txt`), await page.locator("body").ariaSnapshot());
			if (iteration === 0) {
				await ok.click({ force: true });
			} else {
				await ok.focus();
				await page.keyboard.press("Enter");
			}
			await page.getByRole("dialog").waitFor({ state: "hidden" });
			assert.equal(await page.getByRole("textbox").first().inputValue(), value);
		}
		console.log("PASS: checkpoint failure dialog exposes title/body and survives close/reopen without losing the edit form");
	} finally {
		await page.evaluate(() => window.restoreCheckpointTransaction());
	}
}
