using CodeIndex.Models;

namespace CodeIndex.Abstractions;

/// <summary>
/// The queryable + rebuildable in-memory code index behind every MCP tool. Query members are lock-free reads
/// over an immutable snapshot; the mutation members (Build / Rebuild / Refresh*) publish new snapshots atomically.
/// </summary>
public interface ICodeIndexStore
{
    // ── diagnostics / counts ────────────────────────────────────────────────
    string? RepoRoot { get; }
    string CacheDirectory { get; }
    BuildInfo LastBuild { get; }
    int ProjectCount { get; }
    int TsProjectCount { get; }
    int SourceFileCount { get; }
    int TypeCount { get; }
    int MemberCount { get; }
    IReadOnlyList<SourceFileIndex> AllSourceFiles { get; }

    // ── behaviour flags (resolved from config) ────────────────────────────────
    bool SpeculateEnabled { get; }
    int SpeculateTokenBudget { get; }

    // ── query ────────────────────────────────────────────────────────────────
    List<SymbolSearchResult> SearchSymbol(string query, string? kindFilter, string? projectFilter);
    SourceFileIndex? GetFileOutline(string fileQuery);
    string? ResolveSourceFilePath(string fileQuery);
    bool IsIndexedPath(string fullPath);
    List<ProjectIndex> ListProjects();
    ProjectIndex? GetProject(string name);
    List<(TypeInfo Type, SourceFileIndex File)> FindTypes(string name);
    List<(TypeInfo Type, SourceFileIndex File)> GetDerivedTypes(string name);
    BareNameResolution ResolveBareName(string fileQuery, string identifier);
    string GetRepoMap(IReadOnlyList<string> focus, int tokenBudget, string? project);
    string SearchStructural(string pattern, string? project, int max, int perFileCap);
    string CheckDanglingReferences(string fileQuery);
    string GetCallHierarchy(string method, string direction, string? project, int max, int perFileCap, string language);
    string GetCallTrace(string method, string direction, string? project, int maxDepth, int maxNodes, bool includeBodies);
    ProjectDependencyInfo? GetProjectDependencyInfo(string name);
    IReadOnlyDictionary<string, string> ProjectDirsByName();
    IEnumerable<string> FileNames();
    IEnumerable<string> ProjectNames();
    IEnumerable<string> TypeNames();
    IEnumerable<string> SymbolNames(string? projectFilter);

    // ── mutation (composition root + watcher) ─────────────────────────────────
    void Build(string repoRoot);
    bool LoadCachedSnapshot(string repoRoot);
    void Rebuild(string repoRoot, bool fullRebuild, CancellationToken cancellationToken);
    Task RefreshAsync(string repoRoot, bool fullRebuild, CancellationToken cancellationToken);
    Task RefreshTypeScriptAsync(string repoRoot, bool fullRebuild, CancellationToken cancellationToken);
}
