using System.ComponentModel;
using System.Text;
using CodeIndex.Abstractions;
using CodeIndex.Internal;
using ModelContextProtocol.Server;

namespace CodeIndex.Mcp;

[McpServerToolType]
public static class GetSymbolSourceTool
{
    [McpServerTool(Name = "get_symbol_source")]
    [Description("Read source lines from a file, using line numbers from search_symbol or get_file_outline. Only indexed or in-repo files can be read.")]
    public static string GetSymbolSource(
        ICodeIndexStore index,
        IFileSystem fileSystem,
        [Description("File path (from search results) or filename")] string file,
        [Description("Start line (1-based)")] int startLine,
        [Description("Lines to read")] int lineCount)
    {
        string filePath = file;
        if (!Path.IsPathRooted(file) || !fileSystem.FileExists(file))
        {
            string? resolved = index.ResolveSourceFilePath(file);
            if (resolved is not null)
            {
                filePath = resolved;
            }
        }

        // Security: the arg comes from an agent (possibly prompt-injected). Only read a file that is INDEXED, or
        // one that resolves to a path INSIDE the repo root — never an arbitrary absolute path like ~/.aws/credentials.
        string full = Path.GetFullPath(filePath);
        bool allowed = index.IsIndexedPath(full) || index.IsIndexedPath(filePath) || PathSecurity.IsWithinRepo(full, index.RepoRoot);
        if (!allowed)
        {
            return $"Refused: '{file}' is not an indexed file and is outside the repository. Pass a filename or path from search results.";
        }

        if (!fileSystem.FileExists(full))
        {
            return $"Source file not found: {full}";
        }

        string[] allLines = fileSystem.ReadAllLines(full);
        int start = Math.Max(0, startLine - 1); // convert to 0-based
        int count = Math.Min(lineCount, allLines.Length - start);

        if (start >= allLines.Length)
        {
            return $"Start line {startLine} is beyond file length ({allLines.Length} lines).";
        }

        StringBuilder sb = new();
        for (int i = start; i < start + count; i++)
        {
            sb.AppendLine($"{i + 1,5}| {allLines[i]}");
        }

        return sb.ToString();
    }

    // The non-source sentinels GetSymbolSource can return (refused path / missing file / start-past-EOF). Callers
    // that embed the result AS source — the dossiers and the speculative appendix — must check this first so an
    // error string is never presented as code (e.g. on a stale index after the file shrank or was deleted).
    internal static bool IsErrorResult(string result) =>
        result.StartsWith("Refused:", StringComparison.Ordinal)
        || result.StartsWith("Source file not found:", StringComparison.Ordinal)
        || result.StartsWith("Start line ", StringComparison.Ordinal);
}
