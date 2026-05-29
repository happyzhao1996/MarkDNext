# MarkDNext

MarkDNext is a native Windows Markdown editor and viewer. It was vibe-coded with help from Codex, and is inspired by MDV, MarkText, and ghostwriter.

![MarkDNext screenshot](docs/screenshot.png)

## Features

### Platform And Files

- WPF desktop app for Windows 10+ x64.
- No Electron runtime; preview and WYSIWYG rendering use Microsoft Edge WebView2.
- Open Markdown from the UI, drag and drop, or command line, with auto-reload for unchanged files.

### Editing And Formatting

- Source editor, split view, preview-only, and WYSIWYG modes, switchable with `Ctrl+E`, `Ctrl+T`, `Ctrl+R`, and `Ctrl+W`; `Ctrl+Q` closes the app.
- WYSIWYG block editing renders on blur, exposes raw Markdown on focus, supports `/` block commands, and preserves fenced code blocks.
- WYSIWYG block reordering by dragging hover handles on blurred blocks, with a drop indicator for the target position.
- AvalonEdit source editing with Markdown highlighting, current-line highlight, configurable line spacing, optional completion (`Ctrl+H`), and formatting shortcuts (`Ctrl+B/I/U/K`, `$`, and backtick wrapping).

### Markdown Syntax And Rendering

- GitHub-style Markdown via Markdig advanced extensions, with relative images and links resolved from the Markdown file folder.
- Bundled offline KaTeX and highlight.js for math and code rendering.
- Find in editor or preview; split view follows the currently focused pane.

### Export And Appearance

- Print, export PDF, or export HTML; HTML export copies local images into an adjacent `assets` folder.
- Built-in and imported themes with complete light/dark JSON import/export, AppData persistence, and optional Mica/Acrylic backdrop.

## Repository Layout

- `src/` contains the WPF application source and XAML views.
- `resources/app/` contains the app icon and window logo.
- `resources/web/` contains embedded offline WebView assets such as KaTeX and highlight.js.
- `resources/themes/` contains bundled color themes.
- `examples/` contains sample Markdown and theme profile files.
- `docs/` contains repository images and documentation media.
- `scripts/` contains local build and packaging helpers.

## Build

Install the .NET 8 SDK, then build from the repository root:

```powershell
dotnet build -c Release
```

## Publish

Release publishing is configured in `MarkDNext.csproj` as a self-contained, compressed single-file Windows x64 build.

```powershell
.\scripts\package-release.ps1
```

The release helper publishes the app and leaves a single standalone executable at:

```text
dist\MarkDNext-<version>-win-x64.exe
```

Direct `dotnet publish -c Release` still works for local testing, while the release helper cleans `dist` down to the versioned executable used for GitHub releases.

Run it from Explorer or from a terminal:

```powershell
.\dist\MarkDNext-<version>-win-x64.exe .\examples\sample.md
```

The preview requires Microsoft Edge WebView2 Runtime, which is already present on most current Windows 10/11 systems.

## License

MarkDNext is licensed under the Apache License, Version 2.0. See `LICENSE` and `NOTICE`.

This repository also includes third-party dependencies and bundled offline rendering assets. See `THIRD_PARTY_NOTICES.md` before redistributing source or binary builds.
