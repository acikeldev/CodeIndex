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
    // Framework-invocation markers ([OperationContract]/[DataMember]/[Fact]/…). None (=0) for ordinary members;
    // a set bit means the member is invoked by a framework with no C# caller, so it is not dead. Cheap byte flag
    // rather than separate records — these markers number in the thousands across a real codebase.
    [Key(6)] public MemberRootKind RootKinds { get; init; } = MemberRootKind.None;
}
