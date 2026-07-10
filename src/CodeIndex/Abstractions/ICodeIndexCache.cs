using CodeIndex.Models;

namespace CodeIndex.Abstractions;

/// <summary>
/// The C#-segment on-disk index cache (independently versioned MessagePack). Save is best-effort and never
/// throws; Load returns <see langword="null"/> on a missing / corrupt / schema-mismatched file.
/// </summary>
public interface ICodeIndexCache
{
    /// <summary>Cache schema version this build reads/writes; encoded into the cache filename.</summary>
    int CurrentSchemaVersion { get; }

    /// <summary>The versioned cache file path inside <paramref name="cacheDirectory"/>.</summary>
    string GetCachePath(string cacheDirectory);

    /// <summary>Loads the cache, or <see langword="null"/> if absent / unreadable / a different schema.</summary>
    CacheData? Load(string cachePath);

    /// <summary>Atomically persists <paramref name="data"/>. Best-effort — never throws.</summary>
    void Save(string cachePath, CacheData data);
}
