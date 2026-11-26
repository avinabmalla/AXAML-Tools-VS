#nullable enable
using System;
using System.Collections.Generic;

namespace Avalonia.Ide.CompletionEngine.Navigation;

/// <summary>A <c>{Binding}</c> that compiled bindings can't type-check because no <c>x:DataType</c> is in scope.</summary>
public sealed class BindingWithoutDataType
{
    internal BindingWithoutDataType(int start, int length, XamlElement element, XamlElement dataTypeOwner)
    {
        Start = start;
        Length = length;
        Element = element;
        SuggestedDataTypeOwner = dataTypeOwner;
    }

    /// <summary>Span of the whole <c>{Binding ...}</c> markup extension.</summary>
    public int Start { get; }

    public int Length { get; }

    /// <summary>The element the binding is set on.</summary>
    public XamlElement Element { get; }

    /// <summary>
    /// Where adding <c>x:DataType</c> fixes it: the nearest enclosing data template, otherwise
    /// the root element.
    /// </summary>
    public XamlElement SuggestedDataTypeOwner { get; }
}

/// <summary>
/// Finds bindings that would fail to compile when compiled bindings are on by default
/// (Avalonia 12, or <c>AvaloniaUseCompiledBindingsByDefault=true</c>).
/// </summary>
public static class BindingDataTypeAnalyzer
{
    public static IReadOnlyList<BindingWithoutDataType> Analyze(string text)
    {
        var result = new List<BindingWithoutDataType>();
        var elements = XamlScanner.Scan(text);
        var root = XamlScanner.GetRoot(elements);
        if (root is null)
        {
            return result;
        }

        foreach (var element in elements)
        {
            foreach (var attribute in element.Attributes)
            {
                if (!attribute.HasValue || attribute.Value.IndexOf('{') < 0 ||
                    attribute.Name.StartsWith("xmlns", StringComparison.Ordinal) ||
                    attribute.Name.StartsWith("d:", StringComparison.Ordinal))
                {
                    continue;
                }

                var valueOffset = attribute.ValueStart + (attribute.Value.Length - attribute.Value.TrimStart().Length);
                var markup = MarkupExtensionSyntax.Parse(text, valueOffset, attribute.ValueEnd);
                if (markup is null)
                {
                    continue;
                }

                foreach (var binding in EnumerateBindings(markup))
                {
                    if (NeedsDataType(binding, element))
                    {
                        result.Add(new BindingWithoutDataType(
                            binding.Start,
                            binding.End - binding.Start,
                            element,
                            FindDataTypeOwner(element) ?? root));
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Whether compiled bindings are switched off for <paramref name="element"/> by
    /// <c>x:CompileBindings="False"</c> on it or the nearest ancestor that sets it.
    /// </summary>
    public static bool CompileBindingsDisabled(XamlElement element)
    {
        foreach (var e in element.SelfAndAncestors())
        {
            var value = e.GetXamlDirective("CompileBindings")?.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return string.Equals(value!.Trim(), "False", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    private static IEnumerable<MarkupExtensionSyntax> EnumerateBindings(MarkupExtensionSyntax markup)
    {
        if (markup.LocalName is "Binding" or "CompiledBinding")
        {
            yield return markup;
        }

        foreach (var argument in markup.Arguments)
        {
            if (argument.Nested is { } nested)
            {
                foreach (var inner in EnumerateBindings(nested))
                {
                    yield return inner;
                }
            }
        }
    }

    private static bool NeedsDataType(MarkupExtensionSyntax binding, XamlElement element)
    {
        if (binding.LocalName == "Binding" && CompileBindingsDisabled(element))
        {
            return false;
        }

        // Bindings with their own source don't use the DataContext type.
        if (binding.Named("DataType") != null || binding.Named("Source") != null ||
            binding.Named("ElementName") != null || binding.Named("RelativeSource") != null)
        {
            return false;
        }

        var path = (binding.Positional(0) ?? binding.Named("Path"))?.Value?.TrimStart('!', ' ');
        if (path is not null && (path.StartsWith("#", StringComparison.Ordinal) || path.StartsWith("$", StringComparison.Ordinal)))
        {
            return false;
        }

        return XamlNavigationClassifier.FindDataTypeInScope(element) is null;
    }

    private static XamlElement? FindDataTypeOwner(XamlElement element)
    {
        foreach (var e in element.SelfAndAncestors())
        {
            if (e.LocalName.EndsWith("DataTemplate", StringComparison.Ordinal))
            {
                return e;
            }
        }

        return null;
    }
}
