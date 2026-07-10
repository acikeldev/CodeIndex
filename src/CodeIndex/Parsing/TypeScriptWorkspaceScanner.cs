using System.Text.Json;
using CodeIndex.Abstractions;
using CodeIndex.Models;

namespace CodeIndex.Parsing;

/// <summary>
/// Discovers TypeScript/SCSS source files and their project boundaries with a SINGLE pruned recursive walk,
/// independent of the C# <see cref="SolutionScanner"/> (kept separate so the C# cold-start path is untouched — see
/// the Phase 2 blueprint). A project boundary is a directory holding <c>tsconfig.json</c> or <c>package.json</c>;
/// each source file is assigned to its nearest ancestor boundary (project name = that dir's <c>package.json</c>
/// "name" if present, else the dir name). A <c>.scss</c> (or <c>.ts</c>) with no boundary ancestor gets a synthetic
/// <c>scss:&lt;dir&gt;</c> / <c>ts:&lt;dir&gt;</c> project so nothing is dropped. Timestamps are captured for free
/// from the enumeration record and keyed case-insensitively (so a differently-cased re-scan is a no-op, not churn).
/// </summary>
public sealed class TypeScriptWorkspaceScanner
{
    /// <param name="Projects">Discovered TS/SCSS project boundaries (display/discovery only — never fed to the
    /// C#-only dependency graph).</param>
    /// <param name="Timestamps">file path → last-write ticks (OrdinalIgnoreCase).</param>
    /// <param name="FileToProject">file path → owning project name (OrdinalIgnoreCase).</param>
    /// <param name="Aliases">tsconfig paths + package names, harvested for the deferred alias-resolution slice.</param>
    public record TsScanResult(
        List<TsProjectInfo> Projects,
        Dictionary<string, long> Timestamps,
        Dictionary<string, string> FileToProject,
        AliasMap Aliases);

    // Directory NAMES pruned at descent (subtrees never enumerated). Superset of the C# scanner's set: adds the
    // TS build-output dirs dist / .next.
    private static readonly HashSet<string> PrunedDirNames = new(StringComparer.OrdinalIgnoreCase)
        { "node_modules", "bin", "obj", "dist", ".next", ".git", ".vs", ".codeindex" };

    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".ts", ".tsx", ".mts", ".cts", ".scss" };

    private static readonly JsonDocumentOptions JsoncOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IFileSystem _fileSystem;

    public TypeScriptWorkspaceScanner(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    private sealed class Collector
    {
        public List<(string Path, long Ticks)> SourceFiles { get; } = [];
        public HashSet<string> TsconfigDirs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> TsconfigFiles { get; } = [];               // all tsconfig*.json (alias sources)
        public HashSet<string> PackageDirs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> PackageNameByDir { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public TsScanResult Scan(string repoRoot, IReadOnlyList<string>? extraExcludes = null)
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

        Collector c = new();
        Walk(root, c, pruned);
        return BuildResult(c, root);
    }

    // Drives the descent over IFileSystem.EnumerateDirectoryEntries (same pattern as the C# SolutionScanner): pruned
    // directory NAMES are skipped at descent (their subtrees never enumerated) and reparse-point directories are
    // skipped as a cycle guard. Each file's timestamp is captured for free from the enumeration record — no extra
    // stat.
    private void Walk(string root, Collector c, HashSet<string> pruned)
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
                    continue;
                }

                string name = entry.Name;

                // Boundary marker: exact tsconfig.json marks the dir as a project. Any tsconfig*.json is an alias
                // source (paths often live in a tsconfig.base.json that is NOT itself a project root).
                if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    && name.StartsWith("tsconfig", StringComparison.OrdinalIgnoreCase))
                {
                    c.TsconfigFiles.Add(entry.FullPath);
                    if (name.Equals("tsconfig.json", StringComparison.OrdinalIgnoreCase))
                    {
                        c.TsconfigDirs.Add(Path.GetDirectoryName(entry.FullPath)!);
                    }

                    continue;
                }

                if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase))
                {
                    string pdir = Path.GetDirectoryName(entry.FullPath)!;
                    c.PackageDirs.Add(pdir);
                    string? pkgName = ReadPackageName(entry.FullPath);
                    if (pkgName is not null)
                    {
                        c.PackageNameByDir[pdir] = pkgName;
                    }

                    continue;
                }

                if (SourceExtensions.Contains(Path.GetExtension(name)))
                {
                    c.SourceFiles.Add((entry.FullPath, entry.LastWriteTimeUtcTicks));
                }
            }
        }
    }

    private TsScanResult BuildResult(Collector c, string root)
    {
        HashSet<string> boundaries = new(c.TsconfigDirs, StringComparer.OrdinalIgnoreCase);
        boundaries.UnionWith(c.PackageDirs);

        // Pass 1: assign each file to a boundary DIRECTORY (which is unique) with a candidate display name.
        List<(string Path, long Ticks, string Dir)> files = new(c.SourceFiles.Count);
        Dictionary<string, string> baseNameByDir = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, long ticks) in c.SourceFiles)
        {
            (string projectDir, string baseName) = ResolveProject(path, boundaries, c.PackageNameByDir);
            files.Add((path, ticks, projectDir));
            baseNameByDir[projectDir] = baseName;
        }

        // Pass 2: give every USED directory a UNIQUE name. Distinct directories can share a candidate name — the
        // repo has dozens of package-less boundary dirs named "__tests__" / "__mocks__" — so keying projects by the
        // bare name would collapse them into one (dropping all but the first, walk-order dependent). Disambiguate a
        // colliding name with its repo-relative directory so two directories never become one project.
        Dictionary<string, int> nameCounts = new(StringComparer.OrdinalIgnoreCase);
        foreach (string b in baseNameByDir.Values)
        {
            nameCounts[b] = nameCounts.GetValueOrDefault(b) + 1;
        }

        Dictionary<string, string> nameByDir = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string dir, string baseName) in baseNameByDir)
        {
            nameByDir[dir] = nameCounts[baseName] == 1
                ? baseName
                : $"{baseName} [{Path.GetRelativePath(root, dir).Replace('\\', '/')}]";
        }

        Dictionary<string, long> timestamps = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> fileToProject = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, long ticks, string dir) in files)
        {
            timestamps[path] = ticks;
            fileToProject[path] = nameByDir[dir];
        }

        List<TsProjectInfo> projects = nameByDir
            .Select(kv => new TsProjectInfo { Name = kv.Value, ProjectDirPath = kv.Key })
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new TsScanResult(projects, timestamps, fileToProject, BuildAliasMap(c));
    }

    // Nearest ancestor boundary dir → (dir, name). Falls back to a synthetic per-directory project for an orphan
    // file with no tsconfig/package ancestor, so nothing is silently dropped.
    private static (string Dir, string Name) ResolveProject(string filePath, HashSet<string> boundaries, Dictionary<string, string> packageNameByDir)
    {
        string fileDir = Path.GetDirectoryName(filePath)!;
        string? dir = fileDir;
        while (dir is not null)
        {
            if (boundaries.Contains(dir))
            {
                string name = packageNameByDir.TryGetValue(dir, out string? pkg) && !string.IsNullOrWhiteSpace(pkg)
                    ? pkg
                    : Path.GetFileName(dir);
                return (dir, name);
            }

            dir = Path.GetDirectoryName(dir);
        }

        string baseName = Path.GetFileName(fileDir);
        if (baseName.Length == 0)
        {
            baseName = fileDir;
        }

        string prefix = Path.GetExtension(filePath).Equals(".scss", StringComparison.OrdinalIgnoreCase) ? "scss:" : "ts:";
        return (fileDir, prefix + baseName);
    }

    private AliasMap BuildAliasMap(Collector c)
    {
        AliasMap map = new();
        foreach (KeyValuePair<string, string> kv in c.PackageNameByDir)
        {
            map.Packages[kv.Value] = kv.Key;
        }

        // Sorted so the "last tsconfig wins" for a duplicate pattern is DETERMINISTIC across runs (the walk order
        // is not). NOTE: baseUrl anchoring + resolving each target to its owning tsconfig dir is deferred to the
        // step-8 alias slice — the AliasMap is harvested + cached now but consumed by no query yet.
        foreach (string tsconfig in c.TsconfigFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(_fileSystem.ReadAllText(tsconfig), JsoncOptions);
                if (doc.RootElement.TryGetProperty("compilerOptions", out JsonElement co)
                    && co.TryGetProperty("paths", out JsonElement paths)
                    && paths.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty p in paths.EnumerateObject())
                    {
                        List<string> targets = new();
                        if (p.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement t in p.Value.EnumerateArray())
                            {
                                if (t.ValueKind == JsonValueKind.String)
                                {
                                    targets.Add(t.GetString()!);
                                }
                            }
                        }

                        map.Paths[p.Name] = targets; // last tsconfig wins on a duplicate pattern
                    }
                }
            }
            catch { /* unreadable / invalid tsconfig — skip its aliases */ }
        }

        return map;
    }

    private string? ReadPackageName(string packageJsonPath)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(_fileSystem.ReadAllText(packageJsonPath), JsoncOptions);
            if (doc.RootElement.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String)
            {
                string? s = n.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
        }
        catch { /* unreadable / invalid package.json */ }

        return null;
    }
}
