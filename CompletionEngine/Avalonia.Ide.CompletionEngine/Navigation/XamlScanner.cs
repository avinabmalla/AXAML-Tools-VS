#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Avalonia.Ide.CompletionEngine.Navigation;

/// <summary>
/// An attribute of a start tag, with source positions.
/// </summary>
public sealed class XamlAttribute
{
    internal XamlAttribute(string name, int nameStart, string value, int valueStart, int valueEnd, bool hasValue)
    {
        Name = name;
        NameStart = nameStart;
        Value = value;
        ValueStart = valueStart;
        ValueEnd = valueEnd;
        HasValue = hasValue;
    }

    /// <summary>The attribute name as written, including any prefix (e.g. <c>x:Name</c>).</summary>
    public string Name { get; }

    public int NameStart { get; }

    public int NameEnd => NameStart + Name.Length;

    /// <summary>The attribute value without quotes.</summary>
    public string Value { get; }

    /// <summary>Offset of the first character inside the quotes.</summary>
    public int ValueStart { get; }

    /// <summary>Offset of the closing quote (exclusive end of the value).</summary>
    public int ValueEnd { get; }

    /// <summary>False when the attribute has no <c>="..."</c> part yet.</summary>
    public bool HasValue { get; }

    /// <summary>The attribute name without its prefix.</summary>
    public string LocalName => Name.Substring(Name.IndexOf(':') + 1);
}

/// <summary>
/// A start tag in a XAML document, with its attributes and position in the element tree.
/// </summary>
public sealed class XamlElement
{
    private readonly List<XamlAttribute> _attributes = new();
    private readonly List<XamlElement> _children = new();

    internal XamlElement(string name, int start, int nameStart, XamlElement? parent)
    {
        Name = name;
        Start = start;
        NameStart = nameStart;
        Parent = parent;
        parent?._children.Add(this);
    }

    /// <summary>The tag name as written, e.g. <c>Button</c>, <c>local:MyControl</c> or <c>Grid.RowDefinitions</c>.</summary>
    public string Name { get; }

    /// <summary>Offset of the opening <c>&lt;</c>.</summary>
    public int Start { get; }

    public int NameStart { get; }

    public int NameEnd => NameStart + Name.Length;

    /// <summary>Offset just after the start tag's closing <c>&gt;</c> (or the end of the text if unterminated).</summary>
    public int StartTagEnd { get; internal set; }

    /// <summary>Offset just after the element's end tag (or the end of the text if never closed).</summary>
    public int End { get; internal set; }

    public bool IsSelfClosing { get; internal set; }

    public XamlElement? Parent { get; }

    public IReadOnlyList<XamlAttribute> Attributes => _attributes;

    public IReadOnlyList<XamlElement> Children => _children;

    /// <summary>True for property elements such as <c>&lt;Grid.RowDefinitions&gt;</c>.</summary>
    public bool IsPropertyElement => LocalName.Contains('.');

    /// <summary>The tag name without its prefix.</summary>
    public string LocalName => Name.Substring(Name.IndexOf(':') + 1);

    internal void Add(XamlAttribute attribute) => _attributes.Add(attribute);

    /// <summary>
    /// Gets the first attribute whose name matches one of <paramref name="names"/> exactly.
    /// </summary>
    public XamlAttribute? GetAttribute(params string[] names)
    {
        foreach (var attribute in _attributes)
        {
            foreach (var name in names)
            {
                if (attribute.Name == name)
                {
                    return attribute;
                }
            }
        }

        return null;
    }

    /// <summary>The element's <c>x:Name</c>, or its Avalonia <c>Name</c> property.</summary>
    public string? ElementName =>
        NonEmpty(GetXamlDirective("Name")?.Value) ?? NonEmpty(GetAttribute("Name")?.Value);

    /// <summary>
    /// Gets an attribute in the XAML language namespace (<c>x:</c>), whatever prefix it uses.
    /// </summary>
    public XamlAttribute? GetXamlDirective(string localName)
    {
        foreach (var attribute in _attributes)
        {
            var colon = attribute.Name.IndexOf(':');
            if (colon > 0 && attribute.Name.Substring(colon + 1) == localName && attribute.Name.StartsWith("x:", StringComparison.Ordinal))
            {
                return attribute;
            }
        }

        return null;
    }

    /// <summary>This element followed by its ancestors, innermost first.</summary>
    public IEnumerable<XamlElement> SelfAndAncestors()
    {
        for (var e = this; e != null; e = e.Parent)
        {
            yield return e;
        }
    }

    public override string ToString() => $"<{Name}> @{Start}";

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

/// <summary>
/// A forgiving, allocation-light scanner for XAML start tags. Unlike <see cref="System.Xml.XmlReader"/>
/// it copes with the half-typed, invalid documents found in an editor, and keeps source offsets.
/// </summary>
public static class XamlScanner
{
    /// <summary>
    /// Scans <paramref name="text"/> and returns every element in document order.
    /// </summary>
    public static IReadOnlyList<XamlElement> Scan(string text)
    {
        var result = new List<XamlElement>();
        var stack = new Stack<XamlElement>();
        var i = 0;

        while (i < text.Length)
        {
            var lt = text.IndexOf('<', i);
            if (lt < 0)
            {
                break;
            }

            if (StartsWith(text, lt, "<!--"))
            {
                i = SkipPast(text, lt + 4, "-->");
            }
            else if (StartsWith(text, lt, "<![CDATA["))
            {
                i = SkipPast(text, lt + 9, "]]>");
            }
            else if (StartsWith(text, lt, "<?"))
            {
                i = SkipPast(text, lt + 2, "?>");
            }
            else if (StartsWith(text, lt, "<!"))
            {
                i = SkipPast(text, lt + 2, ">");
            }
            else if (StartsWith(text, lt, "</"))
            {
                var nameStart = lt + 2;
                var nameEnd = ReadName(text, nameStart);
                var name = text.Substring(nameStart, nameEnd - nameStart);
                var gt = text.IndexOf('>', nameEnd);
                var end = gt < 0 ? text.Length : gt + 1;

                // Pop to the matching start tag; tolerate mismatched tags.
                if (stack.Any(e => e.Name == name))
                {
                    while (stack.Count > 0)
                    {
                        var e = stack.Pop();
                        e.End = end;
                        if (e.Name == name)
                        {
                            break;
                        }
                    }
                }

                i = end;
            }
            else if (lt + 1 < text.Length && IsNameStartChar(text[lt + 1]))
            {
                var element = ParseStartTag(text, lt, stack.Count > 0 ? stack.Peek() : null);
                result.Add(element);
                i = element.StartTagEnd;

                if (element.IsSelfClosing)
                {
                    element.End = element.StartTagEnd;
                }
                else
                {
                    stack.Push(element);
                }
            }
            else
            {
                i = lt + 1;
            }
        }

        while (stack.Count > 0)
        {
            stack.Pop().End = text.Length;
        }

        return result;
    }

    /// <summary>The first element of the document.</summary>
    public static XamlElement? GetRoot(IReadOnlyList<XamlElement> elements) => elements.Count > 0 ? elements[0] : null;

    /// <summary>
    /// The element whose start tag contains <paramref name="position"/> (between its
    /// <c>&lt;</c> and <c>&gt;</c>, inclusive of the end of a half-typed tag).
    /// </summary>
    public static XamlElement? FindStartTagAt(IReadOnlyList<XamlElement> elements, int position)
    {
        XamlElement? match = null;
        foreach (var element in elements)
        {
            if (element.Start > position)
            {
                break;
            }

            if (position <= element.StartTagEnd)
            {
                match = element;
            }
        }

        return match;
    }

    /// <summary>
    /// The innermost element that contains <paramref name="position"/>, either in its start tag
    /// or in its content.
    /// </summary>
    public static XamlElement? FindInnermostAt(IReadOnlyList<XamlElement> elements, int position)
    {
        XamlElement? match = null;
        foreach (var element in elements)
        {
            if (element.Start > position)
            {
                break;
            }

            if (position < element.End)
            {
                match = element;
            }
        }

        return match;
    }

    /// <summary>The attribute whose name or value contains <paramref name="position"/>.</summary>
    public static XamlAttribute? FindAttributeAt(XamlElement element, int position) =>
        element.Attributes.FirstOrDefault(a =>
            (position >= a.NameStart && position <= a.NameEnd) ||
            (a.HasValue && position >= a.ValueStart && position <= a.ValueEnd));

    private static XamlElement ParseStartTag(string text, int lt, XamlElement? parent)
    {
        var nameStart = lt + 1;
        var nameEnd = ReadName(text, nameStart);
        var element = new XamlElement(text.Substring(nameStart, nameEnd - nameStart), lt, nameStart, parent);
        var i = nameEnd;

        while (i < text.Length)
        {
            i = SkipWhitespace(text, i);
            if (i >= text.Length)
            {
                break;
            }

            var c = text[i];
            if (c == '>')
            {
                element.StartTagEnd = i + 1;
                return element;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '>')
            {
                element.IsSelfClosing = true;
                element.StartTagEnd = i + 2;
                return element;
            }

            if (c == '<')
            {
                // Unterminated tag (still being typed): stop before the next tag.
                element.StartTagEnd = i;
                return element;
            }

            if (!IsNameStartChar(c))
            {
                i++;
                continue;
            }

            var attrStart = i;
            var attrEnd = ReadName(text, attrStart);
            var name = text.Substring(attrStart, attrEnd - attrStart);
            i = SkipWhitespace(text, attrEnd);

            if (i < text.Length && text[i] == '=')
            {
                i = SkipWhitespace(text, i + 1);
                if (i < text.Length && (text[i] == '"' || text[i] == '\''))
                {
                    var quote = text[i];
                    var valueStart = i + 1;
                    var close = text.IndexOf(quote, valueStart);
                    var valueEnd = close < 0 ? text.Length : close;

                    // XML forbids a raw '<' inside attribute values, so one before the closing
                    // quote means the value is unterminated (still being typed): end it there.
                    var nextTag = text.IndexOf('<', valueStart);
                    var unterminated = nextTag >= 0 && (close < 0 || nextTag < close);
                    if (unterminated)
                    {
                        valueEnd = nextTag;
                    }

                    element.Add(new XamlAttribute(name, attrStart, text.Substring(valueStart, valueEnd - valueStart), valueStart, valueEnd, hasValue: true));

                    if (unterminated)
                    {
                        element.StartTagEnd = valueEnd;
                        return element;
                    }

                    i = close < 0 ? valueEnd : close + 1;
                    continue;
                }
            }

            element.Add(new XamlAttribute(name, attrStart, "", attrEnd, attrEnd, hasValue: false));
        }

        element.StartTagEnd = text.Length;
        return element;
    }

    private static int ReadName(string text, int start)
    {
        var i = start;
        while (i < text.Length && IsNameChar(text[i]))
        {
            i++;
        }

        return i;
    }

    private static int SkipWhitespace(string text, int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        return i;
    }

    private static int SkipPast(string text, int start, string terminator)
    {
        var index = text.IndexOf(terminator, start, StringComparison.Ordinal);
        return index < 0 ? text.Length : index + terminator.Length;
    }

    private static bool StartsWith(string text, int index, string value) =>
        string.CompareOrdinal(text, index, value, 0, value.Length) == 0;

    private static bool IsNameStartChar(char c) => char.IsLetter(c) || c == '_' || c == ':';

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == ':' || c == '.' || c == '-';
}
