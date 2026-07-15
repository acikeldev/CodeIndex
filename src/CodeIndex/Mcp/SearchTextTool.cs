using System.ComponentModel;
using System.Text.RegularExpressions;
using CodeIndex.Abstractions;
using CodeIndex.Internal;
using CodeIndex.Models;
using ModelContextProtocol.Server;

namespace CodeIndex.Mcp;

[McpServerToolType]
public static class SearchTextTool
{
    [McpServerTool(Name = "search_text")]
    [Description("Full-text/regex search across indexed C# files — for config keys, error messages, SQL, string literals, TODOs: anything that isn't a symbol name (use search_symbol for those). Grouped by file with a frequency summary and true totals; generated files (designer.cs/Reference.cs/*.g.cs) demoted unless includeGenerated=true.")]
    public static string SearchText(
        ICodeIndexStore index,
        IFileSystem fileSystem,
        [Description("Text or regex to search for")] string query,
        [Description("Optional project filter")] string? project = null,
        [Description("Treat query as regex (default false)")] bool isRegex = false,
        [Description("Case-insensitive (default false)")] bool ignoreCase = false,
        [Description("Lines of context around each match, like grep -C (default 0)")] int contextLines = 0,
        [Description("Max sample lines across all files (default 30)")] int max = 30,
        [Description("Max sample lines per file (default 3); true count still reported")] int perFileMax = 3,
        [Description("Promote generated files into the ranked body (default false; they stay in the summary regardless)")] bool includeGenerated = false)
    {
        Func<string, bool> matcher = BuildMatcher(query, isRegex, ignoreCase, out string? error);
        if (error is not null)
        {
            return error;
        }

        // Clip emitted lines around the literal query when we have one (non-regex) so a single huge line
        // (e.g. an embedded SQL string) can't dominate the response.
        string? focus = isRegex ? null : query;

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
            matcher,
            (lines, i) => GroupedMatchOutput.RenderSample(lines, i, contextLines, focus),
            perFileMax);

        if (outcome.Files.Count == 0)
        {
            string baseMessage = $"No matches found for '{query}'.";
            return outcome.SkippedFiles > 0 ? baseMessage + $" ({outcome.SkippedFiles} file(s) could not be read.)" : baseMessage;
        }

        IReadOnlyDictionary<string, string> projectDirs = index.ProjectDirsByName();
        return GroupedMatchOutput.Format(outcome.Files, projectDirs, outcome.SkippedFiles,
            new GroupedMatchOutput.Options(query, "matches", max, perFileMax, includeGenerated));
    }

    private static Func<string, bool> BuildMatcher(string query, bool isRegex, bool ignoreCase, out string? error)
    {
        error = null;
        if (isRegex)
        {
            Regex regex;
            try
            {
                regex = RegexCache.Get(query, ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            }
            catch (RegexParseException ex)
            {
                error = $"Invalid regex: {ex.Message}";
                return _ => false;
            }
            // Swallow a per-line match timeout (pathological line) so one slow line can't abort the whole scan.
            return line =>
            {
                try { return regex.IsMatch(line); }
                catch (RegexMatchTimeoutException) { return false; }
            };
        }

        StringComparison comparison = ignoreCase
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return line => line.Contains(query, comparison);
    }
}
