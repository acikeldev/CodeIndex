using MessagePack;

namespace CodeIndex.Models;

[MessagePackObject]
public sealed class ProjectIndex
{
    [Key(0)] public required string Name { get; init; }
    [Key(1)] public required string ProjectDirPath { get; init; }
    [Key(2)] public List<string> SourceFiles { get; init; } = [];
}
