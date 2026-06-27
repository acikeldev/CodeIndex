namespace CodeIndex.Models;

/// <summary>
/// Index entry for a single C# source file.
/// </summary>
public sealed class SourceFileIndex
{
    /// <summary>File name without directory (e.g. "UserService.cs").</summary>
    public required string FileName { get; init; }

    /// <summary>Absolute path to the file.</summary>
    public required string FullPath { get; init; }

    /// <summary>Primary namespace declared in this file, or empty string if none.</summary>
    public required string Namespace { get; init; }

    /// <summary>All types declared in this file.</summary>
    public IReadOnlyList<TypeInfo> Types { get; init; } = [];

    /// <summary>UTC timestamp of the file when it was last indexed.</summary>
    public required DateTime IndexedAtUtc { get; init; }
}
