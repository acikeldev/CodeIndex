using System.Diagnostics.CodeAnalysis;

namespace CodeIndex.Abstractions;

/// <summary>
/// Production implementation of <see cref="IFileSystem"/> that delegates
/// directly to <see cref="System.IO"/>.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class FileSystem : IFileSystem
{
    public IEnumerable<string> EnumerateFiles(string path, string pattern, SearchOption searchOption)
        => Directory.EnumerateFiles(path, pattern, searchOption);

    public bool FileExists(string path)
        => File.Exists(path);

    public bool DirectoryExists(string path)
        => Directory.Exists(path);

    public string ReadAllText(string path)
        => File.ReadAllText(path);

    public DateTime GetLastWriteTimeUtc(string path)
        => File.GetLastWriteTimeUtc(path);
}
