using CodeIndex.Indexing;

namespace CodeIndex.Abstractions;

/// <summary>
/// The TypeScript/SCSS-segment on-disk cache. Its schema version is INDEPENDENT of the C# cache, so bumping one
/// never invalidates the other, and their versioned files coexist. Save is best-effort and never throws. All
/// members take the cache DIRECTORY and derive the versioned filename internally.
/// </summary>
public interface ITsIndexCache
{
    /// <summary>TS cache schema version (independent of the C# cache); encoded into the cache filename.</summary>
    int TsSchemaVersion { get; }

    /// <summary>The versioned TS cache file path inside <paramref name="cacheDirectory"/>.</summary>
    string GetTsCachePath(string cacheDirectory);

    /// <summary>Loads the TS segment, or <see langword="null"/> if absent / unreadable / a different schema.</summary>
    TsSegment? Load(string cacheDirectory);

    /// <summary>Atomically persists <paramref name="segment"/>. Best-effort — never throws.</summary>
    void Save(string cacheDirectory, TsSegment segment);
}
