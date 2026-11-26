#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Avalonia.Ide.CompletionEngine.Navigation;

/// <summary>
/// Works out what is under the caret in a XAML document and what Go To Definition should
/// navigate to. Pure text analysis: resolving the result to symbols is the host's job.
/// </summary>
public static class XamlNavigationClassifier
{
    private static readonly HashSet<string> s_typeValuedAttributes = new(StringComparer.Ordinal)
    {
        "x:DataType", "DataType", "TargetType", "x:TypeArguments",
    };

    private static readonly HashSet<string> s_uriValuedTags = new(StringComparer.Ordinal)
    {
        "StyleInclude", "ResourceInclude", "MergeResourceInclude",
    };

    /// <summary>
    /// Classifies <paramref name="position"/> in <paramref name="text"/>. Returns null when there
    /// is nothing to navigate to.
    /// </summary>
    /// <param name="helper">
    /// Completion metadata for the document, used to resolve Avalonia types and to tell events
    /// from properties. Optional: without it, only <c>clr-namespace</c>/<c>using</c> types resolve
    /// and event attributes aren't recognised.
    /// </param>
    public static XamlNavigationTarget? Classify(string text, int position, CompletionEngine.MetadataHelper? helper = null)
    {
        var elements = XamlScanner.Scan(text);
        var element = XamlScanner.FindStartTagAt(elements, position);
        if (element is null)
        {
            return null;
        }

        var resolver = new XamlTypeNameResolver(text, helper);
        var root = XamlScanner.GetRoot(elements);
        var xClass = root?.GetXamlDirective("Class")?.Value;
        var context = new Context(text, elements, element, resolver, helper, xClass);

        if (position >= element.NameStart && position <= element.NameEnd)
        {
            return ClassifyElementName(context, position);
        }

        var attribute = XamlScanner.FindAttributeAt(element, position);
        if (attribute is null)
        {
            return null;
        }

        if (position >= attribute.NameStart && position <= attribute.NameEnd)
        {
            return ClassifyAttributeName(context, attribute, position);
        }

        return ClassifyAttributeValue(context, attribute, position);
    }

    private static XamlNavigationTarget? ClassifyElementName(Context c, int position)
    {
        var element = c.Element;
        var name = element.Name;
        var dot = name.IndexOf('.', name.IndexOf(':') + 1);

        if (dot > 0 && position > element.NameStart + dot)
        {
            // <Grid.RowDefinitions>: the property part.
            var ownerXaml = name.Substring(0, dot);
            var member = name.Substring(dot + 1);
            return new XamlNavigationTarget(XamlNavigationKind.Property, element.NameStart + dot + 1, member.Length, member)
            {
                XamlTypeName = ownerXaml,
                TypeName = c.Resolver.Resolve(ownerXaml),
                MemberName = member,
                XClassName = c.XClass,
            };
        }

        var typePart = dot > 0 ? name.Substring(0, dot) : name;
        return TypeTarget(c, typePart, element.NameStart);
    }

    private static XamlNavigationTarget? ClassifyAttributeName(Context c, XamlAttribute attribute, int position)
    {
        var name = attribute.Name;

        if (name == "xmlns" || name.StartsWith("xmlns:", StringComparison.Ordinal) || IsDirectiveOrDesignTime(c, name))
        {
            return null;
        }

        var colon = name.IndexOf(':');
        var dot = name.IndexOf('.', colon + 1);

        if (dot > 0)
        {
            // Attached property or event: Grid.Row, local:Behaviors.Enabled.
            var ownerXaml = name.Substring(0, dot);
            if (position <= attribute.NameStart + dot)
            {
                return TypeTarget(c, ownerXaml, attribute.NameStart);
            }

            var member = name.Substring(dot + 1);
            return new XamlNavigationTarget(XamlNavigationKind.Property, attribute.NameStart + dot + 1, member.Length, member)
            {
                XamlTypeName = ownerXaml,
                TypeName = c.Resolver.Resolve(ownerXaml),
                MemberName = member,
                IsAttached = true,
                XClassName = c.XClass,
            };
        }

        if (c.Element.IsPropertyElement)
        {
            return null;
        }

        // A property (or event) of the element's own type.
        return new XamlNavigationTarget(XamlNavigationKind.Property, attribute.NameStart, name.Length, name)
        {
            XamlTypeName = c.Element.Name,
            TypeName = c.DeclaringTypeOf(c.Element.Name, name) ?? c.Resolver.Resolve(c.Element.Name),
            MemberName = name,
            XClassName = c.XClass,
        };
    }

    private static XamlNavigationTarget? ClassifyAttributeValue(Context c, XamlAttribute attribute, int position)
    {
        var value = attribute.Value;
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        var valueOffset = attribute.ValueStart + (value.Length - value.TrimStart().Length);

        if (trimmed.StartsWith("{", StringComparison.Ordinal) && !trimmed.StartsWith("{}", StringComparison.Ordinal))
        {
            var markup = MarkupExtensionSyntax.Parse(c.Text, valueOffset, attribute.ValueEnd);
            return markup is null ? null : ClassifyMarkup(c, markup.FindInnermost(position), position);
        }

        if (IsXamlDirective(c, attribute.Name, "Class"))
        {
            return new XamlNavigationTarget(XamlNavigationKind.XClass, valueOffset, trimmed.Length, trimmed)
            {
                TypeName = trimmed,
                XClassName = c.XClass,
            };
        }

        if (IsXamlDirective(c, attribute.Name, "Name") || (attribute.Name == "Name" && !c.Element.IsPropertyElement))
        {
            return new XamlNavigationTarget(XamlNavigationKind.XName, valueOffset, trimmed.Length, trimmed)
            {
                MemberName = trimmed,
                XClassName = c.XClass,
            };
        }

        if (s_typeValuedAttributes.Contains(attribute.Name) || IsXamlDirective(c, attribute.Name, "DataType"))
        {
            return TypeTarget(c, trimmed, valueOffset);
        }

        if (attribute.Name == "Source" && s_uriValuedTags.Contains(c.Element.LocalName))
        {
            return new XamlNavigationTarget(XamlNavigationKind.Uri, valueOffset, trimmed.Length, trimmed)
            {
                Uri = trimmed,
                XClassName = c.XClass,
            };
        }

        if (c.TryGetEvent(attribute.Name, out var ownerType, out var eventName, out var isAttached) && IsIdentifier(trimmed))
        {
            return new XamlNavigationTarget(XamlNavigationKind.EventHandler, valueOffset, trimmed.Length, trimmed)
            {
                MemberName = trimmed,
                EventOwnerTypeName = ownerType,
                EventName = eventName,
                IsAttached = isAttached,
                TypeName = c.XClass,
                XClassName = c.XClass,
            };
        }

        return null;
    }

    private static XamlNavigationTarget? ClassifyMarkup(Context c, MarkupExtensionSyntax markup, int position)
    {
        switch (markup.LocalName)
        {
            case "Binding":
            case "CompiledBinding":
            case "ReflectionBinding":
                return ClassifyBinding(c, markup, position);

            case "StaticResource":
            case "DynamicResource":
                {
                    var key = markup.Positional(0) ?? markup.Named("ResourceKey");
                    if (key is null || !Contains(key, position) || key.Nested != null)
                    {
                        return null;
                    }

                    return new XamlNavigationTarget(XamlNavigationKind.ResourceKey, key.ValueStart, key.Value.Length, key.Value)
                    {
                        MemberName = key.Value,
                        XClassName = c.XClass,
                    };
                }

            case "Type":
                {
                    var type = markup.Positional(0) ?? markup.Named("TypeName");
                    return type is not null && Contains(type, position) ? TypeTarget(c, type.Value, type.ValueStart) : null;
                }

            case "Static":
                {
                    var member = markup.Positional(0) ?? markup.Named("Member");
                    if (member is null || !Contains(member, position))
                    {
                        return null;
                    }

                    var lastDot = member.Value.LastIndexOf('.');
                    if (lastDot <= 0)
                    {
                        return null;
                    }

                    var typePart = member.Value.Substring(0, lastDot);
                    if (position <= member.ValueStart + lastDot)
                    {
                        return TypeTarget(c, typePart, member.ValueStart);
                    }

                    var name = member.Value.Substring(lastDot + 1);
                    return new XamlNavigationTarget(XamlNavigationKind.StaticMember, member.ValueStart + lastDot + 1, name.Length, name)
                    {
                        XamlTypeName = typePart,
                        TypeName = c.Resolver.Resolve(typePart),
                        MemberName = name,
                        XClassName = c.XClass,
                    };
                }

            default:
                return null;
        }
    }

    private static XamlNavigationTarget? ClassifyBinding(Context c, MarkupExtensionSyntax markup, int position)
    {
        // An explicit DataType= wins over x:DataType in scope.
        var explicitDataType = markup.Named("DataType");
        if (explicitDataType is not null && Contains(explicitDataType, position))
        {
            return TypeTarget(c, explicitDataType.Value, explicitDataType.ValueStart);
        }

        var path = markup.Positional(0) ?? markup.Named("Path");
        if (path is null || path.Nested != null || !Contains(path, position))
        {
            return null;
        }

        var segments = SplitPath(path.Value, path.ValueStart);
        if (segments.Count == 0)
        {
            return null;
        }

        var index = segments.FindIndex(s => position >= s.Start && position <= s.Start + s.Name.Length);
        if (index < 0)
        {
            return null;
        }

        // Work out the type the path starts from.
        string? rootXamlType;
        var first = segments[0];
        var skip = 0;

        if (first.Name.StartsWith("#", StringComparison.Ordinal))
        {
            var elementName = first.Name.Substring(1);
            if (index == 0)
            {
                return new XamlNavigationTarget(XamlNavigationKind.XName, first.Start + 1, elementName.Length, elementName)
                {
                    MemberName = elementName,
                    XClassName = c.XClass,
                };
            }

            rootXamlType = c.Elements.FirstOrDefault(e => e.ElementName == elementName)?.Name;
            skip = 1;
        }
        else if (first.Name.StartsWith("$parent", StringComparison.Ordinal))
        {
            var open = first.Name.IndexOf('[');
            var close = first.Name.IndexOf(']');
            rootXamlType = open > 0 && close > open ? first.Name.Substring(open + 1, close - open - 1).Split(',')[0].Trim() : null;
            if (index == 0)
            {
                return rootXamlType is null ? null : TypeTarget(c, rootXamlType, first.Start + open + 1);
            }

            skip = 1;
        }
        else if (first.Name.StartsWith("$self", StringComparison.Ordinal))
        {
            rootXamlType = c.Element.Name;
            skip = 1;
            if (index == 0)
            {
                return null;
            }
        }
        else
        {
            rootXamlType = explicitDataType?.Value ?? FindDataTypeInScope(c.Element);
        }

        var names = segments.Skip(skip).Select(s => s.Name).ToList();
        var segment = segments[index];

        return new XamlNavigationTarget(XamlNavigationKind.BindingPath, segment.Start, segment.Name.Length, segment.Name)
        {
            MemberName = segment.Name,
            PathSegments = names,
            PathIndex = index - skip,
            XamlDataTypeName = rootXamlType,
            DataTypeName = c.Resolver.Resolve(rootXamlType),
            XClassName = c.XClass,
        };
    }

    /// <summary>
    /// Finds the data type bindings on <paramref name="element"/> compile against: the nearest
    /// <c>x:DataType</c> on the element or an ancestor (a <c>DataTemplate</c>'s <c>DataType</c> counts).
    /// </summary>
    public static string? FindDataTypeInScope(XamlElement element)
    {
        foreach (var e in element.SelfAndAncestors())
        {
            var dataType = e.GetXamlDirective("DataType")?.Value;
            if (string.IsNullOrWhiteSpace(dataType) && e.LocalName.EndsWith("DataTemplate", StringComparison.Ordinal))
            {
                dataType = e.GetAttribute("DataType")?.Value;
            }

            if (!string.IsNullOrWhiteSpace(dataType))
            {
                var trimmed = dataType!.Trim();

                // x:DataType="{x:Type vm:Foo}"
                if (trimmed.StartsWith("{", StringComparison.Ordinal))
                {
                    var markup = MarkupExtensionSyntax.Parse(trimmed, 0, trimmed.Length);
                    return markup?.LocalName == "Type" ? (markup.Positional(0) ?? markup.Named("TypeName"))?.Value : null;
                }

                return trimmed;
            }
        }

        return null;
    }

    private static XamlNavigationTarget? TypeTarget(Context c, string xamlTypeName, int start)
    {
        var typeName = xamlTypeName.Trim();
        if (typeName.Length == 0)
        {
            return null;
        }

        return new XamlNavigationTarget(XamlNavigationKind.Type, start, typeName.Length, typeName)
        {
            XamlTypeName = typeName,
            TypeName = c.Resolver.Resolve(typeName),
            XClassName = c.XClass,
        };
    }

    private readonly record struct PathSegment(string Name, int Start);

    /// <summary>
    /// Splits a binding path into member segments, dropping negation (<c>!</c>), casts
    /// (<c>(vm:Foo)</c>) and indexers (<c>[0]</c>).
    /// </summary>
    private static List<PathSegment> SplitPath(string path, int offset)
    {
        var result = new List<PathSegment>();
        var i = 0;

        while (i < path.Length)
        {
            while (i < path.Length && (path[i] == '!' || char.IsWhiteSpace(path[i])))
            {
                i++;
            }

            // Skip a cast or attached-property segment in parentheses.
            if (i < path.Length && path[i] == '(')
            {
                var close = path.IndexOf(')', i);
                i = close < 0 ? path.Length : close + 1;
                continue;
            }

            var start = i;
            var bracketDepth = 0;
            while (i < path.Length && (bracketDepth > 0 || path[i] != '.'))
            {
                if (path[i] == '[')
                {
                    bracketDepth++;
                }
                else if (path[i] == ']')
                {
                    bracketDepth--;
                }

                i++;
            }

            var raw = path.Substring(start, i - start);

            // Keep $parent[...] whole; strip indexers from member names.
            var name = raw.StartsWith("$", StringComparison.Ordinal) ? raw : StripIndexer(raw);
            if (name.Length > 0)
            {
                result.Add(new PathSegment(name.Trim(), offset + start));
            }

            i++; // past '.'
        }

        return result;
    }

    private static string StripIndexer(string segment)
    {
        var bracket = segment.IndexOf('[');
        return bracket >= 0 ? segment.Substring(0, bracket) : segment;
    }

    private static bool Contains(MarkupArgument argument, int position) =>
        position >= argument.ValueStart && position <= argument.ValueEnd;

    private static bool IsIdentifier(string value) =>
        value.Length > 0 && (char.IsLetter(value[0]) || value[0] == '_') && value.All(ch => char.IsLetterOrDigit(ch) || ch == '_');

    private static bool IsXamlDirective(Context c, string attributeName, string localName)
    {
        var colon = attributeName.IndexOf(':');
        if (colon <= 0 || attributeName.Substring(colon + 1) != localName)
        {
            return false;
        }

        var prefix = attributeName.Substring(0, colon);
        return c.Resolver.Aliases.TryGetValue(prefix, out var ns)
            ? ns == Utils.Xaml2006Namespace
            : prefix == "x";
    }

    private static bool IsDirectiveOrDesignTime(Context c, string attributeName)
    {
        var colon = attributeName.IndexOf(':');
        if (colon <= 0 || attributeName.IndexOf('.', colon) > 0)
        {
            return false;
        }

        var prefix = attributeName.Substring(0, colon);
        if (!c.Resolver.Aliases.TryGetValue(prefix, out var ns))
        {
            return prefix == "x" || prefix == "d" || prefix == "mc";
        }

        return ns == Utils.Xaml2006Namespace
            || ns == "http://schemas.microsoft.com/expression/blend/2008"
            || ns == "http://schemas.openxmlformats.org/markup-compatibility/2006";
    }

    private sealed class Context
    {
        public Context(string text, IReadOnlyList<XamlElement> elements, XamlElement element, XamlTypeNameResolver resolver, CompletionEngine.MetadataHelper? helper, string? xClass)
        {
            Text = text;
            Elements = elements;
            Element = element;
            Resolver = resolver;
            Helper = helper?.Metadata is null ? null : helper;
            XClass = string.IsNullOrWhiteSpace(xClass) ? null : xClass!.Trim();
        }

        public string Text { get; }

        public IReadOnlyList<XamlElement> Elements { get; }

        public XamlElement Element { get; }

        public XamlTypeNameResolver Resolver { get; }

        public CompletionEngine.MetadataHelper? Helper { get; }

        public string? XClass { get; }

        /// <summary>The type that declares <paramref name="member"/>, which may be a base of the element's type.</summary>
        public string? DeclaringTypeOf(string xamlTypeName, string member)
        {
            var type = Helper?.LookupType(xamlTypeName);
            var property = type?.Properties.FirstOrDefault(p => p.Name == member);
            if (property?.DeclaringType?.FullName is { Length: > 0 } declaring)
            {
                return declaring;
            }

            var @event = type?.Events.FirstOrDefault(e => e.Name == member);
            return @event?.DeclaringType?.FullName is { Length: > 0 } eventOwner ? eventOwner : null;
        }

        public bool TryGetEvent(string attributeName, out string? ownerType, out string eventName, out bool isAttached)
        {
            ownerType = null;
            eventName = attributeName;
            isAttached = false;

            if (Helper is null || Element.IsPropertyElement)
            {
                return false;
            }

            var colon = attributeName.IndexOf(':');
            var dot = attributeName.IndexOf('.', colon + 1);

            if (dot > 0)
            {
                var owner = Helper.LookupType(attributeName.Substring(0, dot));
                eventName = attributeName.Substring(dot + 1);
                var name = eventName;
                var attached = owner?.Events.FirstOrDefault(e => e.IsAttached && e.Name == name);
                if (attached is null)
                {
                    return false;
                }

                ownerType = owner!.FullName;
                isAttached = true;
                return true;
            }

            var type = Helper.LookupType(Element.Name);
            var match = type?.Events.FirstOrDefault(e => !e.IsAttached && e.Name == attributeName);
            if (match is null)
            {
                return false;
            }

            ownerType = match.DeclaringType?.FullName is { Length: > 0 } declaring ? declaring : type!.FullName;
            return true;
        }
    }
}
