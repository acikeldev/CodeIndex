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
}
