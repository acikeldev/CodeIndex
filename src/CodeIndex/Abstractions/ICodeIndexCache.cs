using CodeIndex.Models;

namespace CodeIndex.Abstractions;

/// <summary>
/// Persists and restores a list of indexed projects to/from a binary cache file.
/// </summary>
public interface ICodeIndexCache
{
    /// <summary>
    /// Serializes <paramref name="projects"/> to the cache file, replacing any
    /// existing cache. Silently does nothing on I/O failure.
    /// </summary>
    void Save(IReadOnlyList<ProjectIndex> projects);

    /// <summary>
    /// Attempts to load the cache. Returns the cached projects on success, or
    /// <see langword="null"/> if the cache does not exist or is corrupt.
    /// </summary>
    IReadOnlyList<ProjectIndex>? TryLoad();
}
