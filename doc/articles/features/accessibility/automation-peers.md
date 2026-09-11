---
uid: Uno.Features.Accessibility.AutomationPeers
---

# Automation peers

> [!TIP]
> This article covers Uno-specific details about how automation peers work on Skia targets. For a full description of the peer model and how to create custom peers, see [Custom automation peers (Microsoft Learn)](https://learn.microsoft.com/windows/apps/design/accessibility/custom-automation-peers).

Every XAML control exposes its accessibility information through an **automation peer**. Uno implements the same `AutomationPeer` model as WinUI — when you use standard controls, the correct peer is created automatically.

## Built-in peers and ARIA role mapping

On WASM, the ARIA role is derived from the `AutomationControlType` reported by the peer. The full mapping is defined in the `AriaMapper` class.

| Control | Automation Peer | ARIA Role (WASM) |
|---------|----------------|------------------|
| `Button` | `ButtonAutomationPeer` | `button` |
| `CheckBox` | `CheckBoxAutomationPeer` | `checkbox` |
| `RadioButton` | `RadioButtonAutomationPeer` | `radio` |
| `Slider` | `SliderAutomationPeer` | `slider` |
| `TextBox` | `TextBoxAutomationPeer` | `textbox` — rendered as `<input type="text">` by default, or `<textarea>` when `AcceptsReturn` is `true` |
| `PasswordBox` | `PasswordBoxAutomationPeer` | `textbox` — rendered as `<input type="password">` |
| `ComboBox` | `ComboBoxAutomationPeer` | `combobox` |
| `ToggleSwitch` | `ToggleSwitchAutomationPeer` | `switch` (with `aria-checked`) |
| `ToggleButton` | `ToggleButtonAutomationPeer` | `button` (with `aria-pressed`) |
| `ListView` | `ListViewAutomationPeer` | `listbox` |
| `ListViewItem` | `ListViewItemAutomationPeer` | `option` |
| `HyperlinkButton` | `HyperlinkButtonAutomationPeer` | `link` — rendered as `<a>` element |
| `Image` | `ImageAutomationPeer` | `img` |
| `ProgressBar` | `ProgressBarAutomationPeer` | `progressbar` |
| `TextBlock` | `TextBlockAutomationPeer` | (none — text role) |

## Supported automation patterns

Automation patterns define the interaction capabilities of a control. All patterns below are routed through the Skia accessibility layer to each platform's native API.

| Pattern | Interface | Used For |
|---------|-----------|----------|
| Invoke | `IInvokeProvider` | Single-action controls (buttons, links) |
| Toggle | `IToggleProvider` | Two-state controls (checkboxes, toggle buttons) |
| RangeValue | `IRangeValueProvider` | Controls with a numeric range (sliders) |
| Value | `IValueProvider` | Controls with a text value (text boxes) |
| ExpandCollapse | `IExpandCollapseProvider` | Expandable controls (combo boxes, tree items) |
| Selection | `ISelectionProvider` | Containers that manage selected items (lists) |
| SelectionItem | `ISelectionItemProvider` | Individual selectable items |
| Scroll | `IScrollProvider` | Scrollable content areas |
| ScrollItem | `IScrollItemProvider` | Items that can be scrolled into view |
| Grid | `IGridProvider` | Grid layouts |
| GridItem | `IGridItemProvider` | Items within a grid |
| Table | `ITableProvider` | Table structures |

## Skia accessibility architecture

On all Skia targets (Win32, macOS, WASM, Android), the accessibility tree is built from automation peers. A shared base layer (`SkiaAccessibilityBase`):

- Queries each peer for its name, role, states, and patterns
- Routes property changes and focus events to the platform-specific implementation

Each platform applies its own pruning strategy. For example, WASM prunes structural elements (like `Grid`, `Border`, `ContentPresenter`) that have no accessible information, to keep the semantic DOM compact. macOS adds all elements but uses `NSAccessibilityUnknownRole` for those without meaningful roles so VoiceOver skips them.

### Platform-specific behavior

- **Win32** — Peers are exposed as UIAutomation provider nodes. Narrator and other UIAutomation clients query the tree directly.
- **macOS** — Each peer becomes an `NSAccessibilityElement` that VoiceOver can discover.
- **WASM** — Each peer produces a hidden DOM element with appropriate ARIA attributes (`role`, `aria-label`, `aria-checked`, etc.).

On Win32, UIA `Invoke` requests are queued to the element's dispatcher rather
than running control callbacks on the COM caller thread. The request returns
before the action executes, including actions that close their own window.
Each native provider has a revocable lifetime. Removing its owner subtree,
replacing its canonical peer, or disposing the accessibility host invalidates
that generation, including peer-only providers sharing an owner. Reattachment
creates a new generation; it cannot revive old pattern objects or queued
callbacks. Disabled elements reject mutations with `UIA_E_ELEMENTNOTENABLED`;
disconnected generations report `UIA_E_ELEMENTNOTAVAILABLE`.

Logical children have a declared-child generation as well as a visual owner.
`InvalidatePeer` and `StructureChanged` retire exposed peer-only descendants of
the invalidated declaration, including descendants reached through ancestors
that have no native provider. This is intentionally conservative: retained
logical peers also need a new provider generation after their declaration is
invalidated. A fresh lookup validates every declared edge through the root before
republishing a retired peer; an ancestor without a native provider is not a
validation boundary. The parent/source identities are checked again after
declaration queries. Canonical and virtual descendants both receive a fresh
generation: a disconnected canonical cache entry is not reusable. Old pattern
objects never revive. Unrelated visual owners remain available.
Invalidation follows a weak index of already exposed providers, without calling
data-backed `GetChildren` methods. Ancestry is captured when a provider is created
and rechecked before pattern operations. Cleanup collects its affected providers,
revokes all of them, then disconnects each native provider once.

Win32 `Toggle`, `Value`, `RangeValue`, `SelectionItem`, and `Scroll` operations
and property reads run on the owner's dispatcher. Unlike `Invoke`, these
operations remain synchronous so results and peer validation failures reach
the UIA caller. Properties remain readable when the control is disabled.
Work that has not started within five seconds times out and cannot access the
peer when the dispatcher later resumes. Once a synchronous operation starts,
the caller waits for its result; the timeout does not abandon an in-progress
mutation. Invoke's side-effect-free preflight retains its five-second timeout,
and the queued action rechecks both lifetime and enabled state.
`SelectorItemAutomationPeer.IsSelected` reads the selector's actual selection
regardless of enabled state; `Select`, `AddToSelection`, and `RemoveFromSelection`
remain enabled-only actions.

Regression coverage includes `Given_UiaInvokeProviderWrapper`,
`Given_Win32AccessibilityPatterns` (production provider wiring, worker calls,
validation errors, and detached/recycled generations), and the
Skia Win32 `Given_Window.When_Closed_Callback_Clears_Content_Native_Window_Is_Destroyed`
test, which checks actual HWND removal rather than only logical window visibility.
The production-wiring tests run in `src\Uno.UI.UnitTests\Uno.UI.UnitTests.Win32.csproj`.
This Windows-only runner has separate intermediate/output directories, a build
host check, and an execution-time Windows guard. It calls real
`UIAutomationCore.UiaDisconnectProvider`; its observer records ordering/counts
without replacing native cleanup. There are no platform skips that turn an
unsupported host into a pass. The platform-neutral `Uno.UI.UnitTests.csproj`
does not reference the Win32 runtime and keeps the linked, managed Invoke/dispatcher
contract tests.

After restoring the selected graph, build the Windows runner in Release with
`UnoTargetFrameworkOverride=net10.0` and run its executable with the filter
`FullyQualifiedName~Given_Win32AccessibilityPatterns|FullyQualifiedName~Given_UiaInvokeProviderWrapper`.
The executable is under `src\Uno.UI.UnitTests\bin\Win32\AnyCPU\Release\net10.0`.
The regressions include a realized ListView item, logical-only replacement,
100,000 declared children without eager enumeration, exact native-disconnect
ordering/counts, started synchronous operations crossing the response deadline,
and callbacks that remove content or close the simulated unit-test window.
Those windows are not real HWNDs: native cleanup calls and managed lifecycle
assertions do not replace COM-client, real-window teardown, Narrator, or
physical-input acceptance against the built runtime.

### Peer-declared child exclusions on Skia WASM

An element-backed `FrameworkElementAutomationPeer` can omit ordinary visual
child-owner roots from `GetChildrenCore`. The browser bridge excludes those
branches from its semantic DOM without unloading or hiding the visual elements.
Use the elements' cached peers, and call `InvalidatePeer` or raise
`AutomationEvents.StructureChanged` after updating the child selection and
visual collection consistently. Repeated notifications are coalesced to read
the final child selection.
Inserted and re-included branches retain their final visual sibling order in
the semantic DOM, including branches whose structural parents have no DOM node.
This includes the realized visual children of Raw list and repeater containers;
the control type alone does not establish a semantic boundary.

This is an exclusion bridge, not a replacement for the visual-tree-based
browser accessibility implementation. Ownerless peers, parent-owned proxies,
and ambiguous owner mappings do not identify an ordinary branch to exclude.
`ListViewBase` and `ItemsRepeater` retain their realized-item lifecycle; the
bridge does not enumerate their data-backed peer children. Setting a container
to `AccessibilityView.Raw` alone does not exclude its descendants.

For browser regressions, run `Given_PeerDeclaredChildren` alongside
`Given_AccessibleAria`, `Given_AccessibleComboBox`, `Given_AccessibleListView`,
and `Given_AccessibleScrollViewer`. Also run
`When_Initial_Tree_Excludes_Retained_Branch` alone in a fresh browser context so
the accessibility-enable-after-attach case starts cold. The excluded list and
repeater tests retain the 100,000-item / at-most-100-realized bound, including
exclusion, re-inclusion, and source replacement.

## See also

- [Accessibility overview](xref:Uno.Features.Accessibility)
- [AutomationProperties reference](xref:Uno.Features.Accessibility.AutomationProperties)
- [Role override](xref:Uno.Features.Accessibility.RoleOverride)
- [Testing with screen readers](xref:Uno.Features.Accessibility.TestingWithScreenReaders)
- [Custom automation peers (Microsoft Learn)](https://learn.microsoft.com/windows/apps/design/accessibility/custom-automation-peers)
