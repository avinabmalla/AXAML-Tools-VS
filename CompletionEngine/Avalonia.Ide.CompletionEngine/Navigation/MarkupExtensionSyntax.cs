#nullable enable
using System.Collections.Generic;

namespace Avalonia.Ide.CompletionEngine.Navigation;

/// <summary>
/// A markup extension such as <c>{Binding Foo, Converter={StaticResource C}}</c>, with offsets
/// into the document.
/// </summary>
public sealed class MarkupExtensionSyntax
{
    internal MarkupExtensionSyntax(int start) => Start = start;

    public int Start { get; }

    public int End { get; internal set; }

    public string Name { get; internal set; } = "";

    public int NameStart { get; internal set; }

    public int NameEnd => NameStart + Name.Length;

    public List<MarkupArgument> Arguments { get; } = new();

    /// <summary>The name without prefix or <c>Extension</c> suffix, e.g. <c>Static</c> for <c>x:Static</c>.</summary>
    public string LocalName
    {
        get
        {
            var name = Name.Substring(Name.IndexOf(':') + 1);
            return name.EndsWith("Extension", System.StringComparison.Ordinal) ? name.Substring(0, name.Length - "Extension".Length) : name;
        }
    }

    public MarkupArgument? Positional(int index)
    {
        var i = 0;
        foreach (var argument in Arguments)
        {
            if (argument.Key is null)
            {
                if (i++ == index)
                {
                    return argument;
                }
            }
        }

        return null;
    }

    public MarkupArgument? Named(string key)
    {
        foreach (var argument in Arguments)
        {
            if (argument.Key == key)
            {
                return argument;
            }
        }

        return null;
    }

    /// <summary>
    /// Parses the markup extension that starts at <paramref name="start"/> (a <c>{</c>), not
    /// reading past <paramref name="limit"/>.
    /// </summary>
    public static MarkupExtensionSyntax? Parse(string text, int start, int limit)
    {
        if (start >= limit || text[start] != '{')
        {
            return null;
        }

        var result = new MarkupExtensionSyntax(start);
        var i = SkipWhitespace(text, start + 1, limit);
        result.NameStart = i;
        while (i < limit && !char.IsWhiteSpace(text[i]) && text[i] != '}' && text[i] != ',')
        {
            i++;
        }

        result.Name = text.Substring(result.NameStart, i - result.NameStart);

        while (i < limit)
        {
            i = SkipWhitespace(text, i, limit);
            if (i >= limit)
            {
                break;
            }

            if (text[i] == '}')
            {
                result.End = i + 1;
                return result;
            }

            if (text[i] == ',')
            {
                i++;
                continue;
            }

            // Read one argument up to a top-level ',' or '}'.
            var argStart = i;
            var depth = 0;
            var equals = -1;
            char quote = '\0';
            while (i < limit)
            {
                var c = text[i];
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }
                }
                else if (c == '\'' )
                {
                    quote = c;
                }
                else if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    if (depth == 0)
                    {
                        break;
                    }

                    depth--;
                }
                else if (c == ',' && depth == 0)
                {
                    break;
                }
                else if (c == '=' && depth == 0 && equals < 0)
                {
                    equals = i;
                }

                i++;
            }

            result.Arguments.Add(MarkupArgument.Create(text, argStart, i, equals));
        }

        result.End = limit;
        return result;
    }

    /// <summary>
    /// Finds the innermost markup extension (this one or a nested one) whose text contains
    /// <paramref name="position"/>.
    /// </summary>
    public MarkupExtensionSyntax FindInnermost(int position)
    {
        foreach (var argument in Arguments)
        {
            if (argument.Nested is { } nested && position >= nested.Start && position <= nested.End)
            {
                return nested.FindInnermost(position);
            }
        }

        return this;
    }

    private static int SkipWhitespace(string text, int i, int limit)
    {
        while (i < limit && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        return i;
    }
}

/// <summary>A positional (<c>Foo</c>) or named (<c>Path=Foo</c>) markup extension argument.</summary>
public sealed class MarkupArgument
{
    public string? Key { get; private set; }

    public int KeyStart { get; private set; }

    public string Value { get; private set; } = "";

    public int ValueStart { get; private set; }

    public int ValueEnd => ValueStart + Value.Length;

    public MarkupExtensionSyntax? Nested { get; private set; }

    internal static MarkupArgument Create(string text, int start, int end, int equals)
    {
        var argument = new MarkupArgument();
        var valueStart = start;

        if (equals >= 0)
        {
            var (keyStart, keyEnd) = Trim(text, start, equals);
            argument.Key = text.Substring(keyStart, keyEnd - keyStart);
            argument.KeyStart = keyStart;
            valueStart = equals + 1;
        }

        var (vs, ve) = Trim(text, valueStart, end);
        argument.ValueStart = vs;
        argument.Value = text.Substring(vs, ve - vs);

        if (argument.Value.StartsWith("{", System.StringComparison.Ordinal))
        {
            argument.Nested = MarkupExtensionSyntax.Parse(text, vs, ve);
        }

        return argument;
    }

    private static (int, int) Trim(string text, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        while (end > start && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        return (start, end);
    }
}
