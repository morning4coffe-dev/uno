---
uid: Uno.Features.Accessibility.TestingWithScreenReaders
---

# Testing with screen readers

> [!TIP]
> For general screen reader setup, navigation shortcuts, and testing methodology, see [Accessibility testing (Microsoft Learn)](https://learn.microsoft.com/windows/apps/design/accessibility/accessibility-testing).

This guide covers Uno-specific steps for verifying accessibility in your application.

## Enabling the accessibility layer (WASM)

On WASM Skia targets, the accessibility layer activates when the user first presses the `Tab` key. A visually hidden **"Enable accessibility"** element becomes reachable via Tab or screen reader — activate it (click, `Enter`, or `Space`) before the full semantic tree becomes available.

To activate manually from browser DevTools:

```js
document.getElementById('uno-enable-accessibility').click();
```

For automated testing scenarios where you need the accessibility layer ready before any user interaction, you can force it on at startup:

```csharp
FeatureConfiguration.AutomationPeer.AutoEnableAccessibility = true;
```

> [!WARNING]
> `AutoEnableAccessibility` materializes and continuously maintains the full semantic DOM for the lifetime of the app, which has a significant runtime cost (every visual-tree change updates the semantic overlay). It is intended for testing and debugging — leave it disabled in production so the cost is only paid when an assistive technology actually requests accessibility. Set it before the host is built (typically in `App.xaml.cs` before `MainWindow` is created); it is read once during accessibility subsystem initialization.
> [!NOTE]
> On Windows (Win32) and macOS, the accessibility tree is always active — no manual activation is required.

## Browser and screen reader pairing (WASM)

When testing WASM apps with a screen reader, the browser choice affects results:

| Browser | Screen Reader | Notes |
|---------|---------------|-------|
| Chrome | NVDA (Windows) | Best overall support for ARIA in Chromium-based browsers. |
| Firefox | NVDA (Windows) | Good alternative; Firefox has its own accessibility engine. |
| Safari | VoiceOver (macOS) | Best VoiceOver experience. |
| Chrome | VoiceOver (macOS) | Works, but Safari is recommended. Enable Full Keyboard Access in System Settings → Keyboard. |

## Using the SamplesApp

The `AccessibilityScreenReaderPage` sample in the Uno SamplesApp includes test sections for common control types:

1. Build and run the SamplesApp (`SamplesApp.Skia.Generic`)
2. Navigate to the `Accessibility_ScreenReader` sample
3. Enable your screen reader and Tab into the app
4. On WASM, activate the "Enable accessibility" button first

## Debugging the accessibility tree

### WASM — inspecting the semantic DOM

On WASM, look for the `#uno-semantics-root` container in the DOM. It contains hidden semantic overlay elements (buttons, inputs, headings, etc.) that the screen reader interacts with. Each element has `aria-label` and the appropriate `role` attribute.

Inspect using browser DevTools:

- **Chrome:** DevTools → Elements → Accessibility pane (right sidebar)
- **Firefox:** DevTools → Accessibility tab
- **Safari:** Develop → Show Web Inspector → Elements → Node → Accessibility

### Common issues

| Problem | Possible cause | Fix |
|---------|---------------|-----|
| Nothing is announced (WASM) | Accessibility layer not activated | Press `Tab`, then activate the "Enable accessibility" button |
| Wrong label announced | `AutomationProperties.Name` not set or wrong `LabeledBy` target | Check `aria-label` in the semantic DOM |
| Headings not in Rotor | Missing `HeadingLevel` property | Verify `AutomationProperties.HeadingLevel` is set; on WASM check for `<h1>`–`<h6>` elements |
| Landmarks not listed | Missing `LandmarkType` property | Verify `AutomationProperties.LandmarkType` is set; on WASM check for `role="navigation"` etc. |
| Live region not announcing | `LiveSetting` not set or content not changing | On WASM, verify `aria-live` attribute exists on the semantic element |
| VoiceOver silent in Chrome | Known Chrome limitation | Test in Safari for best VoiceOver support; in Chrome, enable Full Keyboard Access in System Settings → Keyboard |

## Modal lifetime regression checks (WASM Skia)

Only the topmost modal focus scope owns the background semantic mask. Opening
a child suspends the parent's mask, including when the two popup elements are
DOM siblings. Closing the child reapplies the surviving parent's mask against
the current DOM. Closing a suspended parent must not expose the background
or leave the surviving child's eventual focus-return target inside the closed
scope. After the final close, original `aria-hidden` and `tabindex` values
must be restored.

The managed active scope and live-region modal handle must also clear after
the final close. Closing a suspended parent must not leave it in the surviving
child's managed parent chain. Raise a background live-region event afterward
and check the live-region DOM content, then repeat an open/close cycle.
While a child is open, replace or disable focusable controls in the parent;
resuming the parent must use its updated children without exposing the
background early.

Exercise both close orders, Tab and Shift+Tab wrapping, and close/reopen.
Inspect the exposed accessibility tree as well as keyboard focus; pixels alone
cannot establish modal exclusion. Also test the application's actual modal
implementation: a custom overlay or MAUI modal root is not automatically a
registered Uno `ContentDialog` focus scope.

The browser runtime's `tests/semantic-input.cjs` contains focused DOM/runtime
regressions for this contract. Those tests use mocked managed exports and do
not replace a coherently built application's screen-reader/input acceptance.
For release evidence, record the package/source identity and use a fresh
browser context with normal accessibility activation. Do not count DevTools
activation, injected callbacks, or a headless accessibility snapshot as an
executed Narrator, NVDA, TalkBack, or VoiceOver workflow.

## Derived-name regression checks (WASM Skia)

Text can be represented by an ancestor's accessible name rather than a
standalone paragraph. Check the ancestor's current `aria-label` after changing
the descendant's text, clearing it, moving it to another parent, and
detaching or hiding its containing subtree. The old parent must not retain a
removed child's name. Explicit names and independently actionable descendants
must retain their own naming and semantic behavior.

The browser runtime queues name reevaluation for ordinary semantic ancestors
as well as realized items, reading the final state after coalesced mutations.
Only candidate ancestor discovery has the 16-ancestor bound. Attachment,
exclusion and subtree refresh still have tree-dependent cost; the entire
operation is not claimed to be constant-time or allocation-free.
The same naming precedence used at creation still applies; applications
should not need to invalidate the ancestor peer or force a resize to refresh
an absorbed name.

Inspect the browser's full accessibility tree as well as the DOM: a role-less
container can have a nonignored generic accessibility node with a stale name
even when a simplified ARIA snapshot omits it. A correct visible label or live
paragraph alone is not evidence that this ancestor path is current.

`Given_SemanticNameRefreshQueue` provides focused managed state-unit coverage;
`Given_DerivedAncestorNames` exercises actual event-to-DOM behavior on Skia
WebAssembly. See the browser runtime's `tests/README.md` for commands and
coverage boundaries. Screen-reader and physical-input acceptance still require
a coherently built application.

The `Uno.UI.RuntimeTests.BrowserInput` source consumer provides a separate
keyboard check: activate accessibility with Tab/Enter, then activate **Handle
event** with Enter. Its normal click handler changes the visible status carried
by a role-less ancestor. Check that exact ancestor's DOM label and nonignored
generic browser accessibility node, not a substitute paragraph. This is an
Uno source consumer, not a MAUI package-cohort or physical screen-reader test.

## See also

- [Accessibility overview](xref:Uno.Features.Accessibility)
- [AutomationProperties reference](xref:Uno.Features.Accessibility.AutomationProperties)
- [Custom automation peers](xref:Uno.Features.Accessibility.AutomationPeers)
- [Role override](xref:Uno.Features.Accessibility.RoleOverride)
- [Accessibility testing (Microsoft Learn)](https://learn.microsoft.com/windows/apps/design/accessibility/accessibility-testing)
