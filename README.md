# MarkDNext

MarkDNext is a native Windows Markdown editor and viewer. It was vibe-coded with help from Codex, and is inspired by MDV, MarkText, and ghostwriter.

## Features

- WPF desktop app for Windows 10+ x64.
- No Electron runtime.
- Open Markdown files from the UI, drag and drop, or command line.
- Edit Markdown as source or in WYSIWYG block mode.
- In WYSIWYG mode, the editor itself is the preview: the right preview pane is hidden, focused blocks expose raw Markdown, and blurred blocks render automatically.
- WYSIWYG mode keeps a browser-side block state in one persistent WebView document, while syncing the same Markdown text used by source mode.
- Code blocks in WYSIWYG mode use a language field plus code body editor, with Markdown fences preserved in the saved source.
- WYSIWYG mode includes block commands: type `/` in a focused block to insert headings, lists, code blocks, math blocks, and tables.
- Live WebView2 preview with off-screen KaTeX/code rendering before content is swapped into view.
- GitHub-style Markdown rendering through Markdig advanced extensions.
- Source editor highlighting and completion through AvalonEdit.
- Automatic completion is optional from `Edit -> Automatic Completion`; `Ctrl+H` toggles it in source mode.
- Offline code highlighting through bundled highlight.js assets.
- KaTeX rendering for inline `$\alpha$` and display `$$\alpha$$` formulas.
- Auto reloads the file when it is changed on disk and the editor has no unsaved changes.
- Find in editor or preview.
- Print from the File menu, or use `File -> Export` to export HTML or PDF. HTML export copies local images beside the document under an `assets` folder.
- Relative images and links resolve from the Markdown file folder.
- Theme menu with Mica and Acrylic window backdrop options when supported by Windows, with a flat fallback.

## Build

Install the .NET 8 SDK, then build from the repository root:

```powershell
dotnet build -c Release
```

## Publish

Release publishing is configured in `MarkDNext.csproj` as a self-contained, compressed single-file Windows x64 build.

```powershell
dotnet publish -c Release
```

The standalone executable is created at:

```text
dist\MarkDNext.exe
```

License and third-party notice files are copied to `dist` alongside the executable.

Run it from Explorer or from a terminal:

```powershell
.\dist\MarkDNext.exe .\sample.md
```

The preview requires Microsoft Edge WebView2 Runtime, which is already present on most current Windows 10/11 systems.

## License

MarkDNext is licensed under the Apache License, Version 2.0. See `LICENSE` and `NOTICE`.

This repository also includes third-party dependencies and bundled offline rendering assets. See `THIRD_PARTY_NOTICES.md` before redistributing source or binary builds.
