namespace CodeIndex.Models;

public sealed class SymbolSearchResult
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required string Project { get; init; }
    public required string File { get; init; }
    public required string SourceFilePath { get; init; }
    public required int StartLine { get; init; }
    public required int LineCount { get; init; }
    public string? Namespace { get; init; }
    public string? Signature { get; init; }
    public string? ParentType { get; init; }
}
