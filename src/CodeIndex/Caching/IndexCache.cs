using CodeIndex.Abstractions;
using CodeIndex.Models;
using MessagePack;

namespace CodeIndex.Caching;

/// <summary>
/// The C#-segment on-disk index cache (independently versioned MessagePack). Save is best-effort and never
/// throws; Load returns <see langword="null"/> on a missing / corrupt / schema-mismatched file. Delegates all
/// filesystem access to an injected <see cref="IFileSystem"/> through the shared <see cref="AtomicCacheIo"/>.
/// </summary>
public sealed class IndexCache : ICodeIndexCache
{
    /// <summary>
    /// Bump whenever the shape of CacheData or any [MessagePackObject] model changes in a way
    /// that would make older caches produce wrong results (new SymbolKind values, new TypeInfo
    /// fields, changed semantics, etc.). Older caches with a different version are discarded
    /// and a full rebuild is performed.
    /// </summary>
    // v3: SourceFileIndex gained Usings + UsingAliases (resolve_bare_name)
    // v4: nested types indexed; per-type Namespace; BaseTypes as List<string>; multi-declarator fields split;
    //     Struct/Record/RecordStruct kinds; type-less files retained.
    public const int SchemaVersion = 4;

    private readonly IFileSystem _fileSystem;
    private readonly AtomicCacheIo _io;

    public IndexCache(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
        _io = new AtomicCacheIo(fileSystem);
    }

    /// <summary>Cache schema version this build reads/writes; encoded into the cache filename.</summary>
    public int CurrentSchemaVersion => SchemaVersion;

    /// <summary>
    /// Versioned cache file inside <paramref name="cacheDirectory"/> (e.g. <c>index.v{N}.cache</c>). Encoding the
    /// schema version in the FILENAME lets two installed tool versions coexist instead of clobbering one shared
    /// file and thrashing each other into perpetual full rebuilds. A version this build cannot read simply isn't
    /// found → clean full build. The directory is chosen by CodeIndexConfig (repo dir by default, or user profile).
    /// </summary>
    public string GetCachePath(string cacheDirectory) =>
        Path.Combine(cacheDirectory, $"index.v{SchemaVersion}.cache");

    public record DeltaResult(
        List<string> ChangedFiles,
        List<string> RemovedFiles,
        bool IsFullRebuild
    );

    public static DeltaResult ComputeDelta(CacheData? cached, Dictionary<string, long> currentTimestamps)
        => ComputeDelta(cached?.FileTimestamps, currentTimestamps);

    /// <summary>
    /// Delta against any timestamp baseline (the disk cache's, or a prior in-memory snapshot's — snapshot-swap
    /// computes deltas without a disk load). Path keys are compared case-INSENSITIVELY: the same file can appear
    /// with different casing between runs (launch cwd casing, drive-letter case, or one tool launched from `c:\`
    /// and another from `C:\`), and an ordinal compare would flag every file as removed+changed and force a full
    /// reparse each time.
    /// </summary>
    public static DeltaResult ComputeDelta(IReadOnlyDictionary<string, long>? baseline, Dictionary<string, long> currentTimestamps)
    {
        if (baseline is null || baseline.Count == 0)
        {
            return new DeltaResult([], [], IsFullRebuild: true);
        }

        Dictionary<string, long> baselineCI = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, long> kv in baseline)
        {
            baselineCI[kv.Key] = kv.Value; // last-wins; avoids the ctor throwing on a case-only duplicate
        }

        List<string> removed = baselineCI.Keys
            .Where(f => !currentTimestamps.ContainsKey(f))
            .ToList();

        List<string> changed = currentTimestamps
            .Where(kvp => !baselineCI.TryGetValue(kvp.Key, out long cachedTicks) || kvp.Value != cachedTicks)
            .Select(kvp => kvp.Key)
            .ToList();

        return new DeltaResult(changed, removed, IsFullRebuild: false);
    }

    /// <summary>
    /// Serialize the index to disk. Best-effort and never throws: a failed save only forfeits a startup optimization,
    /// it must not crash the server. Writes to a per-process temp file then atomically renames, so a concurrent
    /// reader (another session starting up) always sees a whole old-or-new file, never a half-written one — the
    /// previous non-atomic WriteAllBytes could hand a torn file to a second session and kill it at startup.
    /// </summary>
    public void Save(string cacheDirectory, CacheData data)
    {
        string cachePath = GetCachePath(cacheDirectory);
        try
        {
            byte[] bytes = MessagePackSerializer.Serialize(data);
            _io.WriteAtomic(cachePath, bytes);
            _io.ReapStale(cacheDirectory, ["index.v*.cache", "index.cache"], cachePath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CodeIndex] Cache save failed ({ex.GetType().Name}: {ex.Message}) — continuing from the in-memory index.");
        }
    }

    public CacheData? Load(string cacheDirectory)
    {
        string cachePath = GetCachePath(cacheDirectory);
        if (!_fileSystem.FileExists(cachePath))
        {
            return null;
        }

        CacheData? data;
        try
        {
            data = MessagePackSerializer.Deserialize<CacheData>(_io.ReadBytes(cachePath));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CodeIndex] Cache load failed ({ex.GetType().Name}: {ex.Message}) — falling back to full rebuild.");
            return null;
        }

        if (data.SchemaVersion != SchemaVersion)
        {
            Console.Error.WriteLine($"[CodeIndex] Cache schema is v{data.SchemaVersion}, current is v{SchemaVersion} — discarding and rebuilding.");
            return null;
        }

        return data;
    }
}
