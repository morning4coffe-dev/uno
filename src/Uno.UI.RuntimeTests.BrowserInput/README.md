# Derived-name keyboard consumer

This small **Uno source consumer** exercises a normal `Button.Click` changing
text absorbed into a peer-bearing, role-less panel. It is not a MAUI handler,
a package-consumer compatibility assertion, or a test-only managed success
callback. The complete Uno runtime comes from project references.

Publish with the normal Skia WebAssembly native features and a compatible
SDK/workload:

```powershell
dotnet publish src/Uno.UI.RuntimeTests.BrowserInput/Uno.UI.RuntimeTests.BrowserInput.csproj `
  -c Release -f net10.0 -p:UnoTargetFrameworkOverride=net10.0
```

Serve `bin/Release/net10.0/publish/wwwroot` over loopback HTTP. From a fresh
browser context:

1. Let the first frame render and the normal loader disappear. Do not resize
   or alter the DOM to make the frame appear.
2. Press Tab and Enter to activate accessibility. The visible status and
   `derived-name-panel` semantic node must still say **Consumed from owning
   source**: activation must not leak into the application button.
3. Tab to **Handle event** if it is not already focused, then press Enter.
4. The visible status and that panel's `aria-label` must say **Event handled
   through the owning source Uno runtime**. In Chromium's raw accessibility
   tree the exact panel must be a nonignored `generic` node with that name.
   Its text must remain absorbed, not replaced by a standalone paragraph.

Record actual keyboard input, raw AX/DOM, pixels, and source/output hashes
separately from compilation and from the runtime test runner's assertions.
Automation over a browser protocol is not physical keyboard/assistive
technology acceptance.

## Isolated build prerequisites

The source head registers and builds the real source tasks before runtime
selection. Its source-generator reference supplies canonical ICU/application
initialization; do not replace that with an injected private initializer.
Fonts and IDBFS match the normal source browser head.

If a feed mirror is necessary, pass `RestoreSources` to **publish/build as
well as restore**. Source-task bootstrap performs its own restore, even when
the outer command uses `--no-restore`. Keep NuGet auditing and warnings-as-errors.
See `Uno.HotReload.Tests/README.md` for the restored-graph security regression.

On Windows, the selected Bootstrap version shortens Emscripten paths through
`$(USERPROFILE)/.uno/emsdk/<version>`. When using a deliberately isolated SDK,
use an owned process-local profile, cache (`WasmCachePath`) and temp directory
on the same volume. Verify the junction's actual target; a preexisting
version-named junction can point to another SDK. Do not repair installed
packages, move the cache into a shared profile, or change AOT/optimization
settings to conceal a cross-volume failure.
