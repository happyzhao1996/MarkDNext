using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using System.Windows.Media;

namespace MarkDNext;

public sealed class MarkdownCompletionData : ICompletionData
{
    private readonly string _insertText;
    private readonly int _caretOffset;

    public MarkdownCompletionData(string text, string description, string? insertText = null, int? caretOffset = null)
    {
        Text = text;
        Description = description;
        _insertText = insertText ?? text;
        _caretOffset = caretOffset ?? _insertText.Length;
    }

    public ImageSource? Image => null;

    public string Text { get; }

    public object Content => Text;

    public object Description { get; }

    public double Priority => 0;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        textArea.Document.Replace(completionSegment, _insertText);
        textArea.Caret.Offset = Math.Min(textArea.Document.TextLength, completionSegment.Offset + _caretOffset);
    }
}
