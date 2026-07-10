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

    public byte[] ReadAllBytes(string path)
        => File.ReadAllBytes(path);

    public void WriteAllBytes(string path, byte[] bytes)
        => File.WriteAllBytes(path, bytes);

    public void EnsureDirectoryExists(string path)
        => Directory.CreateDirectory(path);

    public IEnumerable<FileSystemEntry> EnumerateDirectoryEntries(string directory)
    {
        DirectoryInfo dir = new(directory);
        if (!dir.Exists)
        {
            yield break;
        }

        // AttributesToSkip = 0 (not the default Hidden|System) is load-bearing: hidden/system source
        // files must still be indexed. Non-recursive — the scanner drives the descent itself so it can
        // prune node_modules/bin/obj and skip reparse points at each level.
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };

        foreach (FileSystemInfo info in dir.EnumerateFileSystemInfos("*", options))
        {
            bool isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
            bool isReparsePoint = (info.Attributes & FileAttributes.ReparsePoint) != 0;
            yield return new FileSystemEntry(
                info.FullName,
                info.Name,
                isDirectory,
                isReparsePoint,
                info.LastWriteTimeUtc.Ticks);
        }
    }

    public string[] ReadAllLines(string path)
        => File.ReadAllLines(path);

    public void DeleteFile(string path)
        => File.Delete(path);

    public void MoveFile(string sourcePath, string destPath, bool overwrite)
        => File.Move(sourcePath, destPath, overwrite);
}
