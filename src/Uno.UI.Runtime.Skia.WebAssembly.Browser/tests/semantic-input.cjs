// node semantic-input.cjs <compiled Uno.Runtime.Wasm.js>
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || "playwright-core");
const assert = require("node:assert/strict");
const path = require("node:path");

(async () => {
	const browser = await chromium.launch({ channel: "msedge", headless: true });
	const failures = [];
	async function test(name, action) {
		const page = await browser.newPage();
		try {
			await page.setContent('<div id="uno-body"></div><button id="app">Application action</button>');
			await page.addScriptTag({ path: path.resolve(process.argv[2]) });
			await page.evaluate(() => {
				const api = Uno.UI.Runtime.Skia;
				window.keyEvents = [];
				window.activations = 0;
				window.appKeys = [];
				window.appClicks = 0;
				window.selections = 0;
				const app = document.getElementById("app");
				app.addEventListener("keydown", e => window.appKeys.push(e.type));
				app.addEventListener("keyup", e => window.appKeys.push(e.type));
				app.addEventListener("click", () => window.appClicks++);
				const noop = () => {};
				const accessibility = new Proxy({
					IsAutoEnableAccessibility: () => false,
					OnSelection: () => window.selections++,
					EnableAccessibility: () => {
						window.activations++;
						document.getElementById("app").focus();
					}
				}, { get: (target, key) => target[key] || noop });
				api.WebAssemblyWindowWrapper.getAssemblyExports = () => ({
					Uno: { UI: { Runtime: { Skia: {
						WebAssemblyAccessibility: accessibility,
						BrowserKeyboardInputSource: { OnNativeKeyboardEvent: (_, down, ...args) => {
							window.keyEvents.push({ down, code: args[4] });
							return 0;
						}}
					}}}}
				});
				api.BrowserKeyboardInputSource.initialize({});
				api.Accessibility.setup();
			});
			await action(page);
			console.log(`PASS: ${name}`);
		} catch (error) {
			failures.push(name);
			console.error(`FAIL: ${name}\n${error.stack}`);
		} finally {
			await page.close();
		}
	}
	try {
		for (const key of ["Enter", "Space"]) {
			await test(`${key} activation owns down, repeat and up across focus transfer`, async page => {
				await page.locator("#uno-enable-accessibility").focus();
				await page.keyboard.down(key);
				await page.keyboard.down(key);
				assert.equal(await page.evaluate(() => window.activations), 1);
				assert.deepEqual(await page.evaluate(() => window.keyEvents), []);
				await page.keyboard.up(key);
				assert.deepEqual(await page.evaluate(() => window.keyEvents), []);
				assert.deepEqual(await page.evaluate(() => window.appKeys), []);
				assert.equal(await page.evaluate(() => window.appClicks), 0);
				await page.keyboard.press(key);
				assert.equal(await page.evaluate(() => window.keyEvents.length), 2, "A fresh press must reach the app");
				assert.equal(await page.evaluate(() => window.appClicks), 1);
			});
		}
		await test("blur clears an interrupted activation press", async page => {
			await page.locator("#uno-enable-accessibility").focus();
			await page.keyboard.down("Enter");
			await page.evaluate(() => window.dispatchEvent(new Event("blur")));
			await page.keyboard.up("Enter");
			await page.evaluate(() => { window.keyEvents = []; });
			await page.keyboard.press("Enter");
			assert.equal(await page.evaluate(() => window.keyEvents.length), 2);
		});
		await test("recycled item clears name and refreshes geometry, index and count", async page => {
			const result = await page.evaluate(async () => {
				const api = Uno.UI.Runtime.Skia.SemanticElements;
				const frame = () => new Promise(resolve => requestAnimationFrame(resolve));
				const generation = api.registerVirtualizedContainer(100, "listbox", "Items", false);
				api.addVirtualizedItem(100, 101, 0, 2, 1, 2, 30, 40, "option", "old", generation);
				await frame();
				api.addVirtualizedItem(100, 101, 1, 3, 5, 6, 70, 80, "option", "", generation);
				await frame();
				const item = document.getElementById("uno-semantics-101");
				return { name: item.getAttribute("aria-label"), index: item.getAttribute("aria-posinset"),
					count: item.getAttribute("aria-setsize"), left: item.style.left, top: item.style.top,
					width: item.style.width, height: item.style.height };
			});
			assert.deepEqual(result, { name: null, index: "2", count: "3", left: "5px", top: "6px", width: "70px", height: "80px" });
		});
		await test("unmeasured virtualized placeholders stay hidden until layout and hide again on reuse", async page => {
			const hidden = await page.evaluate(async () => {
				const api = Uno.UI.Runtime.Skia.SemanticElements;
				const accessibility = Uno.UI.Runtime.Skia.Accessibility;
				const frame = () => new Promise(resolve => requestAnimationFrame(resolve));
				const generation = api.registerVirtualizedContainer(110, "listbox", "Items", false);
				api.addVirtualizedItem(110, 111, 0, 1, 0, 0, 100, 0, "option", "", generation);
				await frame();
				const item = document.getElementById("uno-semantics-111");
				const states = [item.hidden];
				accessibility.updateSemanticElementPositioning(111, 100, 0, 0, 0);
				states.push(item.hidden);
				api.addVirtualizedItem(110, 111, 0, 1, 0, 0, 100, 30, "option", "Ready", generation);
				await frame();
				states.push(item.hidden);
				api.addVirtualizedItem(110, 111, 0, 1, 0, 0, 100, 0, "option", "", generation);
				await frame();
				states.push(item.hidden);
				accessibility.updateSemanticElementPositioning(111, 100, 30, 0, 0);
				states.push(item.hidden);
				return states;
			});
			assert.deepEqual(hidden, [true, true, false, true, false]);
		});
		await test("layout changes before realization flush supersede captured geometry", async page => {
			const result = await page.evaluate(async () => {
				const api = Uno.UI.Runtime.Skia.SemanticElements;
				const accessibility = Uno.UI.Runtime.Skia.Accessibility;
				const frame = () => new Promise(resolve => requestAnimationFrame(resolve));
				const generation = api.registerVirtualizedContainer(115, "listbox", "Items", false);
				const snapshot = handle => {
					const item = document.getElementById(`uno-semantics-${handle}`);
					return [item.hidden, item.style.left, item.style.top, item.style.width, item.style.height];
				};
				api.addVirtualizedItem(115, 116, 0, 2, 0, 0, 100, 0, "option", "First", generation);
				accessibility.updateSemanticElementPositioning(116, 120, 30, 4, 5);
				api.addVirtualizedItem(115, 117, 1, 2, 0, 30, 100, 30, "option", "Second", generation);
				accessibility.updateSemanticElementPositioning(117, 140, 0, 6, 7);
				await frame();
				const initial = [snapshot(116), snapshot(117)];
				api.addVirtualizedItem(115, 116, 0, 2, 0, 0, 100, 30, "option", "First", generation);
				accessibility.updateSemanticElementPositioning(116, 160, 0, 8, 9);
				api.addVirtualizedItem(115, 117, 1, 2, 0, 0, 100, 0, "option", "Second", generation);
				accessibility.updateSemanticElementPositioning(117, 180, 40, 10, 11);
				await frame();
				return { initial, reused: [snapshot(116), snapshot(117)] };
			});
			assert.deepEqual(result, {
				initial: [[false, "4px", "5px", "120px", "30px"], [true, "6px", "7px", "140px", "0px"]],
				reused: [[true, "8px", "9px", "160px", "0px"], [false, "10px", "11px", "180px", "40px"]]
			});
		});
		await test("out-of-order realization and reindexing preserve reading order and focus", async page => {
			const result = await page.evaluate(async () => {
				const api = Uno.UI.Runtime.Skia.SemanticElements;
				const frame = () => new Promise(resolve => requestAnimationFrame(resolve));
				const generation = api.registerVirtualizedContainer(120, "listbox", "Items", false);
				api.addVirtualizedItem(120, 122, 1, 3, 0, 30, 100, 30, "option", "Second", generation);
				api.addVirtualizedItem(120, 121, 0, 3, 0, 0, 100, 30, "option", "First", generation);
				await frame();
				const container = document.getElementById("uno-semantics-120");
				const order = () => [...container.children].map(item => item.getAttribute("aria-posinset"));
				const before = order();
				document.getElementById("uno-semantics-121").focus();
				api.addVirtualizedItem(120, 121, 2, 3, 0, 60, 100, 30, "option", "Third", generation);
				await frame();
				return { before, after: order(), focus: document.activeElement.id };
			});
			assert.deepEqual(result, { before: ["1", "2"], after: ["2", "3"], focus: "uno-semantics-121" });
		});
		await test("unregistered generations cannot resurrect nodes after reattach", async page => {
			const result = await page.evaluate(async () => {
				const api = Uno.UI.Runtime.Skia.SemanticElements;
				const oldGeneration = api.registerVirtualizedContainer(200, "listbox", "Old", false);
				api.addVirtualizedItem(200, 201, 0, 1, 0, 0, 10, 10, "option", "obsolete", oldGeneration);
				api.unregisterVirtualizedContainer(200, oldGeneration);
				const newGeneration = api.registerVirtualizedContainer(200, "listbox", "New", false);
				api.addVirtualizedItem(200, 202, 0, 1, 0, 0, 10, 10, "option", "current", newGeneration);
				api.unregisterVirtualizedContainer(200, oldGeneration);
				await new Promise(resolve => requestAnimationFrame(resolve));
				return { obsolete: !!document.getElementById("uno-semantics-201"),
					current: !!document.getElementById("uno-semantics-202") };
			});
			assert.deepEqual(result, { obsolete: false, current: true });
		});
		await test("count updates include pending realizations", async page => {
			const count = await page.evaluate(async () => {
				const api = Uno.UI.Runtime.Skia.SemanticElements;
				const generation = api.registerVirtualizedContainer(300, "listbox", "", false);
				api.addVirtualizedItem(300, 301, 0, 1, 0, 0, 10, 10, "option", "item", generation);
				api.updateVirtualizedItemCount(300, 7, generation);
				await new Promise(resolve => requestAnimationFrame(resolve));
				return document.getElementById("uno-semantics-301").getAttribute("aria-setsize");
			});
			assert.equal(count, "7");
		});
		await test("moving a reused handle preserves new ownership and its activation", async page => {
			const result = await page.evaluate(async () => {
				const api = Uno.UI.Runtime.Skia.SemanticElements;
				const frame = () => new Promise(resolve => requestAnimationFrame(resolve));
				const oldGeneration = api.registerVirtualizedContainer(500, "listbox", "", false);
				const newGeneration = api.registerVirtualizedContainer(600, "listbox", "", false);
				api.addVirtualizedItem(500, 501, 0, 1, 0, 0, 10, 10, "option", "old", oldGeneration);
				await frame();
				const item = document.getElementById("uno-semantics-501");
				api.addVirtualizedItem(600, 501, 0, 1, 0, 0, 10, 10, "option", "new", newGeneration);
				await frame();
				api.removeVirtualizedItem(501, 500, oldGeneration);
				await frame();
				item.click();
				const parent = item.parentElement?.id;
				const selections = window.selections;
				api.unregisterVirtualizedContainer(600, newGeneration);
				item.click();
				return { parent, selections, afterDispose: window.selections };
			});
			assert.deepEqual(result, { parent: "uno-semantics-600", selections: 1, afterDispose: 1 });
		});
		await test("property changes before realization flush override its initial snapshot", async page => {
			const result = await page.evaluate(async () => {
				const api = Uno.UI.Runtime.Skia;
				const generation = api.SemanticElements.registerVirtualizedContainer(700, "listbox", "", false);
				api.SemanticElements.addVirtualizedItem(700, 701, 0, 1, 0, 0, 10, 10, "option", "old", generation, true, false);
				api.Accessibility.updateAriaLabel(701, "current");
				api.SemanticElements.updateDisabledState(701, false);
				api.SemanticElements.updateSelectionState(701, true);
				await new Promise(resolve => requestAnimationFrame(resolve));
				const item = document.getElementById("uno-semantics-701");
				return { name: item.getAttribute("aria-label"), disabled: item.getAttribute("aria-disabled"),
					selected: item.getAttribute("aria-selected") };
			});
			assert.deepEqual(result, { name: "current", disabled: "false", selected: "true" });
		});
		await test("live paragraph updates preserve descendants and clear own text", async page => {
			const result = await page.evaluate(() => {
				const api = Uno.UI.Runtime.Skia;
				const parent = document.createElement("div");
				parent.id = "uno-semantics-800";
				document.body.appendChild(parent);
				api.SemanticElements.createTextElement(800, 801, null, 0, 0, 100, 30, "old", true, false);
				const text = document.getElementById("uno-semantics-801");
				const link = document.createElement("a");
				link.textContent = "child";
				text.appendChild(link);
				api.Accessibility.updateAriaLabel(801, "5");
				const updated = text.firstChild.textContent;
				api.Accessibility.updateAriaLabel(801, "");
				return { updated, cleared: text.firstChild.textContent, childPreserved: text.contains(link) };
			});
			assert.deepEqual(result, { updated: "5", cleared: "", childPreserved: true });
		});
		await test("modal masking preserves ancestors, wraps Tab once, and restores background", async page => {
			await page.evaluate(() => {
				const root = document.getElementById("uno-semantics-root");
				const ancestor = document.createElement("main");
				ancestor.id = "uno-semantics-900";
				const background = document.createElement("button");
				background.id = "uno-semantics-904";
				background.textContent = "Background";
				const modal = document.createElement("div");
				modal.id = "uno-semantics-901";
				const close = document.createElement("button");
				close.id = "uno-semantics-903";
				close.textContent = "OK";
				modal.appendChild(close);
				ancestor.append(background, modal);
				root.appendChild(ancestor);
				Uno.UI.Runtime.Skia.FocusTrap.activateFocusTrap(901, 904, [903]);
			});
			assert.equal(await page.locator("#uno-semantics-900").getAttribute("aria-hidden"), null);
			assert.equal(await page.locator("#uno-semantics-904").getAttribute("aria-hidden"), "true");
			await page.keyboard.press("Tab");
			assert.equal(await page.evaluate(() => document.activeElement.id), "uno-semantics-903");
			assert.equal(await page.evaluate(() => window.keyEvents.filter(event => event.down && event.code === "Tab").length), 0);
			await page.evaluate(() => Uno.UI.Runtime.Skia.FocusTrap.deactivateFocusTrap(901));
			assert.equal(await page.locator("#uno-semantics-904").getAttribute("aria-hidden"), null);
			assert.equal(await page.evaluate(() => document.activeElement.id), "uno-semantics-904");
		});
		await test("late semantic reparenting preserves identity, focus and handlers", async page => {
			const result = await page.evaluate(() => {
				const oldParent = document.createElement("div");
				oldParent.id = "uno-semantics-950";
				const newParent = document.createElement("div");
				newParent.id = "uno-semantics-951";
				const child = document.createElement("button");
				child.id = "uno-semantics-952";
				let clicks = 0;
				child.addEventListener("click", () => clicks++);
				oldParent.appendChild(child);
				document.body.append(oldParent, newParent);
				child.focus();
				const moved = Uno.UI.Runtime.Skia.Accessibility.reparentSemanticElement(952, 951, null);
				child.click();
				return { moved, parent: child.parentElement.id, focused: document.activeElement === child,
					clicks, duplicates: document.querySelectorAll("#uno-semantics-952").length };
			});
			assert.deepEqual(result, { moved: true, parent: "uno-semantics-951", focused: true, clicks: 1, duplicates: 1 });
		});
	} finally {
		await browser.close();
	}
	assert.deepEqual(failures, [], "All semantic input/lifetime contracts must pass");
})().catch(error => {
	console.error(error);
	process.exitCode = 1;
});
