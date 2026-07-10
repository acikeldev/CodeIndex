using CodeIndex.Abstractions;
using CodeIndex.Models;

namespace CodeIndex.Indexing;

/// <summary>
/// Null-object <see cref="ICodeIndexStore"/> used during engine bring-up: every query returns an empty /
/// "not found" result and every mutation is a no-op, so the MCP host boots and answers safely before the real
/// snapshot-swap store is wired in. Replaced by <c>CodeIndexStore</c> once the engine subsystems are ported.
/// </summary>
public sealed class EmptyCodeIndexStore : ICodeIndexStore
{
    public string? RepoRoot => null;
    public string CacheDirectory => string.Empty;
    public BuildInfo LastBuild { get; } = new(DateTime.MinValue, "none", 0, 0, 0);
    public int ProjectCount => 0;
    public int TsProjectCount => 0;
    public int SourceFileCount => 0;
    public int TypeCount => 0;
    public int MemberCount => 0;
    public IReadOnlyList<SourceFileIndex> AllSourceFiles => [];

    public List<SymbolSearchResult> SearchSymbol(string query, string? kindFilter, string? projectFilter) => [];
    public SourceFileIndex? GetFileOutline(string fileQuery) => null;
    public string? ResolveSourceFilePath(string fileQuery) => null;
    public bool IsIndexedPath(string fullPath) => false;
    public List<ProjectIndex> ListProjects() => [];
    public ProjectIndex? GetProject(string name) => null;
    public List<(TypeInfo Type, SourceFileIndex File)> FindTypes(string name) => [];
    public List<(TypeInfo Type, SourceFileIndex File)> GetDerivedTypes(string name) => [];
    public BareNameResolution ResolveBareName(string fileQuery, string identifier) => new(false, null, null, [], []);
    public string GetRepoMap(IReadOnlyList<string> focus, int tokenBudget, string? project) => string.Empty;
    public string SearchStructural(string pattern, string? project, int max, int perFileCap) => string.Empty;
    public string CheckDanglingReferences(string fileQuery) => string.Empty;
    public string GetCallHierarchy(string method, string direction, string? project, int max, int perFileCap, string language) => string.Empty;
    public ProjectDependencyInfo? GetProjectDependencyInfo(string name) => null;
    public IReadOnlyDictionary<string, string> ProjectDirsByName() => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IEnumerable<string> FileNames() => [];
    public IEnumerable<string> ProjectNames() => [];
    public IEnumerable<string> TypeNames() => [];
    public IEnumerable<string> SymbolNames(string? projectFilter) => [];

    public void Build(string repoRoot) { }
    public bool LoadCachedSnapshot(string repoRoot) => false;
    public void Rebuild(string repoRoot, bool fullRebuild, CancellationToken cancellationToken) { }
    public Task RefreshAsync(string repoRoot, bool fullRebuild, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task RefreshTypeScriptAsync(string repoRoot, bool fullRebuild, CancellationToken cancellationToken) => Task.CompletedTask;
}
