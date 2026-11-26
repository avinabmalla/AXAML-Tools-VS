#nullable enable
using System;
using System.Collections.Generic;

namespace Avalonia.Ide.CompletionEngine.Navigation;

/// <summary>
/// Maps a XAML type reference such as <c>vm:MainViewModel</c> or <c>Button</c> to a CLR full name.
/// </summary>
public sealed class XamlTypeNameResolver
{
    private readonly CompletionEngine.MetadataHelper? _helper;
    private readonly Dictionary<string, string> _aliases;

    public XamlTypeNameResolver(string text, CompletionEngine.MetadataHelper? helper)
    {
        _helper = helper?.Metadata is null ? null : helper;
        _aliases = helper?.Aliases ?? CompletionEngine.GetNamespaceAliases(text);
    }

    public IReadOnlyDictionary<string, string> Aliases => _aliases;

    /// <summary>
    /// Resolves <paramref name="xamlTypeName"/> to a full CLR type name, or null when it can't be
    /// resolved (for example a type in the Avalonia namespace before metadata has loaded).
    /// </summary>
    public string? Resolve(string? xamlTypeName)
    {
        if (string.IsNullOrWhiteSpace(xamlTypeName))
        {
            return null;
        }

        var name = xamlTypeName!.Trim();

        // Generic arguments and array suffixes aren't part of the type to navigate to.
        var bracket = name.IndexOfAny(new[] { '(', '[', '`' });
        if (bracket > 0)
        {
            name = name.Substring(0, bracket);
        }

        if (_helper?.LookupType(name) is { } type && !string.IsNullOrEmpty(type.FullName))
        {
            return type.FullName;
        }

        var colon = name.IndexOf(':');
        var prefix = colon < 0 ? "" : name.Substring(0, colon);
        var local = colon < 0 ? name : name.Substring(colon + 1);

        if (_aliases.TryGetValue(prefix, out var ns))
        {
            var clrNamespace = GetClrNamespace(ns);
            if (clrNamespace != null)
            {
                return clrNamespace.Length == 0 ? local : clrNamespace + "." + local;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the CLR namespace for an <c>xmlns</c> value such as <c>using:My.App</c> or
    /// <c>clr-namespace:My.App;assembly=My.App</c>, or null for URI namespaces.
    /// </summary>
    public static string? GetClrNamespace(string xmlns)
    {
        if (xmlns.StartsWith("using:", StringComparison.Ordinal))
        {
            return xmlns.Substring("using:".Length).Trim();
        }

        if (xmlns.StartsWith("clr-namespace:", StringComparison.Ordinal))
        {
            var value = xmlns.Substring("clr-namespace:".Length);
            var semicolon = value.IndexOf(';');
            return (semicolon < 0 ? value : value.Substring(0, semicolon)).Trim();
        }

        return null;
    }
}
