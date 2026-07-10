namespace CodeIndex.Abstractions;

/// <summary>
/// Abstracts file system access so that parsing and indexing logic
/// can be tested without touching the real file system.
/// </summary>
public interface IFileSystem
{
    /// <summary>Returns all files matching <paramref name="pattern"/> under <paramref name="path"/>.</summary>
    IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption searchOption);

    /// <summary>Returns true if the file at <paramref name="path"/> exists.</summary>
    bool FileExists(string path);

    /// <summary>Returns true if the directory at <paramref name="path"/> exists.</summary>
    bool DirectoryExists(string path);

    /// <summary>Reads all text from <paramref name="path"/>.</summary>
    string ReadAllText(string path);

    /// <summary>Returns the last write time (UTC) of the file at <paramref name="path"/>.</summary>
    DateTime GetLastWriteTimeUtc(string path);

    /// <summary>Reads all bytes from <paramref name="path"/>.</summary>
    byte[] ReadAllBytes(string path);

    /// <summary>Writes <paramref name="bytes"/> to <paramref name="path"/>, overwriting any existing file.</summary>
    void WriteAllBytes(string path, byte[] bytes);

    /// <summary>Creates <paramref name="path"/> and any missing parent directories.</summary>
    void EnsureDirectoryExists(string path);

    /// <summary>
    /// Enumerates the immediate (non-recursive) entries of <paramref name="directory"/>, each carrying
    /// the metadata a single pruned tree-walk needs (kind, reparse-point flag, last-write ticks) so the
    /// scanners avoid a second stat call per entry. Returns nothing if the directory does not exist.
    /// </summary>
    IEnumerable<FileSystemEntry> EnumerateDirectoryEntries(string directory);

    /// <summary>Reads all lines of <paramref name="path"/> (splitting on CRLF/CR/LF, no trailing empty line).</summary>
    string[] ReadAllLines(string path);

    /// <summary>Deletes the file at <paramref name="path"/>.</summary>
    void DeleteFile(string path);

    /// <summary>
    /// Moves <paramref name="sourcePath"/> to <paramref name="destPath"/>, overwriting an existing
    /// destination when <paramref name="overwrite"/> is true. Used for torn-write-safe atomic cache writes.
    /// </summary>
    void MoveFile(string sourcePath, string destPath, bool overwrite);
}
