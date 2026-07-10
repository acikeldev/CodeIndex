using System.ComponentModel;
using System.Text.RegularExpressions;
using CodeIndex.Abstractions;
using CodeIndex.Internal;
using CodeIndex.Models;
using ModelContextProtocol.Server;

namespace CodeIndex.Mcp;

[McpServerToolType]
public static class FindReferencesTool
{
    [McpServerTool(Name = "find_references")]
    [Description("Find all source lines referencing a symbol across indexed C# files. Smarter than grep: scoped to indexed files, word-boundary match so 'User' does not match 'UserId', skips '//' line comments. Output is GROUPED BY FILE with a leading file-frequency summary (breadth in one call) and true totals; generated files (designer.cs/Reference.cs/*.g.cs) are demoted — pass includeGenerated=true to promote them. Use to answer 'who calls X?' / 'where is X used?'.")]
    public static string FindReferences(
        ICodeIndexStore index,
        IFileSystem fileSystem,
        [Description("Symbol name to search for (e.g., 'GetItems', 'MergeRecords')")] string symbol,
        [Description("Optional project filter (e.g., 'MyApp.Core')")] string? project = null,
        [Description("Lines of context before and after each match, like grep -C (default 0)")] int contextLines = 0,
        [Description("Max sample lines to emit across all files (default 30)")] int max = 30,
        [Description("Max sample lines shown per file (default 3); the true per-file count is still reported")] int perFileMax = 3,
        [Description("Include generated files (designer.cs/Reference.cs/*.g.cs) in the ranked body (default false — they stay in the summary regardless)")] bool includeGenerated = false)
    {
        Regex wordBoundary = GetWordBoundaryRegex(symbol);

        // Candidate files, deduped by path (a file linked into >1 project is one physical file).
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<SourceFileIndex> candidates = new();
        foreach (SourceFileIndex f in index.AllSourceFiles)
        {
            if (project is not null && !f.ProjectName.Equals(project, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (seen.Add(f.SourceFilePath))
            {
                candidates.Add(f);
            }
        }

        ParallelScanner scanner = new(fileSystem);
        ParallelScanner.ScanOutcome outcome = scanner.Scan(
            candidates,
            // Swallow a per-line regex timeout (pathological line) so it can't surface as an AggregateException
            // from the parallel scan and abort the whole call — mirrors SearchTextTool's matcher.
            line =>
            {
                try
                {
                    return IsCodeReference(line, symbol, wordBoundary);
                }
                catch (RegexMatchTimeoutException)
                {
                    return false;
                }
            },
            (lines, i) => GroupedMatchOutput.RenderSample(lines, i, contextLines, symbol),
            perFileMax);

        if (outcome.Files.Count == 0)
        {
            string baseMessage = $"No references found for '{symbol}'.";
            return outcome.SkippedFiles > 0 ? baseMessage + $" ({outcome.SkippedFiles} file(s) could not be read.)" : baseMessage;
        }

        IReadOnlyDictionary<string, string> projectDirs = index.ProjectDirsByName();
        return GroupedMatchOutput.Format(outcome.Files, projectDirs, outcome.SkippedFiles,
            new GroupedMatchOutput.Options(symbol, "refs", max, perFileMax, includeGenerated));
    }

    internal static bool IsCodeReference(string line, string symbol, Regex wordBoundary)
    {
        if (!line.Contains(symbol, StringComparison.Ordinal))
        {
            return false;
        }

        if (line.TrimStart().StartsWith("//"))
        {
            return false;
        }

        // Word boundary stops 'User' matching 'UserId', 'IUserService', etc.
        return wordBoundary.IsMatch(line);
    }

    internal static Regex GetWordBoundaryRegex(string symbol) =>
        RegexCache.Get($@"\b{Regex.Escape(symbol)}\b", RegexOptions.None);
}
