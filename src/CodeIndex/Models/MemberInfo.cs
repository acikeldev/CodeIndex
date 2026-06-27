namespace CodeIndex.Models;

/// <summary>
/// Represents a single member (method, property, field, constructor, event)
/// within a type declaration.
/// </summary>
public sealed class MemberInfo
{
    /// <summary>Simple name of the member (e.g. "GetUser").</summary>
    public required string Name { get; init; }

    /// <summary>Kind of the member.</summary>
    public required SymbolKind Kind { get; init; }

    /// <summary>
    /// Full signature as it appears in source, including modifiers, return type,
    /// and parameter list (e.g. "public async Task&lt;User&gt; GetUser(int id)").
    /// </summary>
    public required string Signature { get; init; }

    /// <summary>Return type name, or null for constructors and void methods.</summary>
    public string? ReturnType { get; init; }

    /// <summary>1-based line number where this member is declared.</summary>
    public required int StartLine { get; init; }

    /// <summary>1-based line number of the closing brace or expression body.</summary>
    public required int EndLine { get; init; }
}
