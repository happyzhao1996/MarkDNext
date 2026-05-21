using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace MarkDNext;

public sealed class MarkdownColorizer : DocumentColorizingTransformer
{
    private static readonly Regex LinkRegex = new(@"\[[^\]]+\]\([^)]+\)", RegexOptions.Compiled);
    private static readonly Regex InlineCodeRegex = new(@"`[^`]+`", RegexOptions.Compiled);
    private static readonly Regex MathRegex = new(@"\$\$.*?\$\$|\$[^$\r\n]+\$", RegexOptions.Compiled);
    private static readonly Regex StrongRegex = new(@"(\*\*|__)[^\r\n]+?(\*\*|__)", RegexOptions.Compiled);
    private static readonly Regex EmphasisRegex = new(@"(^|[^\*])(\*|_)[^\*_]+(\*|_)", RegexOptions.Compiled);
    private static readonly Regex ListRegex = new(@"^\s*([-+*]|\d+\.)\s+", RegexOptions.Compiled);

    protected override void ColorizeLine(DocumentLine line)
    {
        var text = CurrentContext.Document.GetText(line);
        if (text.Length == 0)
        {
            return;
        }

        var offset = line.Offset;
        var trimmed = text.TrimStart();

        if (IsInsideBlock(line, "```"))
        {
            ColorRange(offset, line.EndOffset, new SolidColorBrush(Color.FromRgb(124, 58, 237)));
            return;
        }

        if (IsInsideBlock(line, "$$"))
        {
            ColorRange(offset, line.EndOffset, new SolidColorBrush(Color.FromRgb(162, 28, 175)));
            return;
        }

        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            ColorRange(offset, line.EndOffset, Brushes.MediumPurple, FontWeights.SemiBold);
            return;
        }

        if (trimmed.StartsWith("$$", StringComparison.Ordinal))
        {
            ColorRange(offset, line.EndOffset, new SolidColorBrush(Color.FromRgb(162, 28, 175)), FontWeights.SemiBold);
            return;
        }

        if (trimmed.StartsWith("#", StringComparison.Ordinal))
        {
            ColorRange(offset, line.EndOffset, new SolidColorBrush(Color.FromRgb(11, 92, 173)), FontWeights.Bold);
            return;
        }

        if (trimmed.StartsWith(">", StringComparison.Ordinal))
        {
            ColorRange(offset, line.EndOffset, Brushes.SlateGray, FontWeights.Normal, FontStyles.Italic);
        }

        var listMatch = ListRegex.Match(text);
        if (listMatch.Success)
        {
            ColorRange(offset + listMatch.Index, offset + listMatch.Index + listMatch.Length, Brushes.Teal, FontWeights.SemiBold);
        }

        ColorMatches(offset, text, LinkRegex, new SolidColorBrush(Color.FromRgb(11, 102, 195)));
        ColorMatches(offset, text, StrongRegex, Brushes.Black, FontWeights.Bold);
        ColorMatches(offset, text, EmphasisRegex, new SolidColorBrush(Color.FromRgb(52, 73, 94)), FontWeights.Normal, FontStyles.Italic);
        ColorMatches(offset, text, MathRegex, new SolidColorBrush(Color.FromRgb(162, 28, 175)));
        ColorMatches(offset, text, InlineCodeRegex, new SolidColorBrush(Color.FromRgb(154, 52, 18)));
    }

    private bool IsInsideBlock(DocumentLine line, string marker)
    {
        var document = CurrentContext.Document;
        var inside = false;

        for (var lineNumber = 1; lineNumber <= line.LineNumber; lineNumber++)
        {
            var currentLine = document.GetLineByNumber(lineNumber);
            var text = document.GetText(currentLine).TrimStart();
            if (text.StartsWith(marker, StringComparison.Ordinal))
            {
                if (lineNumber == line.LineNumber)
                {
                    return inside;
                }

                inside = !inside;
            }
        }

        return inside;
    }

    private void ColorMatches(int lineOffset, string text, Regex regex, Brush brush, FontWeight? weight = null, FontStyle? style = null)
    {
        foreach (Match match in regex.Matches(text))
        {
            if (!match.Success || match.Length == 0)
            {
                continue;
            }

            ColorRange(lineOffset + match.Index, lineOffset + match.Index + match.Length, brush, weight, style);
        }
    }

    private void ColorRange(int startOffset, int endOffset, Brush brush, FontWeight? weight = null, FontStyle? style = null)
    {
        if (endOffset <= startOffset)
        {
            return;
        }

        ChangeLinePart(startOffset, endOffset, element =>
        {
            element.TextRunProperties.SetForegroundBrush(brush);
            if (weight is not null || style is not null)
            {
                var current = element.TextRunProperties.Typeface;
                element.TextRunProperties.SetTypeface(new Typeface(
                    current.FontFamily,
                    style ?? current.Style,
                    weight ?? current.Weight,
                    current.Stretch));
            }
        });
    }
}
