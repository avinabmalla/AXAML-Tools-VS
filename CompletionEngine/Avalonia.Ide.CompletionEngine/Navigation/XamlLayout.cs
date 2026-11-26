#nullable enable
using System;
using System.Collections.Generic;

namespace Avalonia.Ide.CompletionEngine.Navigation;

/// <summary>An element that spans several lines: drawn with an indent guide.</summary>
public readonly record struct XamlBlock(int Start, int EndTagStart);

/// <summary>
/// Layout helpers for the XAML editor: indent guide blocks and smart indentation.
/// </summary>
public static class XamlLayout
{
    /// <summary>
    /// Elements whose end tag is on a later line than their start, as (start of <c>&lt;</c>,
    /// start of <c>&lt;/</c>). Self-closing and single-line elements are skipped.
    /// </summary>
    public static IReadOnlyList<XamlBlock> GetBlocks(string text) => GetBlocks(XamlDocument.Parse(text));

    /// <inheritdoc cref="GetBlocks(string)"/>
    public static IReadOnlyList<XamlBlock> GetBlocks(XamlDocument document)
    {
        var text = document.Text;
        var result = new List<XamlBlock>();

        foreach (var element in document.Elements)
        {
            if (element.IsSelfClosing || element.End <= element.StartTagEnd)
            {
                continue;
            }

            // The end tag begins at the last "</" before End (End is just after its '>').
            var endTagStart = text.LastIndexOf("</", Math.Max(0, element.End - 1), StringComparison.Ordinal);
            if (endTagStart < element.StartTagEnd)
            {
                continue; // Unclosed element (End is the end of the text).
            }

            if (text.IndexOf('\n', element.Start, endTagStart - element.Start) >= 0)
            {
                result.Add(new XamlBlock(element.Start, endTagStart));
            }
        }

        return result;
    }

    /// <summary>
    /// The column the line starting at <paramref name="lineStart"/> should be indented to, or null
    /// to leave it alone. Columns count a tab as <paramref name="tabSize"/> columns.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Inside a start tag: aligned with the first attribute on the tag's first line
    /// (<c>&lt;Button Content="a"</c> → under <c>Content</c>), or one level in from the
    /// <c>&lt;</c> if no attribute is on that line.</item>
    /// <item>Before an end tag: at the element's <c>&lt;</c>.</item>
    /// <item>Inside an element's content: one level in from its <c>&lt;</c>.</item>
    /// </list>
    /// </remarks>
    public static int? GetIndentColumn(string text, int lineStart, int tabSize, int indentSize) =>
        GetIndentColumn(XamlDocument.Parse(text), lineStart, tabSize, indentSize);

    /// <inheritdoc cref="GetIndentColumn(string, int, int, int)"/>
    public static int? GetIndentColumn(XamlDocument document, int lineStart, int tabSize, int indentSize)
    {
        var text = document.Text;
        if (lineStart < 0 || lineStart > text.Length)
        {
            return null;
        }

        var elements = document.Elements;

        // Inside a start tag that continues past this line start?
        var tag = XamlScanner.FindStartTagAt(elements, lineStart);

        // Whitespace inside an attribute value is part of the value: never touch it.
        if (tag is not null && IsInsideAttributeValue(tag, lineStart))
        {
            return null;
        }

        if (tag is not null && lineStart > tag.NameEnd && lineStart < tag.StartTagEnd)
        {
            var tagLineStart = LineStartOf(text, tag.Start);
            var tagLineEnd = LineEndOf(text, tag.Start);

            foreach (var attribute in tag.Attributes)
            {
                if (attribute.NameStart >= tagLineEnd)
                {
                    break;
                }

                if (attribute.NameStart > tag.NameEnd && attribute.NameStart < lineStart)
                {
                    return ColumnOf(text, tagLineStart, attribute.NameStart, tabSize);
                }
            }

            return ColumnOf(text, tagLineStart, tag.Start, tabSize) + indentSize;
        }

        // In content: find the innermost element whose start tag ends before this line.
        XamlElement? parent = null;
        foreach (var element in elements)
        {
            if (element.Start >= lineStart)
            {
                break;
            }

            if (element.StartTagEnd <= lineStart && lineStart < element.End && !element.IsSelfClosing)
            {
                parent = element;
            }
        }

        if (parent is null)
        {
            return 0;
        }

        var parentColumn = ColumnOf(text, LineStartOf(text, parent.Start), parent.Start, tabSize);
        var firstNonSpace = lineStart;
        while (firstNonSpace < text.Length && (text[firstNonSpace] == ' ' || text[firstNonSpace] == '\t'))
        {
            firstNonSpace++;
        }

        var closesParent = string.CompareOrdinal(text, firstNonSpace, "</", 0, 2) == 0;
        return closesParent ? parentColumn : parentColumn + indentSize;
    }

    /// <summary>
    /// When Enter at <paramref name="caret"/> should leave the attribute value instead of putting
    /// a newline inside it, returns the position just after the closing quote (where the newline
    /// should go); otherwise null.
    /// </summary>
    /// <remarks>
    /// Only when the caret is directly before the closing quote of a start tag's attribute: that's
    /// "done with this value". A newline inside an attribute value isn't meaningful (XML turns it
    /// into a space), but in the middle of a value, or inside a <c>{...}</c> markup extension
    /// that's being split across lines, Enter keeps its normal behaviour.
    /// </remarks>
    public static int? GetAttributeExitPosition(XamlDocument document, int caret)
    {
        var text = document.Text;
        var tag = XamlScanner.FindStartTagAt(document.Elements, caret);
        if (tag is null)
        {
            return null;
        }

        foreach (var attribute in tag.Attributes)
        {
            if (!attribute.HasValue || caret != attribute.ValueEnd)
            {
                continue;
            }

            // The value must actually be closed here (not an unterminated value that runs on).
            if (attribute.ValueEnd >= text.Length || (text[attribute.ValueEnd] != '"' && text[attribute.ValueEnd] != '\''))
            {
                return null;
            }

            // Inside an unfinished markup extension ("{Binding Path=X, |"): keep normal Enter.
            if (attribute.Value.TrimStart().StartsWith("{", StringComparison.Ordinal) && !IsBalanced(attribute.Value))
            {
                return null;
            }

            return attribute.ValueEnd + 1;
        }

        return null;
    }

    private static bool IsBalanced(string value)
    {
        var depth = 0;
        foreach (var c in value)
        {
            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
            }
        }

        return depth <= 0;
    }

    /// <summary>Whitespace for <paramref name="column"/> using the editor's tab settings.</summary>
    public static string MakeIndent(int column, bool useTabs, int tabSize)
    {
        if (!useTabs || tabSize <= 0)
        {
            return new string(' ', Math.Max(0, column));
        }

        return new string('\t', column / tabSize) + new string(' ', column % tabSize);
    }

    private static bool IsInsideAttributeValue(XamlElement tag, int position)
    {
        foreach (var attribute in tag.Attributes)
        {
            if (attribute.HasValue && position > attribute.ValueStart && position <= attribute.ValueEnd)
            {
                return true;
            }
        }

        return false;
    }

    private static int LineStartOf(string text, int position)
    {
        var newline = position > 0 ? text.LastIndexOf('\n', position - 1) : -1;
        return newline + 1;
    }

    private static int LineEndOf(string text, int position)
    {
        var newline = text.IndexOf('\n', position);
        return newline < 0 ? text.Length : newline;
    }

    private static int ColumnOf(string text, int lineStart, int position, int tabSize)
    {
        var column = 0;
        for (var i = lineStart; i < position && i < text.Length; i++)
        {
            column = text[i] == '\t' && tabSize > 0 ? (column / tabSize + 1) * tabSize : column + 1;
        }

        return column;
    }
}
