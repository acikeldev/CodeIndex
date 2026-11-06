using MessagePack;

namespace CodeIndex.Models;

/// <summary>
/// An attribute in a XAML file whose value names a code-behind member — typically an event wiring
/// (<c>&lt;Button Click="Save_Click"/&gt;</c>, <c>&lt;EventSetter Handler="Row_Click"/&gt;</c>). Captured for every
/// identifier-valued attribute; whether it really is a handler is decided at query time by checking that the
/// value names a method on the file's <c>x:Class</c> type (an enum value like <c>Orientation="Horizontal"</c> never
/// matches one). Lets call_hierarchy / trace_calls report the XAML as the caller of a handler with no C# caller.
/// </summary>
[MessagePackObject]
public sealed class XamlHook
{
    /// <summary>Element local name (e.g. "Button").</summary>
    [Key(0)] public required string Element { get; init; }

    /// <summary>Attribute name (e.g. "Click", "Handler", "Mouse.MouseDown").</summary>
    [Key(1)] public required string Attribute { get; init; }

    /// <summary>The attribute value — the code-behind member name.</summary>
    [Key(2)] public required string Member { get; init; }

    /// <summary>1-based line of the element.</summary>
    [Key(3)] public required int Line { get; init; }
}
