#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Avalonia.Ide.CompletionEngine.Navigation;

/// <summary>
/// "Which XAML element is at this point of the preview?" The previewer protocol has no hit
/// testing, so this builds an ID render: a copy of the XAML where every control's brushes are set
/// to a colour unique to that element. Rendering it and reading the pixel under the mouse
/// identifies the element. Only brushes change, so layout is identical to the real preview.
/// </summary>
public sealed class XamlPickingMap
{
    private readonly Dictionary<int, XamlElement> _byColor;

    internal XamlPickingMap(string xaml, Dictionary<int, XamlElement> byColor)
    {
        Xaml = xaml;
        _byColor = byColor;
    }

    /// <summary>The XAML to render for picking.</summary>
    public string Xaml { get; }

    /// <summary>The number of elements that got an ID colour.</summary>
    public int Count => _byColor.Count;

    /// <summary>The element whose ID colour is exactly <paramref name="rgb"/> (0xRRGGBB), or null.</summary>
    public XamlElement? Lookup(int rgb) => _byColor.TryGetValue(rgb & 0xFFFFFF, out var element) ? element : null;

    /// <summary>
    /// Finds the element at (<paramref name="x"/>, <paramref name="y"/>) in a 32-bit BGRA frame.
    /// The exact pixel may belong to something we didn't colour (a theme's hover brush, image
    /// content, anti-aliased edges), so nearby pixels are searched outwards, nearest first.
    /// </summary>
    public XamlElement? HitTest(byte[] bgra, int width, int height, int stride, int x, int y, int searchRadius = 8)
    {
        for (var radius = 0; radius <= searchRadius; radius++)
        {
            for (var dy = -radius; dy <= radius; dy++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    // Only the ring at this radius (inner rings were already checked).
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius)
                    {
                        continue;
                    }

                    var px = x + dx;
                    var py = y + dy;
                    if (px < 0 || py < 0 || px >= width || py >= height)
                    {
                        continue;
                    }

                    var i = py * stride + px * 4;
                    if (i + 3 >= bgra.Length)
                    {
                        continue;
                    }

                    var rgb = (bgra[i + 2] << 16) | (bgra[i + 1] << 8) | bgra[i];
                    if (Lookup(rgb) is { } element)
                    {
                        return element;
                    }
                }
            }
        }

        return null;
    }
}

public static class XamlPicking
{
    /// <summary>Brush properties set to the ID colour, when the element's type has them.</summary>
    private static readonly string[] s_brushProperties = { "Background", "Foreground", "BorderBrush", "Fill", "Stroke" };

    /// <summary>
    /// Builds the ID render for <paramref name="document"/>. <paramref name="helper"/> (with
    /// metadata loaded for the document) decides which brush properties each element's type has;
    /// elements with none (styles, setters, resources, property elements) are left alone.
    /// </summary>
    public static XamlPickingMap Create(XamlDocument document, CompletionEngine.MetadataHelper helper)
    {
        var edits = new List<(int Start, int Length, string Text)>();
        var byColor = new Dictionary<int, XamlElement>();
        var id = 0;

        foreach (var element in document.Elements)
        {
            if (element.IsPropertyElement)
            {
                continue;
            }

            var type = helper.LookupType(element.Name);
            if (type is null)
            {
                continue;
            }

            // Disabled controls draw with the theme's disabled brushes on their template parts
            // (and may be faded), hiding our ID colour. Force them enabled in the ID render, and
            // drop Command, since a command that can't execute disables its control too. Layout
            // doesn't depend on either.
            if (HasSettableProperty(type, "IsEnabled"))
            {
                var isEnabled = element.GetAttribute("IsEnabled");
                if (isEnabled is { HasValue: true })
                {
                    edits.Add((isEnabled.ValueStart, isEnabled.ValueEnd - isEnabled.ValueStart, "True"));
                }
            }

            if (element.GetAttribute("Command") is { HasValue: true } command)
            {
                // Remove the whole attribute, including its closing quote.
                edits.Add((command.NameStart, command.ValueEnd + 1 - command.NameStart, ""));
            }

            var properties = s_brushProperties
                .Where(p => HasSettableProperty(type, p))
                // Set as a property element (<Button.Background>...): setting it again would fail to load.
                .Where(p => !element.Children.Any(c => c.LocalName == element.LocalName + "." + p))
                .ToList();

            if (properties.Count == 0)
            {
                continue;
            }

            var rgb = ColorFor(++id);
            byColor[rgb] = element;
            var color = "#FF" + rgb.ToString("X6", CultureInfo.InvariantCulture);

            foreach (var property in properties)
            {
                var existing = element.GetAttribute(property);
                if (existing is { HasValue: true })
                {
                    edits.Add((existing.ValueStart, existing.ValueEnd - existing.ValueStart, color));
                }
                else if (existing is null)
                {
                    edits.Add((element.NameEnd, 0, $" {property}=\"{color}\""));
                }
            }
        }

        var text = new StringBuilder(document.Text);
        foreach (var edit in edits.OrderByDescending(e => e.Start))
        {
            text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Text);
        }

        return new XamlPickingMap(text.ToString(), byColor);
    }

    private static bool HasSettableProperty(MetadataType type, string name) =>
        type.Properties.Any(p => p.Name == name && !p.IsAttached && p.HasSetter);

    /// <summary>
    /// A distinct, opaque colour for each ID: multiplying by an odd constant is a bijection on 24
    /// bits, and spreads consecutive IDs far apart so anti-aliasing doesn't land on a neighbour.
    /// Avoids pure black and white, which themes draw everywhere.
    /// </summary>
    internal static int ColorFor(int id)
    {
        var rgb = (id * 0x9E3779) & 0xFFFFFF;
        return rgb is 0 or 0xFFFFFF ? (rgb ^ 0x010101) : rgb;
    }
}
