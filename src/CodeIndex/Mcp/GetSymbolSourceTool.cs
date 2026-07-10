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
    [Description("Read actual source code lines from a file. Use line numbers from search_symbol or get_file_outline results. Only files that are indexed or inside the repository can be read.")]
    public static string GetSymbolSource(
        ICodeIndexStore index,
        IFileSystem fileSystem,
        [Description("Source file path (from search results) or filename")] string file,
        [Description("Starting line number (1-based)")] int startLine,
        [Description("Number of lines to read")] int lineCount)
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
}
