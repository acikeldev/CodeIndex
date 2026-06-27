namespace CodeIndex.Models;

/// <summary>
/// Represents a type declaration (class, interface, enum, struct, record)
/// parsed from a C# source file.
/// </summary>
public sealed class TypeInfo
{
    /// <summary>Simple name of the type (e.g. "UserService").</summary>
    public required string Name { get; init; }

    /// <summary>Fully qualified namespace (e.g. "MyApp.Services").</summary>
    public required string Namespace { get; init; }

    /// <summary>Kind of the type.</summary>
    public required SymbolKind Kind { get; init; }

    /// <summary>
    /// Base type and implemented interface names as they appear in source
    /// (e.g. ["BaseService", "IUserService"]).
    /// </summary>
    public IReadOnlyList<string> BaseTypes { get; init; } = [];

    /// <summary>All members declared directly on this type.</summary>
    public IReadOnlyList<MemberInfo> Members { get; init; } = [];

    /// <summary>1-based line number of the type declaration keyword.</summary>
    public required int StartLine { get; init; }

    /// <summary>1-based line number of the closing brace.</summary>
    public required int EndLine { get; init; }
}
