using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodeIndex.Abstractions;

namespace CodeIndex.Parsing;

/// <summary>
/// Discovers all .csproj files referenced by .sln and .slnx solution files
/// found under a repository root.
/// </summary>
public sealed class SolutionScanner
{
    private static readonly Regex SlnProjectPattern = new(
        @"Project\(""{[^}]+}""\)\s*=\s*""[^""]+""\s*,\s*""([^""]+\.csproj)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] SkippedDirectories =
        ["bin", "obj", "node_modules", ".vs", ".git"];

    private readonly IFileSystem _fileSystem;

    public SolutionScanner(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// Scans <paramref name="repoRoot"/> for solution files and returns the
    /// absolute paths of all referenced .csproj files, deduplicated.
    /// </summary>
    public IReadOnlyList<string> FindProjectFiles(string repoRoot)
    {
        HashSet<string> projectPaths = new(StringComparer.OrdinalIgnoreCase);

        foreach (string solutionFile in EnumerateSolutionFiles(repoRoot))
        {
            IEnumerable<string> projects = Path.GetExtension(solutionFile)
                .Equals(".slnx", StringComparison.OrdinalIgnoreCase)
                ? ParseSlnx(solutionFile)
                : ParseSln(solutionFile);

            string solutionDir = Path.GetDirectoryName(solutionFile)!;

            foreach (string relativePath in projects)
            {
                string absolute = Path.GetFullPath(
                    Path.Combine(solutionDir, relativePath.Replace('\\', Path.DirectorySeparatorChar)));

                if (_fileSystem.FileExists(absolute))
                {
                    projectPaths.Add(absolute);
                }
            }
        }

        return [.. projectPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)];
    }

    private IEnumerable<string> EnumerateSolutionFiles(string root)
    {
        return _fileSystem
            .EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f =>
            {
                string ext = Path.GetExtension(f);
                if (!ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return !f.Split(Path.DirectorySeparatorChar)
                    .Any(segment => SkippedDirectories.Contains(
                        segment, StringComparer.OrdinalIgnoreCase));
            });
    }

    private IEnumerable<string> ParseSln(string slnPath)
    {
        string content = _fileSystem.ReadAllText(slnPath);
        return SlnProjectPattern
            .Matches(content)
            .Select(m => m.Groups[1].Value);
    }

    private IEnumerable<string> ParseSlnx(string slnxPath)
    {
        string content = _fileSystem.ReadAllText(slnxPath);
        XDocument doc = XDocument.Parse(content);
        return doc.Descendants("Project")
            .Select(e => e.Attribute("Path")?.Value)
            .Where(p => p is not null && p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(p => p!);
    }
}
