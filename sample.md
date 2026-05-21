# MarkDNext Sample

This sample exercises the Markdown preview.

## Editing

- Type on the left and the preview updates automatically.
- Save this file from another editor and this window reloads it when there are no unsaved edits.
- Drag a Markdown file into the window to open it.

## GitHub-style Markdown

- [x] Task lists
- [ ] Tables
- [ ] Code fences

| Feature | Status |
| --- | --- |
| Live preview | Done |
| File watching | Done |
| WYSIWYG blocks | Done |
| KaTeX formulas | Done |
| Print and PDF | Done |

```csharp
var message = "Hello from MarkDNext";
Console.WriteLine(message);
```

> Relative images and local Markdown links are resolved from the document folder.

## KaTeX

Inline formula: $\alpha + \beta = \gamma$.

Display formula:

$$
\int_0^1 x^2\,dx = \frac{1}{3}
$$

## Completion

Press `Ctrl+Space` in the source editor to insert common Markdown snippets.

In WYSIWYG mode, the editor itself becomes the rendered document. Click a block to edit it, or type `/` in an empty focused block for block commands. Code blocks open as a language field plus code body editor.

Automatic completion is off by default. Use `Edit -> Automatic Completion` to enable it, or press `Ctrl+Space` for manual snippets.
