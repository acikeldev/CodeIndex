using MessagePack;

namespace CodeIndex.Models;

/// <summary>
/// The on-disk C#-segment index payload (MessagePack). The schema version travels with the payload so a
/// build that cannot read an older shape discards it and rebuilds. Pure serialized data shared between the
/// store and the cache implementation.
/// </summary>
[MessagePackObject]
public sealed class CacheData
{
    [Key(0)] public required List<ProjectIndex> Projects { get; init; }
    [Key(1)] public required List<SourceFileIndex> SourceFiles { get; init; }
    [Key(2)] public required Dictionary<string, long> FileTimestamps { get; init; }
    [Key(3)] public int SchemaVersion { get; init; }
}
