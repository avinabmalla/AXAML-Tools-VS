#nullable enable
using System.Collections.Generic;

namespace Avalonia.Ide.CompletionEngine.Navigation;

/// <summary>
/// A XAML text and its scanned elements. Scan once per edit and share it between features
/// (indent guides, binding checks, navigation, indentation) instead of each re-scanning.
/// </summary>
public sealed class XamlDocument
{
    private XamlDocument(string text, IReadOnlyList<XamlElement> elements)
    {
        Text = text;
        Elements = elements;
    }

    public string Text { get; }

    public IReadOnlyList<XamlElement> Elements { get; }

    public XamlElement? Root => XamlScanner.GetRoot(Elements);

    public static XamlDocument Parse(string text) => new(text, XamlScanner.Scan(text));
}
