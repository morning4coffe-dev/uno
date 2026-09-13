# Browser semantic regressions

Run the existing semantic/input suite against the **complete** browser script
produced by the owning runtime build:

```powershell
$env:PLAYWRIGHT_MODULE = '<existing playwright-core installation>'
node src\Uno.UI.Runtime.Skia.WebAssembly.Browser\tests\semantic-input.cjs '<built Uno.Runtime.Wasm.js>'
```

The suite requires an installed headless Edge. Every case uses a fresh page;
failures remain failures and the browser closes in `finally`. It checks
keyboard activation ownership, recycled names/geometry/positions, pending
realizations, detached generations, text updates, modal masking/focus, and
semantic reparenting. Managed exports are mocked here: this is JavaScript
runtime-unit coverage, **not** a full MAUI/Uno application or screen-reader
acceptance result.

Modal regressions include descendant and sibling popup DOM topology, both
normal and out-of-order close, restoration of preexisting hidden/tab-stop
attributes, and preservation of the surviving scope's focus-return path.
Do not remove the existing virtualized-lifetime or keyboard cases when
selecting a modal fix.

The managed scope chain, live-region modal handle and JS trap stack must agree
on the surviving top scope. Closing a suspended parent unlinks it from both
chains, including the surviving child's return target. Managed ownership is
updated before JS focus callbacks can close or open another dialog. Updates to
suspended scopes retain their current child lists without reactivating their
masks; focus restoration must reject removed/disabled targets.

Run the real
`Uno.UI.RuntimeTests.Tests.Windows_UI_Xaml_Controls.Given_ContentDialog.When_Nested_Accessibility_`
cases in addition to the JS suite. They exercise both close orders, popup
unloading/disposal, reentrant closure, another open/close cycle, and a real
background `LiveRegionChanged` event reaching the DOM after every modal closes.
The older ignored announcement tests are not coverage for this contract.

For TypeScript-only iteration use the compiler version declared in
`src/Directory.Build.targets` and the includes/options from this runtime's
`tsconfig.json`; `locale` is a command-line option for standalone `tsc`.
Keep `noImplicitAny` and declaration checks enabled. Record such compilation
separately from managed DLL/package builds. Do not replace a deployed script
or restored package to make an application's test pass.

After packaging the coherent runtime, exercise the application's real modal
open/close paths through keyboard/pointer input and its accessibility tree.
The C# peer-boundary and `Given_ItemsWrapGrid` runtime regressions remain
separate requirements, including the supported 100,000-item case with at most
100 realized containers. JavaScript fixture counts do not establish that
managed virtualization or physical assistive-technology coverage.

## Managed derived-ancestor names

A descendant's name can be absorbed into an ordinary semantic ancestor, not
only a realized list item. Updating a standalone paragraph does not exercise
this path. Name changes and child membership changes queue current semantic
ancestors for reevaluation using their normal name resolver. The queue
coalesces changes, checks attachment/exclusion and semantic membership again
at drain time, and drops removed owners. Only candidate ancestor discovery
shares the 16-ancestor absorption bound and stops at the realized-item/data
frontier. Attachment/exclusion checks, name resolution and subtree
reconciliation retain tree-dependent cost. Warmed queue allocation checks do
not establish that the whole accessibility refresh is constant-time or
allocation-free.
Each drain processes only its initial FIFO entries. Body-text reconciliation
can enqueue its ancestor while peer-child insertion is still pending; draining
that reentrant work immediately can starve the insertion indefinitely. Yield
that work to the next dispatcher turn, without introducing a timed delay.

Run the source-linked managed queue and adjacent name/lifetime unit tests:

```powershell
dotnet test --project src\Uno.UI.UnitTests\Uno.UI.UnitTests.csproj `
  --configuration Release --framework net10.0 --no-restore `
  -p:UnoTargetFrameworkOverride=net10.0 -- `
  --filter "FullyQualifiedName~Given_SemanticNameRefreshQueue|FullyQualifiedName~Given_TemplateAutomationText|FullyQualifiedName~Given_VirtualizedSemanticRegionState"
```

These use real controls/peers without loading a visual tree; semantic
membership and attachment are unit-test inputs. They prove queue selection,
coalescing, current-name resolution, cancellation and bounded candidate discovery, not
the browser's event-to-DOM integration.

For that integration, run
`Uno.UI.RuntimeTests.Tests.Windows_UI_Xaml_Automation.Given_DerivedAncestorNames`
on a freshly built Skia WebAssembly test app. Its five cases change real
`TextBlock.Text` and child membership without calling `InvalidatePeer`,
repairing the DOM or forcing layout. They check an absorbed name, clearing,
an independent button, reparenting/detachment, authored-name precedence,
body-text membership and hide/show. Keep `Given_PeerDeclaredChildren` and the
unchanged 100,000-item / at-most-100-realized regression gates separate.

If restore fails auditing or a compatible browser toolchain is missing,
report it as blocked. Do not suppress warnings, substitute previously
compiled managed modules, or count a unit pass as an executed runtime case.

Retain SamplesApp runner `ExitStatus`/dispatcher diagnostics after result
writing, separately from the XML test results. An exception-free standalone
consumer run applies only to that consumer/run, not to the runtime-test runner.

Separate publish directories alone do not make retained bundles immutable:
the repository enables hardlinks, including links to mutable generated loader
files. Before another build, preserve each evidence bundle as an ordinary
file copy and verify its complete hash manifest. A hash-matched recovery from
retained compressed bytes is artifact preservation, not a new runtime pass;
never repair a served bundle to manufacture a successful replay.
