# BeeX DeskNest

A Windows desktop productivity suite built with WPF (.NET 10): pinnable desktop
widgets, a powerful screen capture / recording toolkit, a system cleaner, a
Markdown note editor, and an optional on-device OCR engine.

[![License: Apache-2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2B-lightgrey)
![.NET](https://img.shields.io/badge/.NET-10.0--windows-512BD4)

> UI available in Traditional Chinese, Simplified Chinese and English.

## Features

- **Desktop widgets** — 15 pinnable nest types: sticky notes, to-dos, clock,
  weather, music (with synced lyrics), system monitor, tags, countdown,
  deadlines, quick launcher, work-hours reminder, folder/file drops and more.
- **Screen capture** — region capture with a full annotation toolbox
  (rectangle, arrow, text, pen, highlighter, mosaic, step numbers, color
  picker), pin-to-screen, and in-place screen translation.
- **Screen recording** — region recording (GDI capture piped to FFmpeg),
  scrolling long-capture, quick trim and a built-in video editor; export to
  MP4 or GIF.
- **System cleaner (BeeXCleaner)** — uninstaller, residual / orphan scanning,
  registry backup and a free-space wiper. Elevation-aware.
- **Markdown notes (BeexWrite)** — a WebView2-hosted editor; every note is a
  real `.md` file with auto-save.
- **OCR** — text, formula (LaTeX) and table recognition plus translation,
  provided by an optional, separately installed engine (see *Architecture*).
- **Music lyrics** — aggregated from multiple public providers (Kugou, NetEase,
  QQ Music, LrcLib) and local player lyric caches.
- Global hotkeys, tray menu, floating ball, Acrylic / Dark / Honey themes.

## Architecture

BeeX DeskNest is composed of independent, loosely-coupled parts:

| Component | Project | Notes |
| --- | --- | --- |
| Main app | `src/BeeX.DeskNest.csproj` | WPF + WinForms, `net10.0-windows` |
| OCR sidecar | `src/OCR/BeeX_OCR.csproj` | Separate `net8.0-windows` executable |
| Tests | `test/` | xUnit |

**OCR runs as an isolated sidecar process.** The OCR engine is built as a
standalone executable (`BeeX_OCR.exe` / `BeeX_Formula.exe`) that is downloaded
on demand at runtime and driven purely over an inter-process line protocol
(stdin/stdout) by `OcrSidecarService`. The main application does **not**
reference the OCR project or any inference package — the OCR source tree is
explicitly excluded from the main build (`<Compile Remove="OCR/**" />`). This
keeps the shipped executable small and keeps the OCR engine's dependencies at
arm's length from the main app.

## Requirements

- Windows 10 version 2004 (build 19041) or later
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (to build)
- Optional runtime components are fetched on demand: FFmpeg (recording/clips)
  and the OCR engine — nothing is bundled into the repository.

## Build & run

```powershell
# from the repository root
dotnet build src/BeeX.DeskNest.csproj -c Release

# run the tests
dotnet test test/BeeX.DeskNest.Tests.csproj

# produce a single-file, self-contained portable build
dotnet publish src/BeeX.DeskNest.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -o publish/win-x64
```

## Data & configuration

All user data is stored **outside** the repository under a single BeeX root
(default `D:\BeeX`, falling back to `C:\BeeX`), with a pointer kept at
`%LocalAppData%\BeeX\root.txt`. Sub-folders cover state/config, screenshots,
recordings, clipboard images, notes, and downloaded components. See
`src/Core/BeeXPaths.cs`.

## License

Licensed under the **Apache License 2.0** — see [LICENSE](LICENSE).
Third-party attributions are listed in [NOTICE](NOTICE); usage notes,
third-party content and privacy details are in [DISCLAIMER.md](DISCLAIMER.md).

The OCR sidecar (Apache-2.0: PaddleSharp / OpenCvSharp / PaddleOCR) is a
separate process and is not linked into the main application, so it imposes no
licensing constraint on the main app.

## Contributing & security

- Contribution guidelines: [CONTRIBUTING.md](CONTRIBUTING.md)
- Community standards: [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)
- Reporting vulnerabilities: [SECURITY.md](SECURITY.md)
