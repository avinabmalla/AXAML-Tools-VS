#nullable enable
using System.Text;

namespace Avalonia.Ide.CompletionEngine.Navigation;

/// <summary>
/// Builds names for generated event handlers.
/// </summary>
public static class EventHandlerNaming
{
    /// <summary><c>SaveButton_Click</c>.</summary>
    public const string DefaultPattern = "{Name}_{Event}";

    /// <summary><c>OnSaveButtonClick</c>.</summary>
    public const string OnPrefixPattern = "On{Name}{Event}";

    /// <summary>
    /// Creates the handler name for <paramref name="eventAttributeName"/> on <paramref name="element"/>.
    /// </summary>
    /// <remarks>
    /// <c>{Name}</c> is the element's <c>x:Name</c> (or <c>Name</c>), falling back to its type name
    /// when it has none; <c>{Type}</c> is always the type name; <c>{Event}</c> is the event name
    /// without any owner prefix (<c>Gestures.Tapped</c> → <c>Tapped</c>).
    /// </remarks>
    public static string Create(XamlElement element, string eventAttributeName, string? pattern = null)
    {
        var typeName = element.LocalName;
        var name = element.ElementName ?? typeName;
        return Create(name, typeName, eventAttributeName, pattern);
    }

    public static string Create(string elementName, string typeName, string eventAttributeName, string? pattern = null)
    {
        var dot = eventAttributeName.LastIndexOf('.');
        var eventName = dot >= 0 ? eventAttributeName.Substring(dot + 1) : eventAttributeName;
        var colon = typeName.IndexOf(':');
        if (colon >= 0)
        {
            typeName = typeName.Substring(colon + 1);
        }

        var result = (string.IsNullOrWhiteSpace(pattern) ? DefaultPattern : pattern!)
            .Replace("{Name}", elementName)
            .Replace("{Type}", typeName)
            .Replace("{Event}", eventName);

        return ToIdentifier(result);
    }

    /// <summary>Replaces characters that aren't valid in a C# identifier.</summary>
    public static string ToIdentifier(string value)
    {
        var sb = new StringBuilder(value.Length + 1);
        foreach (var c in value)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        }

        if (sb.Length == 0 || char.IsDigit(sb[0]))
        {
            sb.Insert(0, '_');
        }

        return sb.ToString();
    }
}
