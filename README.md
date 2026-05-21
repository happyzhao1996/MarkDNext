# MarkDNext

MarkDNext is a native Windows Markdown editor and viewer inspired by the MDV app at https://www.mowglii.com/mdv/.

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
- Automatic completion is optional from `Edit -> Automatic Completion`; `Ctrl+Space` always opens manual snippets in source mode.
- Offline code highlighting through bundled highlight.js assets.
- KaTeX rendering for inline `$\\alpha$` and display `$$\\alpha$$` formulas.
- Auto reloads the file when it is changed on disk and the editor has no unsaved changes.
- Find in editor or preview.
- Print from the File menu, or use `File -> Export...` and choose `Microsoft Print to PDF` to create a PDF.
- Relative images and links resolve from the Markdown file folder.
- Theme menu with Mica and Acrylic window backdrop options when supported by Windows, with a flat fallback.

## Build

This repo includes a local .NET SDK under `.dotnet` because the machine only had .NET runtimes installed.

```powershell
.\.dotnet\dotnet.exe build -c Release
```

## Publish

```powershell
.\.dotnet\dotnet.exe publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:EnableCompressionInSingleFile=true
```

The published app is created under:

```text
bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\
```

Run it from Explorer or from a terminal:

```powershell
.\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\MarkDNext.exe .\sample.md
```

The preview requires Microsoft Edge WebView2 Runtime, which is already present on most current Windows 10/11 systems.
