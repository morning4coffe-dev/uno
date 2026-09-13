namespace Uno.UI.Runtime.Skia {

	interface FocusTrapState {
		modalHandle: number;
		triggerHandle: number;
		focusableHandles: number[];
		hiddenElements: { element: HTMLElement; originalAriaHidden: string | null; originalTabIndex: string | null }[];
		keydownHandler: (e: KeyboardEvent) => void;
		parentState: FocusTrapState | null;
	}

	/**
	 * Modal focus trap for ContentDialog and modal overlays.
	 * Manages aria-hidden on background, Tab/Shift+Tab wrapping,
	 * nested modal support, and focus restoration on close.
	 */
	export class FocusTrap {
		private static activeTrap: FocusTrapState | null = null;

		/**
		 * Activates a focus trap for a modal dialog.
		 * Hides background elements and starts Tab wrapping.
		 */
		public static activateFocusTrap(modalHandle: number, triggerHandle: number, focusableHandles: number[]): void {
			const parentState = FocusTrap.activeTrap;

			// Only the top scope owns a mask. Stacking snapshots would hide sibling
			// dialogs and restore stale attributes when a suspended scope closes first.
			if (parentState) {
				FocusTrap.restoreHiddenElements(parentState);
			}
			const modalElement = document.getElementById(`uno-semantics-${modalHandle}`);

			// Set role="dialog" on modal element
			if (modalElement) {
				modalElement.setAttribute("role", "dialog");
				modalElement.setAttribute("aria-modal", "true");
			}

			// Create keydown handler for Tab wrapping.
			// Use capture phase so user code cannot stopPropagation() to escape the trap.
			const keydownHandler = (e: KeyboardEvent) => {
				if (e.key === "Tab") {
					const wrapped = FocusTrap.handleTrapTab(modalHandle, e.shiftKey);
					if (wrapped) {
						e.preventDefault();
						e.stopImmediatePropagation();
					}
				}
			};

			document.addEventListener("keydown", keydownHandler, true);

			FocusTrap.activeTrap = {
				modalHandle,
				triggerHandle,
				focusableHandles,
				hiddenElements: [],
				keydownHandler,
				parentState
			};
			FocusTrap.hideBackgroundElements(FocusTrap.activeTrap);

			// Focus the first focusable element in the modal
			if (focusableHandles.length > 0) {
				const firstElement = document.getElementById(`uno-semantics-${focusableHandles[0]}`);
				if (firstElement) {
					firstElement.focus();
				}
			}
		}

		/**
		 * Deactivates the focus trap for a modal dialog.
		 * Restores background elements and focus.
		 */
		public static deactivateFocusTrap(modalHandle: number): void {
			const trap = FocusTrap.activeTrap;
			if (!trap) {
				return;
			}

			// Handle out-of-order deactivation: if the requested modal is not
			// the topmost, walk the parent chain to find and remove it.
			if (trap.modalHandle !== modalHandle) {
				let current: FocusTrapState | null = trap;
				while (current) {
					if (current.parentState?.modalHandle === modalHandle) {
						const target = current.parentState;
						document.removeEventListener("keydown", target.keydownHandler, true);
						FocusTrap.restoreHiddenElements(target);
						FocusTrap.removeDialogRole(target.modalHandle);
						// The surviving child must return past the closed scope.
						current.triggerHandle = target.triggerHandle;
						current.parentState = target.parentState;
						return;
					}
					current = current.parentState;
				}
				return;
			}

			// Remove keydown handler (must match capture phase used in activate)
			document.removeEventListener("keydown", trap.keydownHandler, true);

			// Restore hidden elements
			FocusTrap.restoreHiddenElements(trap);

			// Remove dialog role
			FocusTrap.removeDialogRole(modalHandle);

			// Reactivate parent trap or clear
			FocusTrap.activeTrap = trap.parentState;
			if (FocusTrap.activeTrap) {
				FocusTrap.hideBackgroundElements(FocusTrap.activeTrap);
			}

			// A suspended parent's trigger may have been removed or disabled.
			const parent = FocusTrap.activeTrap;
			if (!parent || parent.focusableHandles.indexOf(trap.triggerHandle) >= 0) {
				const focused = FocusTrap.tryFocusHandle(trap.triggerHandle);
				if (focused || FocusTrap.activeTrap !== parent) {
					return;
				}
			}
			if (parent) {
				for (const handle of parent.focusableHandles) {
					const focused = FocusTrap.tryFocusHandle(handle);
					if (focused || FocusTrap.activeTrap !== parent) {
						return;
					}
				}
			}
		}

		/**
		 * Updates the focusable children within a modal.
		 */
		public static updateFocusTrapChildren(modalHandle: number, focusableHandles: number[]): void {
			for (let trap = FocusTrap.activeTrap; trap; trap = trap.parentState) {
				if (trap.modalHandle === modalHandle) {
					trap.focusableHandles = focusableHandles;
					return;
				}
			}
		}

		private static tryFocusHandle(handle: number): boolean {
			const element = document.getElementById(`uno-semantics-${handle}`);
			if (!element || element.matches(":disabled") || element.getAttribute("aria-disabled") === "true" ||
				element.hidden || element.getAttribute("aria-hidden") === "true") {
				return false;
			}
			element.focus();
			return document.activeElement === element;
		}

		/**
		 * Handles Tab/Shift+Tab within a focus trap.
		 * Returns true if focus was wrapped.
		 */
		public static handleTrapTab(modalHandle: number, shiftKey: boolean): boolean {
			const trap = FocusTrap.activeTrap;
			if (!trap || trap.modalHandle !== modalHandle || trap.focusableHandles.length === 0) {
				return false;
			}

			const activeElement = document.activeElement;
			const handles = trap.focusableHandles;

			// Find current position in focusable list
			let currentIndex = -1;
			for (let i = 0; i < handles.length; i++) {
				if (activeElement?.id === `uno-semantics-${handles[i]}`) {
					currentIndex = i;
					break;
				}
			}

			if (shiftKey) {
				// Shift+Tab: wrap from first to last
				if (currentIndex <= 0) {
					const lastElement = document.getElementById(`uno-semantics-${handles[handles.length - 1]}`);
					if (lastElement) {
						lastElement.focus();
						return true;
					}
				}
			} else {
				// Tab: wrap from last to first
				if (currentIndex >= handles.length - 1) {
					const firstElement = document.getElementById(`uno-semantics-${handles[0]}`);
					if (firstElement) {
						firstElement.focus();
						return true;
					}
				}
			}

			return false;
		}

		/**
		 * Returns whether a focus trap is currently active.
		 */
		public static isFocusTrapActive(): boolean {
			return FocusTrap.activeTrap !== null;
		}

		/**
		 * Returns the handle of the active modal, or 0 if no trap is active.
		 */
		public static getActiveTrapHandle(): number {
			return FocusTrap.activeTrap?.modalHandle ?? 0;
		}

		private static hideBackgroundElements(trap: FocusTrapState): void {
			const semanticsRoot = document.getElementById("uno-semantics-root");
			const modalElement = document.getElementById(`uno-semantics-${trap.modalHandle}`);
			if (semanticsRoot && modalElement) {
				const allElements = semanticsRoot.querySelectorAll("[id^='uno-semantics-']");
				allElements.forEach((el: HTMLElement) => {
					if (el !== modalElement && !modalElement.contains(el) && !el.contains(modalElement)) {
						trap.hiddenElements.push({
							element: el,
							originalAriaHidden: el.getAttribute("aria-hidden"),
							originalTabIndex: el.getAttribute("tabindex")
						});
						el.setAttribute("aria-hidden", "true");
						el.setAttribute("tabindex", "-1");
					}
				});
			}
		}

		private static restoreHiddenElements(trap: FocusTrapState): void {
			for (const item of trap.hiddenElements) {
				if (item.originalAriaHidden !== null) {
					item.element.setAttribute("aria-hidden", item.originalAriaHidden);
				} else {
					item.element.removeAttribute("aria-hidden");
				}
				if (item.originalTabIndex !== null) {
					item.element.setAttribute("tabindex", item.originalTabIndex);
				} else {
					item.element.removeAttribute("tabindex");
				}
			}
			trap.hiddenElements.length = 0;
		}

		private static removeDialogRole(modalHandle: number): void {
			const modalElement = document.getElementById(`uno-semantics-${modalHandle}`);
			if (modalElement) {
				modalElement.removeAttribute("role");
				modalElement.removeAttribute("aria-modal");
			}
		}
	}
}
