using CodeIndex.Abstractions;
using CodeIndex.Models;
using CodeIndex.Parsing;

namespace CodeIndex.Indexing;

/// <summary>
/// Scans a single .csproj file: enumerates its sibling .cs files and parses
/// each one through <see cref="SourceFileParser"/>, applying delta filtering
/// so unchanged files reuse their cached <see cref="SourceFileIndex"/>.
/// </summary>
public sealed class ProjectScanner
{
    private readonly IFileSystem _fileSystem;
    private readonly SourceFileParser _parser;

    public ProjectScanner(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
        _parser = new SourceFileParser(fileSystem);
    }

    /// <summary>
    /// Scans the project directory of <paramref name="projectFilePath"/> and returns a
    /// <see cref="ProjectIndex"/>. Files present in <paramref name="cached"/> whose
    /// on-disk <see cref="DateTime"/> has not changed are reused without re-parsing.
    /// </summary>
    public ProjectIndex Scan(
        string projectFilePath,
        IReadOnlyDictionary<string, SourceFileIndex> cached)
    {
        string projectDir = Path.GetDirectoryName(projectFilePath)!;
        string projectName = Path.GetFileNameWithoutExtension(projectFilePath);

        List<SourceFileIndex> sourceFiles = [];

        foreach (string filePath in EnumerateCsFiles(projectDir))
        {
            if (!SourceFileParser.ShouldParse(filePath))
            {
                continue;
            }

            DateTime diskTime = _fileSystem.GetLastWriteTimeUtc(filePath);

            if (cached.TryGetValue(filePath, out SourceFileIndex? cachedEntry) &&
                cachedEntry.IndexedAtUtc == diskTime)
            {
                sourceFiles.Add(cachedEntry);
                continue;
            }

            SourceFileIndex? parsed = _parser.Parse(filePath);
            if (parsed is not null)
            {
                sourceFiles.Add(parsed);
            }
        }

        return new ProjectIndex
        {
            Name = projectName,
            Directory = projectDir,
            ProjectFilePath = projectFilePath,
            SourceFiles = sourceFiles,
        };
    }

    private IEnumerable<string> EnumerateCsFiles(string directory)
    {
        if (!_fileSystem.DirectoryExists(directory))
        {
            return [];
        }

        return _fileSystem
            .EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsInSkippedDirectory(f));
    }

    private static bool IsInSkippedDirectory(string filePath)
    {
        return filePath
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])
            .Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                            segment.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }
}
