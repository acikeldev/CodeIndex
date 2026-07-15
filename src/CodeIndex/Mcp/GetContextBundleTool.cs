using System.ComponentModel;
using System.Text;
using CodeIndex.Abstractions;
using CodeIndex.Internal;
using CodeIndex.Models;
using ModelContextProtocol.Server;

namespace CodeIndex.Mcp;

[McpServerToolType]
public static class GetContextBundleTool
{
    [McpServerTool(Name = "get_context_bundle")]
    [Description("Full source for multiple symbols in one call; dedupes symbols that share a file. Pass comma-separated names.")]
    public static string GetContextBundle(
        ICodeIndexStore index,
        IFileSystem fileSystem,
        [Description("Comma-separated symbol names")] string symbols)
    {
        string[] names = symbols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0)
        {
            return "No symbol names provided.";
        }

        // Resolve each symbol to its best match
        IReadOnlyDictionary<string, string> projectDirs = index.ProjectDirsByName();
        StringBuilder sb = new();
        HashSet<string> filesRead = new(StringComparer.OrdinalIgnoreCase);
        int found = 0;

        foreach (string name in names)
        {
            List<SymbolSearchResult> results = index.SearchSymbol(name, null, null);
            if (results.Count == 0)
            {
                sb.AppendLine($"# {name} — not found");
                sb.AppendLine();
                continue;
            }

            // Take the best match (first result = highest rank)
            SymbolSearchResult best = results[0];
            found++;

            sb.AppendLine($"# {best.Signature ?? best.Name} [{GroupedMatchOutput.RelPath(best.SourceFilePath, best.Project, projectDirs)}:{best.StartLine}+{best.LineCount}] ({best.Project})");
            if (best.Namespace is not null)
            {
                sb.AppendLine($"Namespace: {best.Namespace}");
            }

            // Read source — deduplicate file reads
            string fileKey = $"{best.SourceFilePath}:{best.StartLine}:{best.LineCount}";
            if (filesRead.Contains(fileKey))
            {
                sb.AppendLine("(source already included above)");
                sb.AppendLine();
                continue;
            }
            filesRead.Add(fileKey);

            if (fileSystem.FileExists(best.SourceFilePath))
            {
                string[] allLines = fileSystem.ReadAllLines(best.SourceFilePath);
                int start = Math.Max(0, best.StartLine - 1);
                int count = Math.Min(best.LineCount, allLines.Length - start);

                for (int i = start; i < start + count; i++)
                {
                    sb.AppendLine($"{i + 1,5}| {allLines[i]}");
                }
            }
            else
            {
                sb.AppendLine($"Source file not found: {best.SourceFilePath}");
            }

            sb.AppendLine();
        }

        if (found == 0)
        {
            return $"No symbols found for any of: {symbols}";
        }

        return sb.ToString();
    }
}
