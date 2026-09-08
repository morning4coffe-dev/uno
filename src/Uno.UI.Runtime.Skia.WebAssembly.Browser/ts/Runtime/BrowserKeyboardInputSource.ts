namespace Uno.UI.Runtime.Skia {
	export class BrowserKeyboardInputSource {
		private static _exports: any;
		private static _source: any;
		private static _accessibilityActivationKey: string | null = null;

		public static consumeAccessibilityActivation(evt: KeyboardEvent): void {
			BrowserKeyboardInputSource.clearAccessibilityActivation();
			BrowserKeyboardInputSource._accessibilityActivationKey = evt.code || evt.key;
			evt.preventDefault();
			evt.stopImmediatePropagation();
			// Activation moves focus and removes its target. Own the rest of this press before
			// it reaches either a semantic control's handlers or the managed keyboard bridge.
			document.addEventListener("keydown", BrowserKeyboardInputSource.suppressAccessibilityActivation, true);
			document.addEventListener("keyup", BrowserKeyboardInputSource.suppressAccessibilityActivation, true);
			window.addEventListener("blur", BrowserKeyboardInputSource.clearAccessibilityActivation);
		}

		private static suppressAccessibilityActivation(evt: KeyboardEvent): void {
			if ((evt.code || evt.key) === BrowserKeyboardInputSource._accessibilityActivationKey) {
				evt.preventDefault();
				evt.stopImmediatePropagation();
				if (evt.type === "keyup") {
					BrowserKeyboardInputSource.clearAccessibilityActivation();
				}
			}
		}

		private static clearAccessibilityActivation(): void {
			BrowserKeyboardInputSource._accessibilityActivationKey = null;
			document.removeEventListener("keydown", BrowserKeyboardInputSource.suppressAccessibilityActivation, true);
			document.removeEventListener("keyup", BrowserKeyboardInputSource.suppressAccessibilityActivation, true);
			window.removeEventListener("blur", BrowserKeyboardInputSource.clearAccessibilityActivation);
		}

		public static initialize(inputSource: any): any {
			if (BrowserKeyboardInputSource._exports == undefined) {
				const browserExports = WebAssemblyWindowWrapper.getAssemblyExports();
				BrowserKeyboardInputSource._exports = browserExports.Uno.UI.Runtime.Skia.BrowserKeyboardInputSource;
			}

			BrowserKeyboardInputSource._source = inputSource;
			BrowserKeyboardInputSource.subscribeKeyboardEvents();
		}

		private static subscribeKeyboardEvents() {
			document.addEventListener("keydown", BrowserKeyboardInputSource.onKeyboardEvent);
			document.addEventListener("keyup", BrowserKeyboardInputSource.onKeyboardEvent);
		}

		private static onKeyboardEvent(evt: KeyboardEvent): void {
			// When the Enable Accessibility button is still in the DOM,
			// allow Tab to reach it via native browser focus navigation
			// before the managed FocusManager intercepts it.
			if (evt.key === "Tab" && Accessibility.isEnableAccessibilityButtonActive()) {
				const active = document.activeElement;
				const isOnButton = active instanceof HTMLElement && active.id === "uno-enable-accessibility";

				if (evt.type === "keydown") {
					// No element focused yet — let browser Tab to the prepended button
					const isUnfocused = !active || active === document.body || active === document.documentElement;
					if (isUnfocused) {
						return;
					}

					// Button focused + Shift+Tab — let browser move focus back naturally
					if (isOnButton && evt.shiftKey) {
						return;
					}

					// Button focused + Tab (no shift) — fall through to managed FocusManager
				}

				// Don't route keyup to managed code while on the button
				if (evt.type === "keyup" && isOnButton) {
					return;
				}
			}

			let result = BrowserKeyboardInputSource._exports.OnNativeKeyboardEvent(
				BrowserKeyboardInputSource._source,
				evt.type == "keydown",
				evt.ctrlKey,
				evt.shiftKey,
				evt.altKey,
				evt.metaKey,
				evt.code,
				evt.key
			);

			if (result == HtmlEventDispatchResult.PreventDefault) {
				evt.preventDefault();
			}
		}
	}
}
