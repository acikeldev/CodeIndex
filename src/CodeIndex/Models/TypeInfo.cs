using MessagePack;

namespace CodeIndex.Models;

[MessagePackObject]
public sealed class TypeInfo
{
    [Key(0)] public required string Name { get; init; }
    [Key(1)] public required SymbolKind Kind { get; init; }
    [Key(2)] public required string TypeKeyword { get; init; }
    // Individual base-type/interface names, each element one entry (generic args kept intact, e.g. "IRepository<TItem, int>").
    // Stored as a list (not a comma-joined string) so derived-type matching is exact instead of substring — a base
    // "IOrderRepository" must not match a query for "IOrder". Null/empty when the type has no base list.
    [Key(3)] public List<string>? BaseTypes { get; init; }
    [Key(4)] public required int StartLine { get; init; }
    [Key(5)] public required int LineCount { get; init; }
    [Key(6)] public bool IsNested { get; init; }
    [Key(7)] public string? EnumValues { get; init; }
    [Key(8)] public List<MemberInfo> Members { get; init; } = [];
    // The type's own namespace (may differ from the file's first namespace in multi-namespace files). Used by
    // resolve_bare_name and disambiguation so a bare name binds against the correct enclosing namespace.
    [Key(9)] public string? Namespace { get; init; }

    // Comma-joined base list for display (null when there are none). Not serialized — derived from BaseTypes.
    [IgnoreMember]
    public string? BaseTypesDisplay => BaseTypes is { Count: > 0 } ? string.Join(", ", BaseTypes) : null;
}
