#nullable enable
using System;
using System.Collections.Generic;

namespace Avalonia.Ide.CompletionEngine.Navigation;

public enum XamlNavigationKind
{
    /// <summary>A type: element tag, <c>x:DataType</c>, <c>TargetType</c>, <c>{x:Type}</c>, the type part of <c>{x:Static}</c>.</summary>
    Type,

    /// <summary>A property (or attached property/event) used as an attribute or property element.</summary>
    Property,

    /// <summary>The method named in an event attribute, e.g. <c>Click="SaveButton_Click"</c>.</summary>
    EventHandler,

    /// <summary>The value of <c>x:Class</c>.</summary>
    XClass,

    /// <summary>The value of <c>x:Name</c>/<c>Name</c>, or a <c>#name</c> reference in a binding.</summary>
    XName,

    /// <summary>A segment of a binding path, resolved against <c>x:DataType</c>.</summary>
    BindingPath,

    /// <summary>The member part of <c>{x:Static local:Type.Member}</c>.</summary>
    StaticMember,

    /// <summary>The key in <c>{StaticResource Key}</c> / <c>{DynamicResource Key}</c>.</summary>
    ResourceKey,

    /// <summary>A file reference such as a <c>StyleInclude</c> <c>Source</c>.</summary>
    Uri,
}

/// <summary>
/// What F12 should navigate to for a position in a XAML document.
/// </summary>
public sealed class XamlNavigationTarget
{
    public XamlNavigationTarget(XamlNavigationKind kind, int start, int length, string text)
    {
        Kind = kind;
        Start = start;
        Length = length;
        Text = text;
    }

    public XamlNavigationKind Kind { get; }

    /// <summary>Span of the identifier under the caret (used for Ctrl+Click underlining and messages).</summary>
    public int Start { get; }

    public int Length { get; }

    /// <summary>The text of that span.</summary>
    public string Text { get; }

    /// <summary>Full CLR name of the target type (Type, StaticMember), or of the member's owner (Property).</summary>
    public string? TypeName { get; init; }

    /// <summary>The type as written in XAML, e.g. <c>vm:MainViewModel</c>.</summary>
    public string? XamlTypeName { get; init; }

    /// <summary>The member name (Property, StaticMember, EventHandler method, XName, BindingPath segment).</summary>
    public string? MemberName { get; init; }

    /// <summary>True for attached properties/events (<c>Grid.Row</c>).</summary>
    public bool IsAttached { get; init; }

    /// <summary>Full CLR name of the root element's <c>x:Class</c>, if any.</summary>
    public string? XClassName { get; init; }

    /// <summary>EventHandler: the event's owner type and event name.</summary>
    public string? EventOwnerTypeName { get; init; }

    public string? EventName { get; init; }

    /// <summary>BindingPath: all segments, the index of the one under the caret, and the type the path starts from.</summary>
    public IReadOnlyList<string> PathSegments { get; init; } = Array.Empty<string>();

    public int PathIndex { get; init; }

    public string? DataTypeName { get; init; }

    /// <summary>BindingPath: the <c>x:DataType</c> as written (for messages when it can't be resolved).</summary>
    public string? XamlDataTypeName { get; init; }

    /// <summary>Uri: the value as written.</summary>
    public string? Uri { get; init; }

    public override string ToString() => $"{Kind} '{Text}' {TypeName}.{MemberName}";
}
