using CodeIndex.Abstractions;
using CodeIndex.Internal;
using CodeIndex.Models;

namespace CodeIndex.Indexing;

/// <summary>
/// Immutable, published-once query state for the index. Readers do one Volatile.Read of the store's current
/// snapshot and then operate on it LOCK-FREE; a published snapshot is never mutated, so a reader that captured a
/// reference keeps enumerating a stable, consistent graph even while a rebuild swaps in a fresh snapshot.
/// </summary>
public sealed class IndexSnapshot
{
    public IReadOnlyDictionary<string, ProjectIndex> Projects { get; }
    public IReadOnlyList<SourceFileIndex> AllSourceFiles { get; }
    public IReadOnlyDictionary<string, List<SourceFileIndex>> SourceFilesByName { get; }
    public IReadOnlyDictionary<string, SourceFileIndex> SourceFilesByPath { get; }
    public IReadOnlyDictionary<string, long> FileTimestamps { get; }
    public int TypeCount { get; }
    public int MemberCount { get; }
    public int ProjectCount => Projects.Count;
    public int SourceFileCount => AllSourceFiles.Count;

    // TypeScript/SCSS project boundaries merged into this snapshot for discovery (repo_info). Display-only:
    // these NEVER reach ProjectDependencyGraph.Build (which reads Projects, staying strictly C#-only). Not
    // serialized — an IndexSnapshot is never cached — so zero cache impact.
    public IReadOnlyList<TsProjectInfo> TsProjects { get; }

    // Derived, lazily-built project reference graph. ExecutionAndPublication => one instance across racing lock-free
    // readers. Built from Projects (never serialized), so it imposes no cache-schema change. A merged (C#+TS)
    // snapshot INHERITS the C# segment's Lazy so a TS-only republish never re-materializes the C# graph.
    private readonly Lazy<ProjectDependencyGraph> _dependencyGraph;
    internal ProjectDependencyGraph DependencyGraph => _dependencyGraph.Value;
    internal Lazy<ProjectDependencyGraph> DependencyGraphLazy => _dependencyGraph;

    // Derived, lazily-built symbol-mention graph for repo_map (PageRank orientation). Like the dependency graph it
    // is built from the snapshot (never serialized => no cache-schema change) and materializes only on first
    // repo_map call; a new snapshot gets a fresh unbuilt Lazy, so it rebuilds only after the files actually change.
    private readonly Lazy<RepoMap> _repoMap;
    internal RepoMap MentionGraph => _repoMap.Value;

    internal IndexSnapshot(
        Dictionary<string, ProjectIndex> projects,
        List<SourceFileIndex> allSourceFiles,
        Dictionary<string, List<SourceFileIndex>> sourceFilesByName,
        Dictionary<string, SourceFileIndex> sourceFilesByPath,
        IReadOnlyDictionary<string, long> fileTimestamps,
        int typeCount,
        int memberCount,
        IFileSystem fileSystem,
        IReadOnlyList<TsProjectInfo>? tsProjects = null,
        Lazy<ProjectDependencyGraph>? inheritedGraph = null)
    {
        Projects = projects;
        AllSourceFiles = allSourceFiles;
        SourceFilesByName = sourceFilesByName;
        SourceFilesByPath = sourceFilesByPath;
        FileTimestamps = fileTimestamps;
        TypeCount = typeCount;
        MemberCount = memberCount;
        TsProjects = tsProjects ?? [];

        // The lazy graphs read source/.csproj files on demand, so they capture the injected file system.
        _dependencyGraph = inheritedGraph ?? new Lazy<ProjectDependencyGraph>(
            () => ProjectDependencyGraph.Build(Projects, fileSystem), LazyThreadSafetyMode.ExecutionAndPublication);
        _repoMap = new Lazy<RepoMap>(
            () => RepoMap.Build(AllSourceFiles, fileSystem), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The pre-index state: every tool works against it and simply returns "nothing found" until the
    /// first real snapshot is published (used by non-blocking startup and before any build). Its empty graphs
    /// never read a file, so the throwaway file system it carries is never touched.</summary>
    public static readonly IndexSnapshot Empty = new(
        new Dictionary<string, ProjectIndex>(StringComparer.OrdinalIgnoreCase),
        [],
        new Dictionary<string, List<SourceFileIndex>>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, SourceFileIndex>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase),
        typeCount: 0,
        memberCount: 0,
        new FileSystem());
}
