# Third-party notices

A published build of Perianth is a single self-contained file, which means the
components below are **redistributed inside it**. Their licences travel with
it, and this file is how.

The command-line build carries none of these — it uses only the .NET runtime.
Everything here comes with the window.

Every package in the dependency graph declares **MIT**.

## Avalonia

<https://github.com/AvaloniaUI/Avalonia> — MIT

The user-interface toolkit. Chosen because it is the only one giving a real
Windows and Linux desktop from one codebase. Includes the packages
`Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Skia`,
`Avalonia.Win32`, `Avalonia.X11`, `Avalonia.FreeDesktop`, `Avalonia.HarfBuzz`,
`Avalonia.Native`, `Avalonia.Angle.Windows.Natives`,
`Avalonia.Remote.Protocol` and `MicroCom.Runtime`.

## SkiaSharp

<https://github.com/mono/SkiaSharp> — MIT
Copyright (c) 2015-2016 Xamarin, Inc.
Copyright (c) 2017-2018 Microsoft Corporation.

Avalonia's rendering backend, with its native libraries for each platform.
SkiaSharp wraps **Skia** (<https://skia.org>), Google's graphics library, which
carries its own BSD-3-Clause licence and is embedded in the native binary.

## HarfBuzzSharp

<https://github.com/mono/SkiaSharp> — MIT
Copyright (c) 2015-2016 Xamarin, Inc.
Copyright (c) 2017-2018 Microsoft Corporation.

Text shaping, with its native libraries. Wraps **HarfBuzz**
(<https://harfbuzz.github.io>), which carries its own permissive "Old MIT"
licence and is embedded in the native binary.

## Inter

<https://rsms.me/inter/> — SIL Open Font License 1.1

The window's typeface, bundled rather than taken from the system so that the
interface looks the same on both platforms. The OFL permits redistribution
inside an application. The `Avalonia.Fonts.Inter` package that carries it is
MIT.

## Tmds.DBus.Protocol

<https://github.com/tmds/Tmds.DBus> — MIT

Linux desktop integration, reached through Avalonia. Present in a Windows build
but unused there.

## .NET

<https://github.com/dotnet/runtime> — MIT

A self-contained build includes the .NET runtime itself.

---

## What is deliberately not bundled

**`vgmstream-cli`** is an external program, found on `PATH` or named
explicitly. It is never shipped with this tool and none of its code is
included. Voice-audio decoding is the only feature that needs it, and
everything else works without it.

**No game content.** No asset, texture, model, animation or line of dialogue
from *South Park: The Fractured But Whole* is included in this tool's source or
in any build of it. The tool reads files you already have.
