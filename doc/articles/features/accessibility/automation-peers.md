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
Queued requests are discarded if the owning accessibility host is disposed;
new requests against a disposed host report that the element is unavailable.
Regression coverage includes `Given_UiaInvokeProviderWrapper` and the
Skia Win32 `Given_Window.When_Closed_Callback_Clears_Content_Native_Window_Is_Destroyed`
test, which checks actual HWND removal rather than only logical window visibility.

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
