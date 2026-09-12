---
uid: uno.features.uno.winrt
---

# Non-UI Cross-Platform API - Using Uno.WinRT

Uno.WinRT is the non-UI layer of Uno Platform.

It is composed of APIs that have been present since the beginning of Uno Platform to provide cross-platform access to non-UI features such as generic filesystem, sensors, file/image/video picking, networking, and devices like MIDI, flashlight, geolocation, Game Pads, and dozens more.

These APIs can be used in a NuGet package directly, without depending on UI features.

`Uno.WinRT` declares its non-UI package dependencies itself: `Uno.Foundation`
provides the foundation APIs and its `Uno.Foundation.Logging` dependency, while
`Uno.Diagnostics.Eventing` supports the bundled dispatching implementation.
Applications do not need to reference `Uno.WinUI` merely to supply these dependencies.

Platform-specific runtime and host integration still applies, including the
WebAssembly runtime selected by the corresponding host packages.

For a list of available APIs, browse the tree on the side of this document.
