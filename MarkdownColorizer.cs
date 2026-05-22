using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace MarkDNext;

public sealed class MarkdownColorizer : DocumentColorizingTransformer
{
    private static readonly Regex LinkRegex = new(@"\[(?<text>(?:\\.|[^\]\\])+)\]\((?<target>(?:\\.|[^\)\\])+)\)", RegexOptions.Compiled);
    private static readonly Regex InlineCodeRegex = new(@"`[^`]+`", RegexOptions.Compiled);
    private static readonly Regex MathRegex = new(@"\$\$.*?\$\$|\$[^$\r\n]+\$", RegexOptions.Compiled);
    private static readonly Regex StrongRegex = new(@"(\*\*|__)[^\r\n]+?(\*\*|__)", RegexOptions.Compiled);
    private static readonly Regex EmphasisRegex = new(@"(^|[^\*])(\*|_)[^\*_]+(\*|_)", RegexOptions.Compiled);
    private static readonly Regex ListRegex = new(@"^\s*([-+*]|\d+\.)\s+", RegexOptions.Compiled);

    private Brush _headingBrush = CreateBrush("#0b5cad");
    private Brush _linkBrush = CreateBrush("#0b66c3");
    private Brush _linkTargetBrush = CreateBrush("#64748b");
    private Brush _mutedBrush = CreateBrush("#64748b");
    private Brush _accentBrush = CreateBrush("#0f766e");
    private Brush _codeBrush = CreateBrush("#9a3412");
    private Brush _mathBrush = CreateBrush("#a21caf");
    private Brush _textBrush = CreateBrush("#111827");

    public void ApplyTheme(string heading, string link, string linkTarget, string muted, string accent, string code, string text)
    {
        _headingBrush = CreateBrush(heading);
        _linkBrush = CreateBrush(link);
        _linkTargetBrush = CreateBrush(linkTarget);
        _mutedBrush = CreateBrush(muted);
        _accentBrush = CreateBrush(accent);
        _codeBrush = CreateBrush(code);
        _mathBrush = CreateBrush(heading);
        _textBrush = CreateBrush(text);
    }

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
            ColorRange(offset, line.EndOffset, _codeBrush);
            return;
        }

        if (IsInsideBlock(line, "$$"))
        {
            ColorRange(offset, line.EndOffset, _mathBrush);
            return;
        }

        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            ColorRange(offset, line.EndOffset, _codeBrush, FontWeights.SemiBold);
            return;
        }

        if (trimmed.StartsWith("$$", StringComparison.Ordinal))
        {
            ColorRange(offset, line.EndOffset, _mathBrush, FontWeights.SemiBold);
            return;
        }

        if (trimmed.StartsWith("#", StringComparison.Ordinal))
        {
            ColorRange(offset, line.EndOffset, _headingBrush, FontWeights.Bold);
            return;
        }

        if (trimmed.StartsWith(">", StringComparison.Ordinal))
        {
            ColorRange(offset, line.EndOffset, _mutedBrush, FontWeights.Normal, FontStyles.Italic);
        }

        var listMatch = ListRegex.Match(text);
        if (listMatch.Success)
        {
            ColorRange(offset + listMatch.Index, offset + listMatch.Index + listMatch.Length, _accentBrush, FontWeights.SemiBold);
        }

        ColorLinks(offset, text);
        ColorMatches(offset, text, StrongRegex, _textBrush, FontWeights.Bold);
        ColorMatches(offset, text, EmphasisRegex, _mutedBrush, FontWeights.Normal, FontStyles.Italic);
        ColorMatches(offset, text, MathRegex, _mathBrush);
        ColorMatches(offset, text, InlineCodeRegex, _codeBrush);
    }

    private void ColorLinks(int lineOffset, string text)
    {
        foreach (Match match in LinkRegex.Matches(text))
        {
            if (!match.Success || match.Length == 0)
            {
                continue;
            }

            var label = match.Groups["text"];
            var target = match.Groups["target"];
            ColorRange(lineOffset + match.Index, lineOffset + match.Index + match.Length, _mutedBrush);
            ColorRange(lineOffset + label.Index, lineOffset + label.Index + label.Length, _linkBrush, FontWeights.SemiBold);
            ColorRange(lineOffset + target.Index, lineOffset + target.Index + target.Length, _linkTargetBrush);
        }
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

    private static Brush CreateBrush(string hex)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.Black;
        }
    }
}
