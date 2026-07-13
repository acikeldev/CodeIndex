using CodeIndex.Abstractions;
using CodeIndex.Internal;

namespace CodeIndex.Mcp;

/// <summary>
/// Confidence-gated "next-hop" appendices. When a tool result has one obvious follow-up (the single symbol you
/// searched for → its source; a small file's outline → its full body), appending that follow-up now — a few
/// tokens re-billed at cache rates over the remaining turns — is far cheaper than the whole extra round-trip a
/// separate call would cost. Gated by <see cref="ICodeIndexStore.SpeculateEnabled"/>, a per-appendix token
/// budget, and structural confidence (single match / small file) so a wrong guess never dumps dead context.
/// </summary>
internal static class SpeculativeAppendix
{
    // A file whose whole body is small enough to be worth pre-fetching alongside its outline.
    private const int SmallFileMaxLines = 120;

    private const string SourceHeader =
        "\n\nSource (pre-fetched — no follow-up get_symbol_source needed):\n";
    private const string FileHeader =
        "\n\nFull source (small file, pre-fetched — no follow-up read needed):\n";

    /// <summary>
    /// Source for a single confident target, or empty when speculation is off, the file is missing, or the source
    /// would exceed the budget (then an explicit get_symbol_source is expected).
    /// </summary>
    public static string ForSource(ICodeIndexStore index, IFileSystem fileSystem, string sourceFilePath, int startLine, int lineCount)
    {
        if (!index.SpeculateEnabled || !fileSystem.FileExists(sourceFilePath))
        {
            return string.Empty;
        }

        string source = GetSymbolSourceTool.GetSymbolSource(index, fileSystem, sourceFilePath, startLine, lineCount);
        return WithinBudget(index, source) ? SourceHeader + source.TrimEnd() : string.Empty;
    }

    /// <summary>
    /// Whole body of a small file, or empty when speculation is off, the file is missing/large, or the body would
    /// exceed the budget.
    /// </summary>
    public static string ForSmallFile(ICodeIndexStore index, IFileSystem fileSystem, string sourceFilePath)
    {
        if (!index.SpeculateEnabled || !fileSystem.FileExists(sourceFilePath))
        {
            return string.Empty;
        }

        string[] lines = fileSystem.ReadAllLines(sourceFilePath);
        if (lines.Length == 0 || lines.Length > SmallFileMaxLines)
        {
            return string.Empty;
        }

        string source = GetSymbolSourceTool.GetSymbolSource(index, fileSystem, sourceFilePath, 1, lines.Length);
        return WithinBudget(index, source) ? FileHeader + source.TrimEnd() : string.Empty;
    }

    private static bool WithinBudget(ICodeIndexStore index, string content) =>
        !string.IsNullOrEmpty(content) && Output.EstimateTokens(content) <= index.SpeculateTokenBudget;
}
