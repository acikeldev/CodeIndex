using System.Diagnostics;
using CodeIndex.Abstractions;
using CodeIndex.Caching;
using CodeIndex.Internal;
using CodeIndex.Models;
using CodeIndex.Parsing;

namespace CodeIndex.Indexing;

/// <summary>
/// The queryable + rebuildable in-memory code index behind every MCP tool. Query members are lock-free reads over
/// an immutable snapshot; the mutation members publish new snapshots atomically. All filesystem access flows
/// through the injected <see cref="IFileSystem"/> — the scanners/parsers are constructed once over it and are
/// stateless (they hold only the file system), so PLINQ can drive them concurrently.
/// </summary>
internal sealed class CodeIndexStore : ICodeIndexStore
{
    // The single published state readers see. Readers Volatile.Read it once and work lock-free; a rebuild builds a
    // fresh snapshot entirely off-lock then Volatile.Write-publishes it (the release fence makes every constructing
    // write visible to any thread that reads the new reference). A published snapshot is never mutated, so no reader
    // ever sees a torn/half-updated graph. _snapshot is always Compose(_csSnapshot, _tsSegment).
    private IndexSnapshot _snapshot = IndexSnapshot.Empty;

    // The two source segments that Compose into _snapshot. Each has EXACTLY ONE writer: PublishCs writes
    // _csSnapshot, PublishTs writes _tsSegment; both re-read the sibling and re-publish under _rebuildGate, so a
    // racing C# and TS rebuild can never drop a segment. _csSnapshot is also the C# delta baseline (NOT the merged
    // _snapshot — using the merged one would make every TS file look "removed" on each C# rebuild) and the C# cache
    // source. Written only under _rebuildGate.
    private IndexSnapshot _csSnapshot = IndexSnapshot.Empty;
    private TsSegment _tsSegment = TsSegment.Empty;

    // _rebuildGate is the SOLE gate under which _snapshot/_csSnapshot/_tsSegment are written and _snapshot is
    // published; it serializes concurrent C# rebuilds (startup build vs first watcher event, overlapping fires).
    // Readers never take it — reads are fully lock-free.
    private readonly object _rebuildGate = new();

    // Serializes TS rebuild EXECUTION so the off-lock parse works against a stable baseline (only one
    // RebuildTypeScript in flight). It must NEVER be the only lock around a segment write or a _snapshot publish —
    // those always take _rebuildGate. Lock order is _tsRebuildLock (outer) then _rebuildGate (inner, briefly); no
    // path takes them the other way, so there is no cycle / deadlock.
    private readonly object _tsRebuildLock = new();

    private readonly IFileSystem _fileSystem;
    private readonly ICodeIndexCache _csCache;
    private readonly ITsIndexCache _tsCache;
    private readonly CodeIndexConfig _config;

    // Stateless scanners/parsers, constructed ONCE over the injected file system. They hold only the file system, so
    // the PLINQ workers below can call them concurrently.
    private readonly SolutionScanner _solutionScanner;
    private readonly SourceFileParser _sourceFileParser;
    private readonly TypeScriptWorkspaceScanner _tsWorkspaceScanner;
    private readonly TypeScriptParser _tsParser;
    private readonly ScssParser _scssParser;

    public CodeIndexStore(IFileSystem fileSystem, ICodeIndexCache csCache, ITsIndexCache tsCache, CodeIndexConfig config)
    {
        _fileSystem = fileSystem;
        _csCache = csCache;
        _tsCache = tsCache;
        _config = config;

        _solutionScanner = new SolutionScanner(_fileSystem);
        _sourceFileParser = new SourceFileParser(_fileSystem);
        _tsWorkspaceScanner = new TypeScriptWorkspaceScanner(_fileSystem);
        _tsParser = new TypeScriptParser(_fileSystem);
        _scssParser = new ScssParser(_fileSystem);
    }

    // Speculative-appendix behaviour, surfaced from config so the (config-less) MCP tools can gate on it.
    public bool SpeculateEnabled => _config.Speculate;
    public int SpeculateTokenBudget => _config.SpeculateTokenBudget;

    private IndexSnapshot Current => Volatile.Read(ref _snapshot);

    // Compose the published snapshot from the two segments. Fast-path: with no TS files (C#-only repos and the whole
    // pre-TS-build startup window) return the C# snapshot unchanged — zero overhead, byte-identical to the C#-only
    // tool. Otherwise union both file sets; C# Projects alone seed the dependency graph (TS projects are kept
    // separate as display-only), and the merged snapshot INHERITS the C# graph Lazy so a TS-only republish reuses
    // the already-materialized C# graph instead of thrashing it. FileTimestamps stays C#-only (the TS baseline lives
    // on _tsSegment).
    private IndexSnapshot Compose(IndexSnapshot cs, TsSegment ts)
    {
        if (ts.Files.Count == 0)
        {
            return cs;
        }

        SnapshotBuilder builder = new();
        builder.AddProjects(cs.Projects.Values);
        foreach (SourceFileIndex f in cs.AllSourceFiles)
        {
            builder.AddFile(f);
        }

        foreach (SourceFileIndex f in ts.Files)
        {
            builder.AddFile(f);
        }

        builder.SetTsProjects(ts.Projects);
        return builder.Build(cs.FileTimestamps, _fileSystem, cs.DependencyGraphLazy);
    }

    // Publish a new C# segment and re-compose with the live TS segment. Takes _rebuildGate.
    private void PublishCs(IndexSnapshot cs)
    {
        lock (_rebuildGate)
        {
            PublishCsLocked(cs);
        }
    }

    // As PublishCs, but the caller already holds _rebuildGate (the Rebuild body). lock is reentrant in C#, but
    // calling this form avoids re-taking the gate.
    private void PublishCsLocked(IndexSnapshot cs)
    {
        _csSnapshot = cs;
        Volatile.Write(ref _snapshot, Compose(cs, _tsSegment));
    }

    // Publish a new TS segment and re-compose with the live C# segment. Takes _rebuildGate (held only for the
    // O(files) Compose + write — never for the multi-second parse), so it can't block a racing C# delta for long.
    private void PublishTs(TsSegment ts)
    {
        lock (_rebuildGate)
        {
            _tsSegment = ts;
            Volatile.Write(ref _snapshot, Compose(_csSnapshot, ts));
        }
    }

    // The repo root this store indexes, retained for path-containment security checks. Set once at startup
    // (same value every call); volatile so lock-free readers see it.
    private volatile string? _repoRoot;
    public string? RepoRoot => _repoRoot;

    // How the current index came to be (full build / delta / cache preload). Volatile: written by the serialized
    // rebuild, read lock-free by the health tool.
    private volatile BuildInfo _lastBuild = new(DateTime.MinValue, "none", 0, 0, 0);
    public BuildInfo LastBuild => _lastBuild;

    /// <summary>Where the cache lives for this store's repo + config (for the health tool / diagnostics).</summary>
    public string CacheDirectory => _config.ResolveCacheDirectory(_repoRoot ?? Directory.GetCurrentDirectory());

    public int ProjectCount => Current.ProjectCount;
    // TS/SCSS project boundaries (kept out of the C#-only ProjectCount, which feeds the dependency graph). Exposed
    // so the overview tools can report a project total consistent with the merged file/type/member counts.
    public int TsProjectCount => Current.TsProjects.Count;
    public int SourceFileCount => Current.SourceFileCount;
    public int TypeCount => Current.TypeCount;
    public int MemberCount => Current.MemberCount;

    // Startup build (Program.cs). Delegates to the re-callable Rebuild.
    public void Build(string repoRoot) => Rebuild(repoRoot, fullRebuild: false);

    /// <summary>
    /// Fast startup path: publish a snapshot straight from the on-disk cache — NO scan, NO parse — so the MCP
    /// host can start and answer immediately (~0.1-0.2s) while the RepositoryWatcher revalidates in the
    /// background. Returns true if a usable cache was loaded, false if the caller should build synchronously
    /// (cold start with no cache — we block once rather than serve an empty index). Never throws.
    /// </summary>
    public bool LoadCachedSnapshot(string repoRoot)
    {
        _repoRoot = Path.GetFullPath(repoRoot);
        try
        {
            string cacheDir = _config.ResolveCacheDirectory(repoRoot);
            CacheData? cached = _csCache.Load(cacheDir);
            if (cached is null)
            {
                return false;
            }

            SnapshotBuilder builder = new();
            builder.AddProjects(cached.Projects);
            foreach (SourceFileIndex file in cached.SourceFiles)
            {
                builder.AddFile(file);
            }

            Dictionary<string, long> timestamps = new(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, long> kv in cached.FileTimestamps)
            {
                timestamps[kv.Key] = kv.Value;
            }

            // Publish as the C# segment under the rebuild lock like every other writer. At startup _tsSegment is
            // Empty, so Compose returns `loaded` unchanged — identical to a bare Volatile.Write, and free.
            IndexSnapshot loaded = builder.Build(timestamps, _fileSystem);
            PublishCs(loaded);
            _lastBuild = new BuildInfo(DateTime.UtcNow, "cache-load", 0, loaded.SourceFileCount, 0);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CodeIndex] Cache preload failed ({ex.GetType().Name}: {ex.Message}) — will build synchronously.");
            return false;
        }
    }

    /// <summary>Runs a rebuild on a background thread (Task.Run) so the caller — the RepositoryWatcher's
    /// ExecuteAsync — yields immediately and never blocks host startup. Rebuilds still serialize via the store's
    /// rebuild lock.</summary>
    public Task RefreshAsync(string repoRoot, bool fullRebuild = false, CancellationToken cancellationToken = default)
        => Task.Run(() => Rebuild(repoRoot, fullRebuild, cancellationToken), cancellationToken);

    /// <summary>
    /// (Re)builds the index and atomically publishes a new immutable snapshot. Safe to call repeatedly (the
    /// RepositoryWatcher calls it on file/branch changes). A branch switch is handled by the normal DELTA path —
    /// git bumps the mtime of every file it rewrites during a checkout, so the timestamp delta reparses exactly
    /// the files that differ between branches (identical files keep their mtime and are skipped). Pass
    /// <paramref name="fullRebuild"/> = true only to force a full reparse ignoring the cache. Readers never block
    /// on a rebuild — reads take no lock; the finished snapshot is swapped in atomically.
    /// </summary>
    public void Rebuild(string repoRoot, bool fullRebuild = false, CancellationToken cancellationToken = default)
    {
        _repoRoot = Path.GetFullPath(repoRoot);
        lock (_rebuildGate)
        {
            Stopwatch sw = Stopwatch.StartNew();
            string cacheDir = _config.ResolveCacheDirectory(repoRoot);

            SolutionScanner.ScanResult scanResult = _solutionScanner.Scan(repoRoot, _config.Exclude, _config.LooseProjects);

            // Timestamps captured for free during the single pruned walk — no per-file GetLastWriteTimeUtc stats.
            Dictionary<string, long> currentTimestamps = scanResult.Timestamps;
            Dictionary<string, string> fileToProject = [];
            foreach (SolutionScanner.ProjectInfo project in scanResult.Projects)
            {
                foreach (string csFile in project.CsFiles)
                {
                    fileToProject[csFile] = project.Name;
                }
            }

            // The C# delta baseline is the C#-ONLY segment, NOT the merged Current. Using Current would include TS
            // files, and since the C# scan never lists them they'd all look "removed" — every C# rebuild would purge
            // the TS segment and defeat the no-op short-circuit. This is the load-bearing correctness fix.
            IndexSnapshot prev = _csSnapshot;

            // FULL rebuild (branch switch / forced): reparse everything, ignore any base.
            if (fullRebuild)
            {
                PublishFull(scanResult, currentTimestamps, cacheDir, sw);
                return;
            }

            // Pick the delta base WITHOUT touching disk when a live snapshot already exists.
            IReadOnlyDictionary<string, long> baseTimestamps;
            IReadOnlyList<SourceFileIndex> baseFiles;
            IEnumerable<ProjectIndex> baseProjects;
            bool usedPrev;
            if (prev.SourceFileCount > 0)
            {
                baseTimestamps = prev.FileTimestamps;
                baseFiles = prev.AllSourceFiles;
                baseProjects = prev.Projects.Values;
                usedPrev = true;
            }
            else
            {
                CacheData? cached = _csCache.Load(cacheDir);
                if (cached is null)
                {
                    PublishFull(scanResult, currentTimestamps, cacheDir, sw);
                    return;
                }

                baseTimestamps = cached.FileTimestamps;
                baseFiles = cached.SourceFiles;
                baseProjects = cached.Projects;
                usedPrev = false;
            }

            IndexCache.DeltaResult delta = IndexCache.ComputeDelta(baseTimestamps, currentTimestamps);
            if (delta.IsFullRebuild)
            {
                PublishFull(scanResult, currentTimestamps, cacheDir, sw);
                return;
            }

            SnapshotBuilder builder = new();
            builder.AddProjects(baseProjects);   // carry prior projects forward (never prune — as today)
            builder.AddProjects(scanResult);     // add newly-scanned, skip existing

            // Copy-forward every survivor (reuses already-parsed SourceFileIndex objects — no reparse).
            HashSet<string> affected = new(delta.RemovedFiles.Concat(delta.ChangedFiles), StringComparer.OrdinalIgnoreCase);
            foreach (SourceFileIndex f in baseFiles)
            {
                if (!affected.Contains(f.SourceFilePath))
                {
                    builder.AddFile(f);
                }
            }

            int reparsed = 0;
            foreach (string file in delta.ChangedFiles)
            {
                if (!fileToProject.TryGetValue(file, out string? projectName))
                {
                    continue;
                }

                SourceFileIndex? parsed = _sourceFileParser.Parse(file, projectName);
                if (parsed is not null)
                {
                    builder.AddFile(parsed);
                }

                reparsed++;
            }

            int removed = delta.RemovedFiles.Count;

            if (reparsed == 0 && removed == 0)
            {
                // Nothing changed. If we're already serving that state, keep it (no realloc, no swap, no save).
                if (usedPrev)
                {
                    sw.Stop();
                    LogDelta(0, 0, prev, sw);
                    return;
                }

                // Cold start from the disk cache with no changes: publish the rebuilt snapshot, but the on-disk
                // cache is already current so skip the save.
                IndexSnapshot same = builder.Build(currentTimestamps, _fileSystem);
                PublishCsLocked(same);
                sw.Stop();
                LogDelta(0, 0, same, sw);
                return;
            }

            IndexSnapshot snap = builder.Build(currentTimestamps, _fileSystem);
            PublishCsLocked(snap);
            _csCache.Save(cacheDir, BuildCacheData(snap, currentTimestamps));
            sw.Stop();
            LogDelta(reparsed, removed, snap, sw);
        }
    }

    private void PublishFull(SolutionScanner.ScanResult scanResult, Dictionary<string, long> timestamps, string cacheDir, Stopwatch sw)
    {
        IndexSnapshot full = BuildFullSnapshot(scanResult, timestamps);
        PublishCsLocked(full);
        _csCache.Save(cacheDir, BuildCacheData(full, timestamps));
        sw.Stop();
        _lastBuild = new BuildInfo(DateTime.UtcNow, "full", sw.ElapsedMilliseconds, full.SourceFileCount, 0);
        Console.Error.WriteLine($"[CodeIndex] Full build: {full.ProjectCount} projects, {full.SourceFileCount} files, {full.TypeCount} types, {full.MemberCount} members in {sw.ElapsedMilliseconds}ms");
    }

    private IndexSnapshot BuildFullSnapshot(SolutionScanner.ScanResult scanResult, Dictionary<string, long> timestamps)
    {
        SnapshotBuilder builder = new();
        builder.AddProjects(scanResult);

        // Parse files in parallel — Roslyn ParseText is thread-safe and each parse is independent, pure work.
        // AsOrdered keeps AllSourceFiles deterministic across runs; the cheap merge stays single-threaded.
        List<SourceFileIndex> parsedFiles = scanResult.Projects
            .SelectMany(project => project.CsFiles.Select(csFile => (File: csFile, Project: project.Name)))
            .AsParallel()
            .AsOrdered()
            .Select(pair => _sourceFileParser.Parse(pair.File, pair.Project))
            .Where(parsed => parsed is not null)
            .Select(parsed => parsed!)
            .ToList();

        foreach (SourceFileIndex parsed in parsedFiles)
        {
            builder.AddFile(parsed);
        }

        return builder.Build(timestamps, _fileSystem);
    }

    private void LogDelta(int reparsed, int removed, IndexSnapshot s, Stopwatch sw)
    {
        _lastBuild = new BuildInfo(DateTime.UtcNow, "delta", sw.ElapsedMilliseconds, reparsed, removed);
        Console.Error.WriteLine($"[CodeIndex] Delta: {reparsed} reparsed, {removed} removed ({s.ProjectCount} projects, {s.SourceFileCount} files, {s.TypeCount} types, {s.MemberCount} members) in {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>Runs a TypeScript/SCSS (re)build on a background thread so the caller — the RepositoryWatcher —
    /// never blocks. TS rebuilds serialize via <c>_tsRebuildLock</c> and the C# index keeps serving throughout;
    /// the multi-second parse happens entirely off the publish gate (see RebuildTypeScript). Only runs when
    /// config.IndexTypeScript is on (the caller gates it).</summary>
    public Task RefreshTypeScriptAsync(string repoRoot, bool fullRebuild = false, CancellationToken cancellationToken = default)
        => Task.Run(() => RebuildTypeScript(repoRoot, fullRebuild, cancellationToken), cancellationToken);

    // Consistent read of the published TS segment. _tsSegment is written only under _rebuildGate (by PublishTs);
    // read it under the same gate. Cheap and uncontended.
    private TsSegment CurrentTsSegment()
    {
        lock (_rebuildGate)
        {
            return _tsSegment;
        }
    }

    /// <summary>
    /// (Re)builds the TS/SCSS segment and publishes a merged snapshot. The scan + delta + tree-sitter parse run
    /// OUTSIDE _rebuildGate (holding only _tsRebuildLock, which serializes TS execution so the copy-forward baseline
    /// is stable) — so a concurrent C# delta never waits on the parse. The gate is taken only inside PublishTs, for
    /// the O(files) Compose + write. A branch switch / tsconfig edit passes fullRebuild = true to force a full
    /// re-parse + re-resolve; ordinary edits take the timestamp-delta path (reparse only changed files).
    /// </summary>
    private void RebuildTypeScript(string repoRoot, bool fullRebuild, CancellationToken ct)
    {
        lock (_tsRebuildLock)
        {
            Stopwatch sw = Stopwatch.StartNew();
            string cacheDir = _config.ResolveCacheDirectory(repoRoot);

            TsSegment prevTs = CurrentTsSegment();

            // Warm start: if nothing is published yet, serve the cached TS segment immediately (so TS symbols are
            // queryable within a beat), then revalidate against the live tree below.
            if (prevTs.Files.Count == 0 && !fullRebuild)
            {
                TsSegment? warm = _tsCache.Load(cacheDir);
                if (warm is not null)
                {
                    PublishTs(warm);
                    prevTs = warm;
                }
            }

            TypeScriptWorkspaceScanner.TsScanResult scan = _tsWorkspaceScanner.Scan(repoRoot, _config.Exclude);
            Dictionary<string, long> currentTimestamps = scan.Timestamps;

            IReadOnlyDictionary<string, long>? baseTimestamps = fullRebuild ? null : prevTs.Timestamps;
            IndexCache.DeltaResult delta = IndexCache.ComputeDelta(baseTimestamps, currentTimestamps);

            List<SourceFileIndex> files;
            int reparsed;
            if (delta.IsFullRebuild)
            {
                files = ParseAllTs(scan, ct);
                reparsed = files.Count;
            }
            else
            {
                if (delta.ChangedFiles.Count == 0 && delta.RemovedFiles.Count == 0)
                {
                    // Nothing changed since the baseline we're already serving — keep it, no republish, no save.
                    sw.Stop();
                    return;
                }

                HashSet<string> affected = new(delta.RemovedFiles.Concat(delta.ChangedFiles), StringComparer.OrdinalIgnoreCase);
                files = new List<SourceFileIndex>(prevTs.Files.Count);
                foreach (SourceFileIndex f in prevTs.Files)
                {
                    if (!affected.Contains(f.SourceFilePath))
                    {
                        files.Add(f); // copy-forward survivor (already-parsed, no reparse)
                    }
                }

                files.AddRange(ParseChangedTs(delta.ChangedFiles, scan.FileToProject, ct));
                reparsed = delta.ChangedFiles.Count;
            }

            TsSegment newTs = new(files, currentTimestamps, scan.Projects, scan.Aliases);
            PublishTs(newTs);
            _tsCache.Save(cacheDir, newTs);
            sw.Stop();
            Console.Error.WriteLine($"[CodeIndex] TS build: {newTs.Projects.Count} projects, {newTs.Files.Count} files ({reparsed} reparsed, {delta.RemovedFiles.Count} removed) in {sw.ElapsedMilliseconds}ms");
        }
    }

    // tree-sitter Parser is not thread-safe, but TypeScriptParser/ScssParser each construct a fresh Parser per call,
    // so each PLINQ worker parses independently. AsOrdered over a path-sorted source keeps output deterministic.
    private List<SourceFileIndex> ParseAllTs(TypeScriptWorkspaceScanner.TsScanResult scan, CancellationToken ct)
    {
        return scan.FileToProject
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .AsParallel().AsOrdered().WithCancellation(ct)
            .Select(kv => ParseTsOrScss(kv.Key, kv.Value))
            .Where(parsed => parsed is not null)
            .Select(parsed => parsed!)
            .ToList();
    }

    private List<SourceFileIndex> ParseChangedTs(List<string> changed, Dictionary<string, string> fileToProject, CancellationToken ct)
    {
        return changed
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .AsParallel().AsOrdered().WithCancellation(ct)
            .Select(path => fileToProject.TryGetValue(path, out string? project) ? ParseTsOrScss(path, project) : null)
            .Where(parsed => parsed is not null)
            .Select(parsed => parsed!)
            .ToList();
    }

    private SourceFileIndex? ParseTsOrScss(string path, string project) =>
        Path.GetExtension(path).Equals(".scss", StringComparison.OrdinalIgnoreCase)
            ? _scssParser.Parse(path, project)
            : _tsParser.Parse(path, project);

    // Below this many hits from the cheap Contains passes, and only for acronym-shaped queries, run the extra
    // camelCase-initials fallback pass (GUBR -> GetUsersByRole). Keeps the common path free of the extra walk.
    private const int InitialsFallbackThreshold = 5;

    public List<SymbolSearchResult> SearchSymbol(string query, string? kindFilter, string? projectFilter)
    {
        IndexSnapshot snap = Current;
        List<SourceFileIndex> files = snap.AllSourceFiles
            .Where(f => projectFilter is null || f.ProjectName.Equals(projectFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        List<SymbolSearchResult> results = [];
        foreach (SourceFileIndex file in files)
        {
            CollectMatches(file, kindFilter, name => name.Contains(query, StringComparison.OrdinalIgnoreCase), results);
        }

        // camelCase-initials fallback: only when the cheap passes found almost nothing and the query looks like an
        // acronym, so precise substring queries never pay for it and never get polluted (initials rank lowest).
        if (results.Count < InitialsFallbackThreshold && LooksLikeInitialism(query))
        {
            foreach (SourceFileIndex file in files)
            {
                CollectMatches(file, kindFilter,
                    name => !name.Contains(query, StringComparison.OrdinalIgnoreCase) && SymbolRanker.IsInitialsSubsequence(name, query),
                    results);
            }
        }

        results = Deduplicate(results);

        // Decorate-sort: compute each composite rank key ONCE, then sort by it (total order, deterministic).
        SymbolSortKey[] keys = new SymbolSortKey[results.Count];
        SymbolSearchResult[] arr = [.. results];
        for (int i = 0; i < arr.Length; i++)
        {
            keys[i] = SymbolRanker.KeyFor(arr[i], query);
        }

        Array.Sort(keys, arr);

        // Caller is responsible for applying any limit / token budget — see SearchSymbolTool.
        return [.. arr];
    }

    private static void CollectMatches(SourceFileIndex file, string? kindFilter, Func<string, bool> nameMatch, List<SymbolSearchResult> results)
    {
        foreach (TypeInfo type in file.Types)
        {
            if (MatchesKindFilter(type.Kind, kindFilter) && nameMatch(type.Name))
            {
                results.Add(MakeTypeResult(file, type));
            }

            foreach (MemberInfo member in type.Members)
            {
                if (MatchesKindFilter(member.Kind, kindFilter) && nameMatch(member.Name))
                {
                    results.Add(MakeMemberResult(file, type, member));
                }
            }
        }
    }

    private static SymbolSearchResult MakeTypeResult(SourceFileIndex file, TypeInfo type) => new()
    {
        Name = type.Name,
        Kind = type.Kind.ToString(),
        Project = file.ProjectName,
        File = file.FileName,
        SourceFilePath = file.SourceFilePath,
        StartLine = type.StartLine,
        LineCount = type.LineCount,
        Namespace = type.Namespace ?? file.Namespace,
        Signature = $"{type.TypeKeyword} {type.Name}",
        ParentType = null
    };

    private static SymbolSearchResult MakeMemberResult(SourceFileIndex file, TypeInfo type, MemberInfo member) => new()
    {
        Name = member.Name,
        Kind = member.Kind.ToString(),
        Project = file.ProjectName,
        File = file.FileName,
        SourceFilePath = file.SourceFilePath,
        StartLine = member.StartLine,
        LineCount = member.LineCount,
        Namespace = type.Namespace ?? file.Namespace,
        Signature = member.Signature,
        ParentType = type.Name
    };

    // Collapse byte-identical rows (a physical file compiled into >1 project is parsed into >1 SourceFileIndex,
    // so its symbols emit duplicate rows). Keyed on (path, line, signature) — deliberately NOT project.
    private static List<SymbolSearchResult> Deduplicate(List<SymbolSearchResult> results)
    {
        HashSet<(string, int, string)> seen = new();
        List<SymbolSearchResult> output = new(results.Count);
        foreach (SymbolSearchResult r in results)
        {
            if (seen.Add((r.SourceFilePath, r.StartLine, r.Signature ?? r.Name)))
            {
                output.Add(r);
            }
        }

        return output;
    }

    // An acronym-shaped query worth trying initials matching on: short, all letters, with at least one uppercase.
    private static bool LooksLikeInitialism(string query) =>
        query.Length is >= 2 and <= 6 && query.All(char.IsLetter) && query.Any(char.IsUpper);

    public SourceFileIndex? GetFileOutline(string fileQuery)
    {
        IndexSnapshot snap = Current;
        if (snap.SourceFilesByName.TryGetValue(fileQuery, out List<SourceFileIndex>? exactMatches))
        {
            return exactMatches[0];
        }

        return snap.SourceFilesByName
            .Where(kvp => kvp.Key.Contains(fileQuery, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Value[0])
            .FirstOrDefault();
    }

    public string? ResolveSourceFilePath(string fileQuery) => GetFileOutline(fileQuery)?.SourceFilePath;

    /// <summary>True if the given full path is an indexed source file (used by get_symbol_source to allow reading
    /// indexed files even if they somehow sit outside the repo root, e.g. linked files).</summary>
    public bool IsIndexedPath(string fullPath) => Current.SourceFilesByPath.ContainsKey(fullPath);

    public List<ProjectIndex> ListProjects() =>
        [.. UnifiedProjects(Current).Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];

    public ProjectIndex? GetProject(string projectName)
    {
        IndexSnapshot snap = Current;
        if (snap.Projects.TryGetValue(projectName, out ProjectIndex? project))
        {
            return project;
        }

        // TS/SCSS project: synthesize a view on demand (avoids grouping TS files for the common C# hit).
        return snap.TsProjects.Count > 0 ? UnifiedProjects(snap).GetValueOrDefault(projectName) : null;
    }

    // Unified project view for the discovery tools: the real C# ProjectIndex entries plus a synthesized entry per
    // TS/SCSS project (its SourceFiles = that project's TS/SCSS file paths, so list_files works). C# wins on a name
    // collision. Built on demand from the snapshot — only the occasional project tools call it.
    private static Dictionary<string, ProjectIndex> UnifiedProjects(IndexSnapshot snap)
    {
        Dictionary<string, ProjectIndex> result = new(snap.Projects, StringComparer.OrdinalIgnoreCase);
        if (snap.TsProjects.Count == 0)
        {
            return result;
        }

        Dictionary<string, List<string>> tsFilesByProject = new(StringComparer.Ordinal);
        foreach (SourceFileIndex f in snap.AllSourceFiles)
        {
            if (f.Language == Language.CSharp)
            {
                continue;
            }

            if (!tsFilesByProject.TryGetValue(f.ProjectName, out List<string>? list))
            {
                list = [];
                tsFilesByProject[f.ProjectName] = list;
            }

            list.Add(f.SourceFilePath);
        }

        foreach (TsProjectInfo tp in snap.TsProjects)
        {
            if (result.ContainsKey(tp.Name))
            {
                continue; // a C# project with the same name wins
            }

            // Store project-relative, forward-slashed paths — matching the C# ProjectIndex.SourceFiles shape
            // (SnapshotBuilder.AddProjects) so list_files emits one consistent path form across languages.
            List<string> sourceFiles = tsFilesByProject.TryGetValue(tp.Name, out List<string>? abs)
                ? abs.Select(p => Path.GetRelativePath(tp.ProjectDirPath, p).Replace('\\', '/')).ToList()
                : [];
            result[tp.Name] = new ProjectIndex
            {
                Name = tp.Name,
                ProjectDirPath = tp.ProjectDirPath,
                SourceFiles = sourceFiles,
            };
        }

        return result;
    }

    /// <summary>Returns ALL types matching a name (there may be several — same short name in different namespaces,
    /// or a partial type split across files). Callers disambiguate; use <see cref="FindType"/> only when the first
    /// match is genuinely sufficient.</summary>
    public List<(TypeInfo Type, SourceFileIndex File)> FindTypes(string typeName)
    {
        IndexSnapshot snap = Current;
        List<(TypeInfo, SourceFileIndex)> matches = new();
        foreach (SourceFileIndex file in snap.AllSourceFiles)
        {
            foreach (TypeInfo type in file.Types)
            {
                if (type.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add((type, file));
                }
            }
        }

        return matches;
    }

    public (TypeInfo Type, SourceFileIndex File)? FindType(string typeName)
    {
        List<(TypeInfo Type, SourceFileIndex File)> all = FindTypes(typeName);
        return all.Count > 0 ? all[0] : null;
    }

    public List<(TypeInfo Type, SourceFileIndex File)> GetDerivedTypes(string typeName)
    {
        return Current.AllSourceFiles
            .SelectMany(file => file.Types
                .Where(t => t.BaseTypes is not null
                    && t.BaseTypes.Any(b => BaseNameMatches(b, typeName)))
                .Select(t => (t, file)))
            .ToList();
    }

    // A base-list entry may be generic ("IRepository&lt;TEntity, int&gt;") and/or namespace-qualified ("Data.IEntity").
    // Reduce to the simple type name before an exact compare so "IEntity" never matches "IEntityRepository".
    private static bool BaseNameMatches(string baseEntry, string typeName)
    {
        int lt = baseEntry.IndexOf('<');
        string noGeneric = lt < 0 ? baseEntry : baseEntry[..lt];
        int dot = noGeneric.LastIndexOf('.');
        string simple = (dot < 0 ? noGeneric : noGeneric[(dot + 1)..]).Trim();
        return simple.Equals(typeName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolve what a BARE (unqualified) type name binds to when written in <paramref name="fileQuery"/>, given
    /// that file's usings + aliases. Syntax+index based (no semantic model): an alias wins outright; otherwise a
    /// candidate type is IN SCOPE if its namespace is the file's own (or an ancestor) or one of its plain usings.
    /// Two in-scope candidates == the ambiguity the caller must see (e.g. one ORM's MyApp.Data.User
    /// vs another's MyApp.Data.Models.User). Out-of-scope candidates are also returned
    /// (so you can see the ".Models one exists but isn't imported"). Requires designer.cs to be indexed.
    /// </summary>
    public BareNameResolution ResolveBareName(string fileQuery, string identifier)
    {
        IndexSnapshot snap = Current;
        SourceFileIndex? file =
            (snap.SourceFilesByName.TryGetValue(fileQuery, out List<SourceFileIndex>? exact) ? exact[0] : null)
            ?? snap.SourceFilesByName.Where(kvp => kvp.Key.Contains(fileQuery, StringComparison.OrdinalIgnoreCase))
                   .Select(kvp => kvp.Value[0]).FirstOrDefault()
            ?? (snap.SourceFilesByPath.TryGetValue(fileQuery, out SourceFileIndex? byPath) ? byPath : null);

        if (file is null)
        {
            return new BareNameResolution(false, null, null, [], []);
        }

        if (file.UsingAliases.TryGetValue(identifier, out string? aliasTarget))
        {
            return new BareNameResolution(true, file.Namespace, aliasTarget, [], []);
        }

        // Namespaces a bare name can resolve into: the file's own namespace + each ancestor (C# searches
        // outward through nested namespaces) + every plain `using`.
        HashSet<string> inScopeNs = new(file.Usings, StringComparer.Ordinal);
        for (string ns = file.Namespace ?? ""; ns.Length > 0;)
        {
            inScopeNs.Add(ns);
            int dot = ns.LastIndexOf('.');
            ns = dot < 0 ? "" : ns[..dot];
        }

        List<BareNameCandidate> inScope = new();
        List<BareNameCandidate> outOfScope = new();
        foreach (SourceFileIndex f in snap.AllSourceFiles)
        {
            // resolve_bare_name reasons about C# usings/namespaces; TS/SCSS types have no C# binding rules and would
            // only add cross-language noise. Scope the candidate set to C#.
            if (f.Language != Language.CSharp)
            {
                continue;
            }

            foreach (TypeInfo t in f.Types)
            {
                if (!t.Name.Equals(identifier, StringComparison.Ordinal))
                {
                    continue;
                }

                string tns = t.Namespace ?? f.Namespace ?? "";
                string fqn = string.IsNullOrEmpty(tns) ? t.Name : $"{tns}.{t.Name}";
                bool via = inScopeNs.Contains(tns);
                string reason = via
                    ? (tns == file.Namespace ? "(same/enclosing namespace)" : $"using {tns};")
                    : "(namespace not imported)";
                (via ? inScope : outOfScope).Add(new BareNameCandidate(fqn, tns, f.SourceFilePath, reason));
            }
        }

        return new BareNameResolution(true, file.Namespace, null, inScope, outOfScope);
    }

    public IReadOnlyList<SourceFileIndex> AllSourceFiles => Current.AllSourceFiles;

    /// <summary>repo_map: PageRank-ranked, token-budgeted overview of the most important symbols. The
    /// symbol-mention graph is a Lazy on the current snapshot (built on first call, cached until the snapshot
    /// changes). <paramref name="focus"/> = file/symbol names for personalized (task-relevant) ranking.</summary>
    public string GetRepoMap(IReadOnlyList<string> focus, int tokenBudget, string? project)
    {
        IndexSnapshot snap = Current;
        List<RepoMap.RankedSymbol> ranked = snap.MentionGraph.Rank(focus, project);
        return RepoMap.Render(ranked, tokenBudget);
    }

    /// <summary>search_structural: find C# code by AST shape (a curated pattern vocabulary). Parses the indexed
    /// C# files on demand (parallel, generated files excluded); scope with <paramref name="project"/> to bound
    /// cost.</summary>
    public string SearchStructural(string pattern, string? project, int max, int perFileCap)
    {
        IReadOnlyList<SourceFileIndex> files = Current.AllSourceFiles;
        if (StructuralSearch.HasPattern(pattern))
        {
            return StructuralSearch.Search(_fileSystem, files, pattern, project, max, perFileCap);
        }

        if (TsStructuralSearch.HasPattern(pattern))
        {
            return TsStructuralSearch.Search(_fileSystem, files, pattern, project, max, perFileCap);
        }

        return $"Unknown structural pattern '{pattern}'.\n\nC# patterns:\n{StructuralSearch.PatternHelp()}\n\nTypeScript/TSX patterns:\n{TsStructuralSearch.PatternHelp()}";
    }

    /// <summary>check_dangling_references (TS/TSX): report imports never used, and used PascalCase identifiers that
    /// are neither imported nor declared here BUT are defined in another indexed TS file (a likely missing import —
    /// the post-merge build-break pattern). Index cross-check keeps the missing-import side high-precision.</summary>
    public string CheckDanglingReferences(string fileQuery)
    {
        IndexSnapshot snap = Current;
        SourceFileIndex? file =
            (snap.SourceFilesByName.TryGetValue(fileQuery, out List<SourceFileIndex>? exact) ? exact[0] : null)
            ?? snap.SourceFilesByName.Where(kvp => kvp.Key.Contains(fileQuery, StringComparison.OrdinalIgnoreCase))
                   .Select(kvp => kvp.Value[0]).FirstOrDefault()
            ?? (snap.SourceFilesByPath.TryGetValue(fileQuery, out SourceFileIndex? byPath) ? byPath : null);

        if (file is null)
        {
            return $"check_dangling_references: file '{fileQuery}' not indexed."
                + NameSuggester.DidYouMean(fileQuery, FileNames());
        }

        if (file.Language == Language.CSharp)
        {
            return "check_dangling_references: TypeScript/TSX only (for C# bare-name resolution use resolve_bare_name).";
        }

        TsReferenceCheck.Result? r = TsReferenceCheck.Analyze(_fileSystem, file.SourceFilePath);
        if (r is null)
        {
            return $"check_dangling_references: could not read '{file.FileName}'.";
        }

        HashSet<string> importNames = new(r.Imports.Select(i => i.Name), StringComparer.Ordinal);

        List<TsReferenceCheck.ImportBinding> unused = r.Imports
            .Where(i => !r.Used.ContainsKey(i.Name))
            .GroupBy(i => i.Name).Select(g => g.First())
            .OrderBy(i => i.Line).ToList();

        List<(string Name, int Line, List<string> Definers)> dangling = new();
        foreach (KeyValuePair<string, int> use in r.Used.OrderBy(k => k.Value))
        {
            string name = use.Key;
            if (name.Length < 2 || !char.IsUpper(name[0]))
            {
                continue; // PascalCase-only: locals/params are camelCase; keeps precision high
            }

            if (importNames.Contains(name) || r.Declared.Contains(name))
            {
                continue;
            }

            List<string> definers = snap.AllSourceFiles
                .Where(f => f.Language != Language.CSharp
                    && !f.SourceFilePath.Equals(file.SourceFilePath, StringComparison.OrdinalIgnoreCase)
                    && f.Types.Any(t => t.Name == name))
                .Select(f => f.FileName).Distinct().Take(5).ToList();
            if (definers.Count > 0)
            {
                dangling.Add((name, use.Value, definers));
            }
        }

        System.Text.StringBuilder sb = new();
        sb.AppendLine($"# Reference check: {file.FileName} ({file.ProjectName})");
        sb.AppendLine();
        sb.AppendLine($"Possibly MISSING imports ({dangling.Count}) — used but not imported (a project symbol defined elsewhere):");
        if (dangling.Count == 0)
        {
            sb.AppendLine("  (none)");
        }
        else
        {
            foreach ((string name, int line, List<string> definers) in dangling)
            {
                sb.AppendLine($"  {name}  [used line {line}] — defined in {string.Join(", ", definers)}");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"UNUSED imports ({unused.Count}) — imported but never referenced:");
        if (unused.Count == 0)
        {
            sb.AppendLine("  (none)");
        }
        else
        {
            foreach (TsReferenceCheck.ImportBinding i in unused)
            {
                sb.AppendLine($"  {i.Name}  from '{i.Module}'  [line {i.Line}]");
            }
        }

        sb.AppendLine();
        sb.AppendLine("(Heuristic: syntactic + PascalCase + index-confirmed. Misses camelCase/dynamic refs; a type-only import used only in a type position still counts as used.)");
        return sb.ToString();
    }

    /// <summary>call_hierarchy: heuristic C# caller/callee lookup over Roslyn syntax trees (name-based, no semantic
    /// model). direction = callers | callees | both. Parses on demand — callers scans all C# files (scope with
    /// <paramref name="project"/>); callees parses only the method's declaring file(s).</summary>
    public string GetCallHierarchy(string method, string direction, string? project, int max, int perFileCap, string language = "both")
    {
        IReadOnlyList<SourceFileIndex> files = Current.AllSourceFiles;
        string dir = (direction ?? "callers").Trim().ToLowerInvariant();

        string Cs() => dir switch
        {
            "callees" => CallHierarchy.Callees(_fileSystem, files, method, project, max),
            "both" => CallHierarchy.Callers(_fileSystem, files, method, project, max, perFileCap) + "\n\n" + CallHierarchy.Callees(_fileSystem, files, method, project, max),
            _ => CallHierarchy.Callers(_fileSystem, files, method, project, max, perFileCap),
        };
        string Ts() => dir switch
        {
            "callees" => TsCallHierarchy.Callees(_fileSystem, files, method, project, max),
            "both" => TsCallHierarchy.Callers(_fileSystem, files, method, project, max, perFileCap) + "\n\n" + TsCallHierarchy.Callees(_fileSystem, files, method, project, max),
            _ => TsCallHierarchy.Callers(_fileSystem, files, method, project, max, perFileCap),
        };

        return (language ?? "both").Trim().ToLowerInvariant() switch
        {
            "csharp" or "cs" or "c#" => Cs(),
            "typescript" or "ts" or "tsx" => Ts(),
            _ => $"## C#\n{Cs()}\n\n## TypeScript\n{Ts()}",
        };
    }

    // Candidate-name sets for did-you-mean suggestions (lock-free; each captures the current snapshot once and any
    // iterator closes over that captured immutable snapshot, never a re-read of Current).
    public IEnumerable<string> FileNames() => Current.SourceFilesByName.Keys;

    public IEnumerable<string> ProjectNames()
    {
        IndexSnapshot snap = Current;
        foreach (string name in snap.Projects.Keys)
        {
            yield return name;
        }

        foreach (TsProjectInfo tp in snap.TsProjects)
        {
            yield return tp.Name;
        }
    }

    public IEnumerable<string> TypeNames()
    {
        IndexSnapshot snap = Current;
        return TypeNamesFrom(snap);
    }

    public IEnumerable<string> SymbolNames(string? projectFilter = null)
    {
        IndexSnapshot snap = Current;
        return SymbolNamesFrom(snap, projectFilter);
    }

    private static IEnumerable<string> TypeNamesFrom(IndexSnapshot snap)
    {
        foreach (SourceFileIndex file in snap.AllSourceFiles)
        {
            foreach (TypeInfo type in file.Types)
            {
                yield return type.Name;
            }
        }
    }

    private static IEnumerable<string> SymbolNamesFrom(IndexSnapshot snap, string? projectFilter)
    {
        foreach (SourceFileIndex file in snap.AllSourceFiles)
        {
            if (projectFilter is not null && !file.ProjectName.Equals(projectFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (TypeInfo type in file.Types)
            {
                yield return type.Name;
                foreach (MemberInfo member in type.Members)
                {
                    yield return member.Name;
                }
            }
        }
    }

    /// <summary>Forward + reverse project references from the current snapshot's precomputed graph (lock-free,
    /// exact-name matching). Null if the project isn't indexed. One Volatile.Read so existence + both lists come
    /// from a single consistent snapshot.</summary>
    public ProjectDependencyInfo? GetProjectDependencyInfo(string projectName)
    {
        IndexSnapshot snap = Current;
        if (snap.Projects.TryGetValue(projectName, out ProjectIndex? proj))
        {
            ProjectDependencyGraph graph = snap.DependencyGraph;
            return new ProjectDependencyInfo(proj.Name, graph.GetReferences(proj.Name), graph.GetDependents(proj.Name));
        }

        // TS/SCSS project: discoverable, but no dependency edges are modeled (deferred, and the C#-only graph is
        // never fed TS projects). Degrade to empty lists — never null — so get_project_dependencies returns a valid
        // result rather than a misleading "not found".
        TsProjectInfo? ts = snap.TsProjects.FirstOrDefault(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
        return ts is not null ? new ProjectDependencyInfo(ts.Name, [], []) : null;
    }

    /// <summary>Project name → project directory, from the current snapshot (lock-free). Used by the grouped
    /// scan-output tools to compute repo-relative paths.</summary>
    public IReadOnlyDictionary<string, string> ProjectDirsByName()
    {
        IndexSnapshot snap = Current;
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        foreach (ProjectIndex p in snap.Projects.Values)
        {
            map[p.Name] = p.ProjectDirPath;
        }

        // Include TS/SCSS project dirs too, so the grouped scan-output tools (find_references / search_text) render
        // repo-relative paths for TS hits instead of collapsing every file to a bare filename. C# wins on collision.
        foreach (TsProjectInfo tp in snap.TsProjects)
        {
            map.TryAdd(tp.Name, tp.ProjectDirPath);
        }

        return map;
    }

    private static bool MatchesKindFilter(SymbolKind kind, string? kindFilter)
    {
        if (kindFilter is null)
        {
            return true;
        }

        return kindFilter.ToLowerInvariant() switch
        {
            "class" => kind is SymbolKind.Class or SymbolKind.StaticClass or SymbolKind.AbstractClass or SymbolKind.SealedClass,
            "struct" => kind is SymbolKind.Struct or SymbolKind.RecordStruct,
            "record" => kind is SymbolKind.Record or SymbolKind.RecordStruct,
            "method" => kind is SymbolKind.Method,
            "property" => kind is SymbolKind.Property,
            "field" => kind is SymbolKind.Field,
            "enum" => kind is SymbolKind.Enum,
            "interface" => kind is SymbolKind.Interface,
            "constructor" or "ctor" => kind is SymbolKind.Constructor,
            "event" => kind is SymbolKind.Event,
            _ => true
        };
    }

    private CacheData BuildCacheData(IndexSnapshot snap, Dictionary<string, long> timestamps)
    {
        return new CacheData
        {
            Projects = [.. snap.Projects.Values],
            SourceFiles = [.. snap.AllSourceFiles],
            FileTimestamps = timestamps,
            SchemaVersion = _csCache.CurrentSchemaVersion
        };
    }
}
