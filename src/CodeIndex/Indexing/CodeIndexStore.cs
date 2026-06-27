using CodeIndex.Abstractions;
using CodeIndex.Models;
using CodeIndex.Parsing;

namespace CodeIndex.Indexing;

/// <summary>
/// In-memory code index: discovers projects via <see cref="SolutionScanner"/>,
/// parses source files via <see cref="ProjectScanner"/>, and provides
/// case-insensitive substring search over types, members, and file paths.
/// Persists the index through an optional <see cref="ICodeIndexCache"/> so that
/// unchanged files are not re-parsed on restart.
/// </summary>
public sealed class CodeIndexStore : ICodeIndexStore
{
    private readonly SolutionScanner _solutionScanner;
    private readonly ProjectScanner _projectScanner;
    private readonly ICodeIndexCache? _cache;

    private List<ProjectIndex> _projects = [];
    private bool _cacheSeeded;

    public CodeIndexStore(IFileSystem fileSystem, ICodeIndexCache? cache = null)
    {
        _solutionScanner = new SolutionScanner(fileSystem);
        _projectScanner = new ProjectScanner(fileSystem);
        _cache = cache;
    }

    /// <inheritdoc/>
    public void Rebuild(string repoRoot)
    {
        IReadOnlyList<string> projectFiles = _solutionScanner.FindProjectFiles(repoRoot);

        // Seed the per-file delta table from the disk cache on the very first rebuild.
        if (!_cacheSeeded && _cache is not null)
        {
            _projects = _cache.TryLoad()?.ToList() ?? [];
            _cacheSeeded = true;
        }

        // Build a lookup of currently cached source files for delta filtering.
        Dictionary<string, SourceFileIndex> cachedFiles = _projects
            .SelectMany(p => p.SourceFiles)
            .ToDictionary(f => f.FullPath, StringComparer.OrdinalIgnoreCase);

        List<ProjectIndex> rebuilt = [];

        foreach (string projectFile in projectFiles)
        {
            ProjectIndex project = _projectScanner.Scan(projectFile, cachedFiles);
            rebuilt.Add(project);
        }

        _projects = rebuilt;
        _cache?.Save(_projects);
    }

    /// <inheritdoc/>
    public IReadOnlyList<ProjectIndex> GetProjects() => _projects;

    /// <inheritdoc/>
    public IReadOnlyList<SourceFileIndex> GetFiles() =>
        _projects.SelectMany(p => p.SourceFiles).ToList();

    /// <inheritdoc/>
    public IReadOnlyList<TypeInfo> SearchTypes(string name, SymbolKind? kind = null) =>
        _projects
            .SelectMany(p => p.SourceFiles)
            .SelectMany(f => f.Types)
            .Where(t => t.Name.Contains(name, StringComparison.OrdinalIgnoreCase) &&
                        (kind is null || t.Kind == kind))
            .ToList();

    /// <inheritdoc/>
    public IReadOnlyList<MemberInfo> SearchMembers(string name, SymbolKind? kind = null) =>
        _projects
            .SelectMany(p => p.SourceFiles)
            .SelectMany(f => f.Types)
            .SelectMany(t => t.Members)
            .Where(m => m.Name.Contains(name, StringComparison.OrdinalIgnoreCase) &&
                        (kind is null || m.Kind == kind))
            .ToList();

    /// <inheritdoc/>
    public IReadOnlyList<SourceFileIndex> SearchFiles(string pathFragment) =>
        _projects
            .SelectMany(p => p.SourceFiles)
            .Where(f => f.FullPath.Contains(pathFragment, StringComparison.OrdinalIgnoreCase))
            .ToList();
}
