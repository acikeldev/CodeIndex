using CodeIndex.Abstractions;
using CodeIndex.Models;
using CodeIndex.Parsing;

namespace CodeIndex.Indexing;

/// <summary>
/// Mutable staging buffer that assembles an <see cref="IndexSnapshot"/> off to the side, then freezes it via
/// Build(). Only this type mutates the snapshot collections, and only BEFORE Build() publishes them — that is the
/// invariant the lock-free readers rely on.
/// </summary>
internal sealed class SnapshotBuilder
{
    private readonly Dictionary<string, ProjectIndex> _projects = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SourceFileIndex> _allSourceFiles = [];
    private readonly Dictionary<string, List<SourceFileIndex>> _sourceFilesByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SourceFileIndex> _sourceFilesByPath = new(StringComparer.OrdinalIgnoreCase);
    private int _typeCount;
    private int _memberCount;
    private IReadOnlyList<TsProjectInfo> _tsProjects = [];

    /// <summary>Register freshly-scanned projects (relative-path SourceFiles), skipping names already present.</summary>
    public void AddProjects(SolutionScanner.ScanResult scan)
    {
        foreach (SolutionScanner.ProjectInfo project in scan.Projects)
        {
            if (_projects.ContainsKey(project.Name))
            {
                continue;
            }

            _projects[project.Name] = new ProjectIndex
            {
                Name = project.Name,
                ProjectDirPath = project.ProjectDirPath,
                SourceFiles = project.CsFiles
                    .Select(f => Path.GetRelativePath(project.ProjectDirPath, f).Replace('\\', '/'))
                    .ToList(),
            };
        }
    }

    /// <summary>Seed projects carried forward from a prior snapshot (or the disk cache), skipping duplicates.</summary>
    public void AddProjects(IEnumerable<ProjectIndex> priorProjects)
    {
        foreach (ProjectIndex project in priorProjects)
        {
            _projects.TryAdd(project.Name, project);
        }
    }

    public void AddFile(SourceFileIndex file)
    {
        _allSourceFiles.Add(file);
        _sourceFilesByPath[file.SourceFilePath] = file;

        if (!_sourceFilesByName.TryGetValue(file.FileName, out List<SourceFileIndex>? list))
        {
            list = [];
            _sourceFilesByName[file.FileName] = list;
        }

        list.Add(file);

        _typeCount += file.Types.Count;
        _memberCount += file.Types.Sum(t => t.Members.Count);
    }

    /// <summary>Register the TS/SCSS project boundaries for the composed snapshot (discovery only — they never
    /// reach the C#-only dependency graph). Replaces any prior set.</summary>
    public void SetTsProjects(IReadOnlyList<TsProjectInfo> tsProjects) => _tsProjects = tsProjects;

    /// <summary>Freeze the snapshot. <paramref name="inheritedGraph"/> lets a merged (C#+TS) snapshot reuse the C#
    /// segment's already-materialized dependency-graph Lazy (its Projects are the same references), so a TS-only
    /// republish never rebuilds the C# graph; pass null for a fresh graph over this builder's Projects.</summary>
    public IndexSnapshot Build(
        IReadOnlyDictionary<string, long> timestamps,
        IFileSystem fileSystem,
        Lazy<ProjectDependencyGraph>? inheritedGraph = null) =>
        new(_projects, _allSourceFiles, _sourceFilesByName, _sourceFilesByPath, timestamps,
            _typeCount, _memberCount, fileSystem, _tsProjects, inheritedGraph);
}
