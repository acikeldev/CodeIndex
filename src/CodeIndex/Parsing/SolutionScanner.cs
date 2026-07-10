using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodeIndex.Abstractions;

namespace CodeIndex.Parsing;

/// <summary>
/// Discovers projects and source files with a SINGLE pruned recursive walk of the repo (was two full-repo
/// AllDirectories walks + a per-project walk each). Excluded directory names (node_modules, bin, obj, .git, .vs,
/// .codeindex) are pruned AT DESCENT so the tree beneath them is never enumerated, and every .cs file's mtime is
/// captured for free from the enumeration record (no separate GetLastWriteTimeUtc call). Projects keep the exact
/// prior semantics: only solution-referenced .csproj become projects, deduped by directory, and each .cs is
/// assigned to its deepest ancestor .csproj directory.
/// </summary>
public sealed partial class SolutionScanner
{
    public record ScanResult(List<ProjectInfo> Projects, Dictionary<string, long> Timestamps);
    public record ProjectInfo(string Name, string ProjectDirPath, List<string> CsFiles);

    [GeneratedRegex(@"^Project\(""\{[^}]+\}""\)\s*=\s*""[^""]*""\s*,\s*""([^""]+)""", RegexOptions.Multiline)]
    private static partial Regex CsprojPattern();

    // Directory NAMES pruned at descent (matched case-insensitively). Their subtrees are never enumerated.
    private static readonly HashSet<string> PrunedDirNames =
        new(StringComparer.OrdinalIgnoreCase) { "node_modules", "bin", "obj", ".git", ".vs", ".codeindex" };

    private readonly IFileSystem _fileSystem;

    public SolutionScanner(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    private sealed class WalkCollector
    {
        public List<string> SlnFiles { get; } = [];
        public List<string> SlnxFiles { get; } = [];
        public List<string> CsprojFiles { get; } = [];
        public HashSet<string> CsprojDirs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Path, long Ticks)> CsFiles { get; } = [];
        public HashSet<string> DbmlFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ProjectAcc(string name, string dirPath)
    {
        public string Name { get; } = name;
        public string DirPath { get; } = dirPath;
        public List<string> Files { get; } = [];
    }

    public ScanResult Scan(string repoRoot, IReadOnlyList<string>? extraExcludes = null, bool looseProjects = false)
    {
        string root = Path.GetFullPath(repoRoot);

        HashSet<string> pruned = PrunedDirNames;
        if (extraExcludes is { Count: > 0 })
        {
            pruned = new HashSet<string>(PrunedDirNames, StringComparer.OrdinalIgnoreCase);
            foreach (string e in extraExcludes)
            {
                if (!string.IsNullOrWhiteSpace(e))
                {
                    pruned.Add(e.Trim());
                }
            }
        }

        WalkCollector collector = new();
        Walk(root, collector, pruned);

        Dictionary<string, ProjectAcc> byDir = ResolveSolutionProjectDirs(collector);
        if (looseProjects)
        {
            AddLooseProjects(collector, byDir);
        }

        return BuildResult(collector, byDir);
    }

    // Register every discovered .csproj dir that no solution referenced, so a repo with no .sln (or partial
    // coverage) still indexes its projects instead of silently indexing nothing.
    private static void AddLooseProjects(WalkCollector c, Dictionary<string, ProjectAcc> byDir)
    {
        foreach (string csproj in c.CsprojFiles)
        {
            string dir = Path.GetDirectoryName(csproj)!;
            if (!byDir.ContainsKey(dir))
            {
                byDir[dir] = new ProjectAcc(Path.GetFileNameWithoutExtension(csproj), dir);
            }
        }
    }

    // Drives the descent itself over IFileSystem.EnumerateDirectoryEntries: pruned directory NAMES are skipped at
    // descent (their subtrees never enumerated) and reparse-point directories are skipped as a cycle guard. Each
    // file's timestamp is captured for free from the enumeration record — no extra stat. AttributesToSkip=None
    // semantics (indexing hidden/system .cs files) live in the concrete file system, not here.
    private void Walk(string root, WalkCollector c, HashSet<string> pruned)
    {
        Stack<string> stack = new();
        stack.Push(root);

        while (stack.Count > 0)
        {
            string dir = stack.Pop();

            List<FileSystemEntry> entries;
            try
            {
                entries = _fileSystem.EnumerateDirectoryEntries(dir).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                continue; // inaccessible / vanished mid-walk — skip this subtree, keep scanning the rest
            }

            foreach (FileSystemEntry entry in entries)
            {
                if (entry.IsDirectory)
                {
                    if (pruned.Contains(entry.Name))
                    {
                        continue;
                    }

                    if (entry.IsReparsePoint)
                    {
                        continue; // junction/symlink — don't follow (cycle guard)
                    }

                    stack.Push(entry.FullPath);
                }
                else
                {
                    // Extension + timestamp are prefetched from the enumeration record — no extra stat.
                    switch (Path.GetExtension(entry.Name).ToLowerInvariant())
                    {
                        case ".cs":
                            c.CsFiles.Add((entry.FullPath, entry.LastWriteTimeUtcTicks));
                            break;
                        case ".csproj":
                            c.CsprojFiles.Add(entry.FullPath);
                            c.CsprojDirs.Add(Path.GetDirectoryName(entry.FullPath)!);
                            break;
                        case ".sln":
                            c.SlnFiles.Add(entry.FullPath);
                            break;
                        case ".slnx":
                            c.SlnxFiles.Add(entry.FullPath);
                            break;
                        case ".dbml":
                            c.DbmlFiles.Add(entry.FullPath);
                            break;
                    }
                }
            }
        }
    }

    // Only .csproj files actually referenced by a solution become projects, deduped by directory (first wins).
    // .sln parsed before .slnx (preserves the historical name tie-break for a shared directory).
    private Dictionary<string, ProjectAcc> ResolveSolutionProjectDirs(WalkCollector c)
    {
        HashSet<string> csprojFileSet = new(c.CsprojFiles, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ProjectAcc> byDir = new(StringComparer.OrdinalIgnoreCase);

        foreach (string sln in c.SlnFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            string slnDir = Path.GetDirectoryName(sln)!;
            string content;
            try
            {
                content = _fileSystem.ReadAllText(sln);
            }
            catch
            {
                continue;
            }

            foreach (Match m in CsprojPattern().Matches(content))
            {
                TryRegister(m.Groups[1].Value, slnDir, csprojFileSet, byDir);
            }
        }

        foreach (string slnx in c.SlnxFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            string slnxDir = Path.GetDirectoryName(slnx)!;
            XDocument doc;
            try
            {
                doc = XDocument.Parse(_fileSystem.ReadAllText(slnx));
            }
            catch
            {
                continue;
            }

            foreach (XElement projectElement in doc.Descendants("Project"))
            {
                string? path = projectElement.Attribute("Path")?.Value;
                if (path is not null)
                {
                    TryRegister(path, slnxDir, csprojFileSet, byDir);
                }
            }
        }

        return byDir;
    }

    private static void TryRegister(string relPath, string slnDir, HashSet<string> csprojFileSet, Dictionary<string, ProjectAcc> byDir)
    {
        if (!relPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string full = Path.GetFullPath(Path.Combine(slnDir, relPath));
        if (!csprojFileSet.Contains(full)) // replaces the old file-existence probe (only walk-discovered csproj count)
        {
            return;
        }

        string dir = Path.GetDirectoryName(full)!;
        if (byDir.ContainsKey(dir))
        {
            return; // dedup by directory, first wins
        }

        byDir[dir] = new ProjectAcc(Path.GetFileNameWithoutExtension(full), dir);
    }

    private ScanResult BuildResult(WalkCollector c, Dictionary<string, ProjectAcc> byDir)
    {
        // Case-insensitive so delta comparison against a differently-cased cached key set is a no-op, not churn.
        Dictionary<string, long> timestamps = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string path, long ticks) in c.CsFiles)
        {
            if (!IsIndexableCs(path, c.DbmlFiles))
            {
                continue;
            }

            string? owner = DeepestCsprojDir(path, c.CsprojDirs);
            if (owner is null)
            {
                continue; // no ancestor .csproj at all — belongs to no project (as today)
            }

            if (!byDir.TryGetValue(owner, out ProjectAcc? acc))
            {
                continue; // deepest ancestor is a NON-solution sub-project — excluded (matches old sub-project rule)
            }

            acc.Files.Add(path);
            timestamps[path] = ticks;
        }

        List<ProjectInfo> projects = byDir.Values
            .Where(a => a.Files.Count > 0)
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a => new ProjectInfo(a.Name, a.DirPath, a.Files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();

        return new ScanResult(projects, timestamps);
    }

    // The deepest ancestor directory (including the file's own) that contains a .csproj, or null.
    // "Not under any deeper csproj dir" == "deepest csproj ancestor is this project" — exactly the old exclusion.
    private static string? DeepestCsprojDir(string csPath, HashSet<string> csprojDirs)
    {
        string? dir = Path.GetDirectoryName(csPath);
        while (dir is not null)
        {
            if (csprojDirs.Contains(dir))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    private static bool IsIndexableCs(string csPath, HashSet<string> dbmlFiles) =>
        !IsDesignerWithoutDbml(csPath, dbmlFiles.Contains);

    // Single source of truth for the LINQ-to-SQL designer.cs rule: a "<Name>.Designer.cs" is only indexable when a
    // sibling "<Name>.dbml" exists (those hold real L2S entities). WinForms/settings/resx designers have no dbml.
    // The dbml-existence check is injected so the scanner can use its prefetched set and the watcher can probe disk.
    internal static bool IsDesignerWithoutDbml(string csPath, Func<string, bool> dbmlExists)
    {
        string fileName = Path.GetFileName(csPath);
        const string suffix = ".designer.cs";
        if (fileName.Length <= suffix.Length || !fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false; // not a designer file → indexable
        }

        string baseName = fileName[..^suffix.Length];
        string siblingDbml = Path.Combine(Path.GetDirectoryName(csPath)!, baseName + ".dbml");
        return !dbmlExists(siblingDbml);
    }

    // Single-path form for callers without a prefetched dbml set (e.g. the watcher's new-file gate).
    internal bool IsIndexableCsOnDisk(string csPath) =>
        !IsDesignerWithoutDbml(csPath, _fileSystem.FileExists);
}
