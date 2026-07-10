using CodeIndex.Models;

namespace CodeIndex.Indexing;

/// <summary>
/// The TypeScript/SCSS half of the index, kept as a lightweight immutable value ALONGSIDE the C# segment
/// (never a second full <see cref="IndexSnapshot"/>). Composed with the live C# snapshot into the single
/// published snapshot by <c>CodeIndexStore.Compose</c>. Immutable after construction; the store's only writer
/// is <c>PublishTs</c>, under the shared rebuild gate. Holds its OWN timestamp baseline (case-insensitive, so a
/// re-scan with different path casing is a no-op, not churn) and its OWN project list — <see cref="Projects"/>
/// are display/discovery only and never reach the C#-only dependency graph.
/// </summary>
public sealed class TsSegment
{
    public static readonly TsSegment Empty = new(
        [],
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase),
        [],
        AliasMap.Empty);

    public IReadOnlyList<SourceFileIndex> Files { get; }
    public IReadOnlyDictionary<string, long> Timestamps { get; }
    public IReadOnlyList<TsProjectInfo> Projects { get; }
    public AliasMap Aliases { get; }

    public TsSegment(
        IReadOnlyList<SourceFileIndex> files,
        IReadOnlyDictionary<string, long> timestamps,
        IReadOnlyList<TsProjectInfo> projects,
        AliasMap aliases)
    {
        Files = files;
        Timestamps = timestamps;
        Projects = projects;
        Aliases = aliases;
    }
}
