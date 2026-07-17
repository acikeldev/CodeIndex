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
    [Description("Index-scoped text/regex search that TAGS each hit with its enclosing Type.member and splits matches into production/test/generated — for config keys, error strings, SQL, TODOs when you want that structure (grep can't give it). For plain text where you just need the matching lines, the native grep tool is faster and you use it more fluently. Not for symbol names — use search_symbol. Generated files demoted unless includeGenerated=true.")]
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
            // Tag each hit with its enclosing Type.member (from the index) — the structural context that
            // differentiates search_text from a plain grep. Only for single-line samples (contextLines==0);
            // a context window keeps its raw form.
            (file, lines, i) =>
            {
                string rendered = GroupedMatchOutput.RenderSample(lines, i, contextLines, focus);
                if (contextLines > 0)
                {
                    return rendered;
                }

                // Map the scanner's 0-based line index into the parser's 1-based line space (i + 1). This assumes
                // both readers split lines identically — true for \r \n \r\n, but Roslyn's line table also counts
                // the rare Unicode separators U+0085/U+2028/U+2029 that File.ReadAllLines does not, so a .cs file
                // containing one can shift a boundary-adjacent hit's tag by a line. Annotation-only: the match
                // counts and the prod/test/generated facet never consult SymbolLocator.
                string? symbol = SymbolLocator.EnclosingSymbol(file, i + 1);
                return symbol is null ? rendered : $"{rendered}  «{symbol}»";
            },
            perFileMax);

        if (outcome.Files.Count == 0)
        {
            string baseMessage = $"No matches found for '{query}'.";
            return outcome.SkippedFiles > 0 ? baseMessage + $" ({outcome.SkippedFiles} file(s) could not be read.)" : baseMessage;
        }

        IReadOnlyDictionary<string, string> projectDirs = index.ProjectDirsByName();
        // ClassifyReferences: text matches split prod/test/generated (useful: "is this TODO in prod or test?").
        // No nonCode classifier passed — search_text legitimately matches strings/comments, so nothing is excluded.
        return GroupedMatchOutput.Format(outcome.Files, projectDirs, outcome.SkippedFiles,
            new GroupedMatchOutput.Options(query, "matches", max, perFileMax, includeGenerated, ClassifyReferences: true));
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
