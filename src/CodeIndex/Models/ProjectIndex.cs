namespace CodeIndex.Models;

/// <summary>
/// Index entry for a single C# project (.csproj).
/// </summary>
public sealed class ProjectIndex
{
    /// <summary>Project name derived from the .csproj filename (e.g. "MyApp.Services").</summary>
    public required string Name { get; init; }

    /// <summary>Absolute path to the project directory.</summary>
    public required string Directory { get; init; }

    /// <summary>Absolute path to the .csproj file.</summary>
    public required string ProjectFilePath { get; init; }

    /// <summary>All indexed source files belonging to this project.</summary>
    public IReadOnlyList<SourceFileIndex> SourceFiles { get; init; } = [];
}
