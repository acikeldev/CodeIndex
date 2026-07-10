using CodeIndex.Abstractions;

namespace CodeIndex.Caching;

/// <summary>
/// Torn-write-safe cache IO shared by the C# and TS cache segments. A write goes to a per-process temp file and
/// then atomically replaces the target, so a concurrent reader (another session starting up) always sees a whole
/// old-or-new file — never a half-written one. Reaping deletes a superseded cache file only after it has been
/// untouched past <see cref="StaleCacheReclaimAge"/>, so a different tool version sharing the repo — which re-saves
/// its own cache each run — is never robbed of a live cache (which would thrash both into perpetual full rebuilds).
/// </summary>
internal sealed class AtomicCacheIo
{
    /// <summary>How long a superseded cache must be untouched before it may be reclaimed.</summary>
    public static readonly TimeSpan StaleCacheReclaimAge = TimeSpan.FromMinutes(30);

    private readonly IFileSystem _fileSystem;

    public AtomicCacheIo(IFileSystem fileSystem) => _fileSystem = fileSystem;

    /// <summary>Write bytes via a per-process temp file + atomic replace. Creates the directory. Throws on failure —
    /// callers decide whether to swallow.</summary>
    public void WriteAtomic(string path, byte[] bytes)
    {
        string? dir = Path.GetDirectoryName(path);
        if (dir is not null)
        {
            _fileSystem.EnsureDirectoryExists(dir);
        }

        string tmp = $"{path}.tmp-{Environment.ProcessId}";
        _fileSystem.WriteAllBytes(tmp, bytes);
        _fileSystem.MoveFile(tmp, path, overwrite: true);
    }

    /// <summary>Read a cache file's raw bytes.</summary>
    public byte[] ReadBytes(string path) => _fileSystem.ReadAllBytes(path);

    /// <summary>
    /// Reap superseded cache files matching any of <paramref name="globs"/> in <paramref name="directory"/>, skipping
    /// <paramref name="currentPath"/> and any in-flight <c>.tmp-</c> file, once older than the reclaim window.
    /// Best-effort — a locked / racing file is left alone. Callers pass ONLY their own segment's globs so the two
    /// segments never reap each other's live cache.
    /// </summary>
    public void ReapStale(string directory, IEnumerable<string> globs, string currentPath)
    {
        if (!_fileSystem.DirectoryExists(directory))
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        foreach (string glob in globs)
        {
            foreach (string stale in _fileSystem.EnumerateFiles(directory, glob, SearchOption.TopDirectoryOnly))
            {
                if (stale.Equals(currentPath, StringComparison.OrdinalIgnoreCase) ||
                    stale.Contains(".tmp-", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    if (now - _fileSystem.GetLastWriteTimeUtc(stale) > StaleCacheReclaimAge)
                    {
                        _fileSystem.DeleteFile(stale);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // locked / in use by another session — leave it
                }
            }
        }
    }
}
