namespace CodeIndex.Internal;

/// <summary>
/// Valid values for the <c>kind=</c> filter parameters, and validation used to reject an unknown kind with a
/// helpful error instead of silently matching everything (which made a typo'd filter look like "no results of
/// that kind exist").
/// </summary>
internal static class KindFilter
{
    // Accepted by search_symbol (types + members).
    public static readonly string[] SymbolKinds =
        ["class", "struct", "record", "interface", "enum", "method", "property", "field", "constructor", "event"];

    // Accepted by get_type_members (members only).
    public static readonly string[] MemberKinds =
        ["method", "property", "field", "constructor", "event"];

    public static bool IsValidSymbolKind(string kind) => IsIn(kind, SymbolKinds);

    public static bool IsValidMemberKind(string kind) => IsIn(kind, MemberKinds);

    public static string ValidSymbolKindsList() => string.Join(", ", SymbolKinds);

    public static string ValidMemberKindsList() => string.Join(", ", MemberKinds);

    private static bool IsIn(string kind, string[] set)
    {
        string k = kind.ToLowerInvariant();
        if (k is "ctor")
        {
            return true; // alias for constructor
        }

        return Array.IndexOf(set, k) >= 0;
    }
}
