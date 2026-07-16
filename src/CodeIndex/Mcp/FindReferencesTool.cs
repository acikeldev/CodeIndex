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
    [Description("Where a symbol is used across indexed C# files ('who calls X?'). Smarter than grep: word-boundary match so 'User' won't hit 'UserId', skips '//' comments. Grouped by file with a frequency summary and true totals; generated files (designer.cs/Reference.cs/*.g.cs) are demoted unless includeGenerated=true. A 'Usage:' line splits the refs into production/test/generated (by path) so the real production-usage count needs no follow-up filtering, and heuristically flags matches that are only inside a string literal or trailing comment as excluded (the headline ref total is unaffected).")]
    public static string FindReferences(
        ICodeIndexStore index,
        IFileSystem fileSystem,
        [Description("Symbol name")] string symbol,
        [Description("Optional project filter")] string? project = null,
        [Description("Lines of context around each match, like grep -C (default 0)")] int contextLines = 0,
        [Description("Max sample lines across all files (default 30)")] int max = 30,
        [Description("Max sample lines per file (default 3); true count still reported")] int perFileMax = 3,
        [Description("Promote generated files (designer.cs/Reference.cs/*.g.cs) into the ranked body (default false; they stay in the summary regardless)")] bool includeGenerated = false)
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
            (_, lines, i) => GroupedMatchOutput.RenderSample(lines, i, contextLines, symbol),
            perFileMax,
            // Classify each already-matched line: is every occurrence inside a string literal or a trailing
            // comment? Same timeout swallow as the matcher.
            line =>
            {
                try
                {
                    return IsStringOrCommentOnlyReference(line, wordBoundary);
                }
                catch (RegexMatchTimeoutException)
                {
                    return false;
                }
            });

        if (outcome.Files.Count == 0)
        {
            string baseMessage = $"No references found for '{symbol}'.";
            return outcome.SkippedFiles > 0 ? baseMessage + $" ({outcome.SkippedFiles} file(s) could not be read.)" : baseMessage;
        }

        IReadOnlyDictionary<string, string> projectDirs = index.ProjectDirsByName();
        return GroupedMatchOutput.Format(outcome.Files, projectDirs, outcome.SkippedFiles,
            new GroupedMatchOutput.Options(symbol, "refs", max, perFileMax, includeGenerated, ClassifyReferences: true));
    }

    /// <summary>
    /// Heuristic: true when EVERY word-boundary occurrence on the line is inside a double-quoted string literal or
    /// after a '//'. The caller only invokes this on lines already accepted by <see cref="IsCodeReference"/> (a
    /// full-line comment never reaches here), so it isolates trailing-comment-only and string-literal-only
    /// mentions. A line with even one code occurrence returns false (kept as a real reference), so mixed lines
    /// never underclaim. Handles char literals (so a '"' doesn't open a phantom string) and $-interpolation holes
    /// (code inside {…} counts as real). NOT authoritative — verbatim (@") and raw (""") strings can still fool it,
    /// which is why it only re-partitions the facet split and never changes the headline reference total.
    /// </summary>
    internal static bool IsStringOrCommentOnlyReference(string line, Regex wordBoundary)
    {
        MatchCollection matches = wordBoundary.Matches(line);
        if (matches.Count == 0)
        {
            return false;
        }

        foreach (Match m in matches)
        {
            if (!IsInsideStringOrComment(line, m.Index))
            {
                return false;
            }
        }

        return true;
    }

    // Single left-to-right scan to `index`. Tracks double-quoted string state (honouring \" and \\ escapes),
    // $-interpolation holes ({…} inside an interpolated string is real code), char literals (skipped so a '"'
    // char can't open a phantom string), and a '//' line-comment start outside a string. Verbatim/raw strings are
    // not modelled (documented limitation — facet-only impact).
    private static bool IsInsideStringOrComment(string line, int index)
    {
        bool inString = false;
        bool interpolated = false;
        int holeDepth = 0;   // brace depth inside an interpolated string; >0 means we're in a real-code hole
        for (int i = 0; i < index; i++)
        {
            char c = line[i];
            if (inString)
            {
                if (holeDepth > 0)
                {
                    if (c == '{')
                    {
                        holeDepth++;
                    }
                    else if (c == '}')
                    {
                        holeDepth--;
                    }

                    continue;
                }

                if (c == '\\')
                {
                    i++;   // skip the escaped char (\" \\ etc.)
                    continue;
                }

                if (interpolated && c == '{')
                {
                    if (i + 1 < line.Length && line[i + 1] == '{')
                    {
                        i++;   // {{ is a literal brace, still string
                        continue;
                    }

                    holeDepth = 1;   // entering a code hole
                    continue;
                }

                if (c == '"')
                {
                    inString = false;
                    interpolated = false;
                }

                continue;
            }

            if (c == '\'')
            {
                // Skip a char literal so a '"' inside it can't open a phantom string.
                i++;
                while (i < index && line[i] != '\'')
                {
                    if (line[i] == '\\')
                    {
                        i++;
                    }

                    i++;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
                interpolated = i > 0 && line[i - 1] == '$';
                continue;
            }

            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                return true;
            }
        }

        // A match reached while inside a code hole is real code, not "inside string".
        return inString && holeDepth == 0;
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
