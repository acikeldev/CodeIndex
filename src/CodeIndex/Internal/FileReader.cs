using CodeIndex.Abstractions;

namespace CodeIndex.Internal;

/// <summary>
/// File line reader shared by tools that scan source files. Returns Success=false for
/// missing/locked/unreadable files so callers can keep scanning the rest of the tree
/// and report aggregate skip counts in their response.
/// </summary>
internal sealed class FileReader
{
    public readonly record struct ReadResult(string[]? Lines, bool Success, string? FailureReason);

    private readonly IFileSystem _fileSystem;

    public FileReader(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    public ReadResult ReadFileLines(string path)
    {
        if (!_fileSystem.FileExists(path))
        {
            return new ReadResult(null, Success: false, FailureReason: "missing");
        }

        try
        {
            return new ReadResult(_fileSystem.ReadAllLines(path), Success: true, FailureReason: null);
        }
        catch (Exception ex)
        {
            return new ReadResult(null, Success: false, FailureReason: ex.GetType().Name);
        }
    }
}
