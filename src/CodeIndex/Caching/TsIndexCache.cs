using CodeIndex.Abstractions;
using CodeIndex.Indexing;
using CodeIndex.Models;
using MessagePack;

namespace CodeIndex.Caching;

/// <summary>
/// On-disk cache for the TypeScript/SCSS segment, stored SEPARATELY from the C# cache in
/// <c>index.ts.v{N}.cache</c>. Its schema version (<see cref="TsSchemaVersion"/>) is INDEPENDENT of the C# cache's
/// <see cref="IndexCache.CurrentSchemaVersion"/>: bumping one invalidates only its own file, never the other's.
/// Delegates all filesystem access to an injected <see cref="IFileSystem"/> through the shared
/// <see cref="AtomicCacheIo"/>, for the same torn-write-safe IO and reclaim window as the C# cache.
/// </summary>
public sealed class TsIndexCache : ITsIndexCache
{
    /// <summary>Bump when the TS/SCSS parse output or <see cref="TsCacheData"/> shape changes. Independent of the
    /// C# cache version — a mismatch here forces only a TS rebuild.</summary>
    public const int SchemaVersion = 1;

    private readonly IFileSystem _fileSystem;
    private readonly AtomicCacheIo _io;

    public TsIndexCache(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
        _io = new AtomicCacheIo(fileSystem);
    }

    /// <summary>TS cache schema version (independent of the C# cache); encoded into the cache filename.</summary>
    public int TsSchemaVersion => SchemaVersion;

    public string GetTsCachePath(string cacheDirectory) =>
        Path.Combine(cacheDirectory, $"index.ts.v{SchemaVersion}.cache");

    [MessagePackObject]
    public sealed class TsCacheData
    {
        [Key(0)] public required List<SourceFileIndex> SourceFiles { get; init; }
        [Key(1)] public required Dictionary<string, long> FileTimestamps { get; init; }
        [Key(2)] public required List<TsProjectInfo> TsProjects { get; init; }
        [Key(3)] public required AliasMap Aliases { get; init; }
        [Key(4)] public int SchemaVersion { get; init; }
    }

    /// <summary>Serialize the TS segment. Best-effort, never throws — a failed save only forfeits a warm start.</summary>
    public void Save(string cacheDirectory, TsSegment segment)
    {
        try
        {
            TsCacheData data = new()
            {
                SourceFiles = [.. segment.Files],
                FileTimestamps = ToPlainDictionary(segment.Timestamps),
                TsProjects = [.. segment.Projects],
                Aliases = segment.Aliases,
                SchemaVersion = SchemaVersion,
            };
            string path = GetTsCachePath(cacheDirectory);
            _io.WriteAtomic(path, MessagePackSerializer.Serialize(data));
            // Reap superseded TS caches only (index.ts.v*.cache), leaving the C# caches (index.v*.cache / index.cache)
            // strictly alone — the mirror image of the C# cache's reap. Same reclaim window.
            _io.ReapStale(cacheDirectory, ["index.ts.v*.cache"], path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CodeIndex] TS cache save failed ({ex.GetType().Name}: {ex.Message}) — continuing from the in-memory index.");
        }
    }

    /// <summary>Load the TS segment, or null (file missing, unreadable, or a different schema version → full TS rebuild).</summary>
    public TsSegment? Load(string cacheDirectory)
    {
        string path = GetTsCachePath(cacheDirectory);
        if (!_fileSystem.FileExists(path))
        {
            return null;
        }

        TsCacheData data;
        try
        {
            data = MessagePackSerializer.Deserialize<TsCacheData>(_io.ReadBytes(path));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CodeIndex] TS cache load failed ({ex.GetType().Name}: {ex.Message}) — falling back to a TS rebuild.");
            return null;
        }

        if (data.SchemaVersion != SchemaVersion)
        {
            Console.Error.WriteLine($"[CodeIndex] TS cache schema is v{data.SchemaVersion}, current is v{SchemaVersion} — discarding and rebuilding.");
            return null;
        }

        return new TsSegment(
            data.SourceFiles,
            ToCaseInsensitive(data.FileTimestamps),
            data.TsProjects,
            data.Aliases ?? AliasMap.Empty);
    }

    private static Dictionary<string, long> ToPlainDictionary(IReadOnlyDictionary<string, long> src)
    {
        Dictionary<string, long> d = new(src.Count);
        foreach (KeyValuePair<string, long> kv in src)
        {
            d[kv.Key] = kv.Value;
        }

        return d;
    }

    private static Dictionary<string, long> ToCaseInsensitive(Dictionary<string, long> src)
    {
        Dictionary<string, long> d = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, long> kv in src)
        {
            d[kv.Key] = kv.Value; // last-wins on a case-only duplicate
        }

        return d;
    }
}
