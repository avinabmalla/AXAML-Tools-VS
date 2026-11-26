#nullable enable
using System;
using System.Linq;
using System.Text;

namespace Avalonia.Ide.CompletionEngine.Navigation;

/// <summary>
/// Adjusts XAML before it's sent to the previewer.
/// </summary>
public static class XamlPreviewTransform
{
    /// <summary>
    /// Removes event handler attributes (<c>Click="Save_Click"</c>, attached ones such as
    /// <c>Gestures.Tapped="..."</c>), so interacting with the preview never runs code-behind
    /// handlers. Each removed attribute is replaced by spaces of the same length (newlines kept),
    /// so line and column numbers in previewer errors still match the editor.
    /// </summary>
    /// <param name="helper">Metadata loaded for the document, used to tell events from properties.</param>
    public static string RemoveEventHandlers(XamlDocument document, CompletionEngine.MetadataHelper helper)
    {
        if (helper.Metadata is null)
        {
            return document.Text;
        }

        StringBuilder? text = null;

        foreach (var element in document.Elements)
        {
            if (element.IsPropertyElement)
            {
                continue;
            }

            var type = helper.LookupType(element.Name);

            foreach (var attribute in element.Attributes)
            {
                if (!attribute.HasValue || !IsEvent(helper, type, attribute.Name))
                {
                    continue;
                }

                // From the attribute name to its closing quote.
                var start = attribute.NameStart;
                var end = Math.Min(document.Text.Length, attribute.ValueEnd + 1);

                text ??= new StringBuilder(document.Text);
                for (var i = start; i < end; i++)
                {
                    if (text[i] != '\n' && text[i] != '\r')
                    {
                        text[i] = ' ';
                    }
                }
            }
        }

        return text?.ToString() ?? document.Text;
    }

    private static bool IsEvent(CompletionEngine.MetadataHelper helper, MetadataType? elementType, string attributeName)
    {
        var dot = attributeName.IndexOf('.', attributeName.IndexOf(':') + 1);
        if (dot > 0)
        {
            // Owner.Name: any event of that name on the owner (routed events registered on static
            // classes such as Gestures aren't always flagged as attached in the metadata).
            // Gestures.Tapped and similar: the owner may not be known to the metadata at all (Avalonia
            // 12 doesn't expose Gestures to XAML), but the same routed event exists on the element.
            var owner = helper.LookupType(attributeName.Substring(0, dot));
            var name = attributeName.Substring(dot + 1);
            return owner?.Events.Any(e => e.Name == name) == true ||
                   elementType?.Events.Any(e => e.Name == name) == true;
        }

        return elementType?.Events.Any(e => !e.IsAttached && e.Name == attributeName) == true;
    }
}
