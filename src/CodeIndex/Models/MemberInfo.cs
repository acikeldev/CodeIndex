using MessagePack;

namespace CodeIndex.Models;

[MessagePackObject]
public sealed class MemberInfo
{
    [Key(0)] public required string Name { get; init; }
    [Key(1)] public required SymbolKind Kind { get; init; }
    [Key(2)] public required string ReturnType { get; init; }
    [Key(3)] public required string Signature { get; init; }
    [Key(4)] public required int StartLine { get; init; }
    [Key(5)] public required int LineCount { get; init; }
}
