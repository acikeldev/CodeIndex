using MessagePack;

namespace CodeIndex.Models;

/// <summary>
/// A TypeScript/SCSS "project" boundary: a directory holding a <c>tsconfig.json</c> (preferred) or a
/// <c>package.json</c>, or a synthetic <c>scss:&lt;dir&gt;</c> for orphan <c>.scss</c> with no ancestor
/// tsconfig. Discoverable via repo_info, but carries NO dependency edges — it never reaches
/// <see cref="Index.ProjectDependencyGraph"/> (which stays strictly C#-only), so the C# dependency graph
/// is provably unchanged. Serialized in the independent TS cache segment (index.ts.v{N}.cache).
/// </summary>
[MessagePackObject]
public sealed class TsProjectInfo
{
    [Key(0)] public required string Name { get; init; }
    [Key(1)] public required string ProjectDirPath { get; init; }
}
