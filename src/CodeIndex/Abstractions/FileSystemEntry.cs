namespace CodeIndex.Abstractions;

/// <summary>
/// One entry (file or directory) returned by <see cref="IFileSystem.EnumerateDirectoryEntries"/>,
/// carrying the metadata a single-level pruned walk needs without a second stat call: the kind of
/// entry, whether it is a reparse point (symlink/junction — a cycle guard), and its last-write time.
/// </summary>
public readonly record struct FileSystemEntry(
    string FullPath,
    string Name,
    bool IsDirectory,
    bool IsReparsePoint,
    long LastWriteTimeUtcTicks);
