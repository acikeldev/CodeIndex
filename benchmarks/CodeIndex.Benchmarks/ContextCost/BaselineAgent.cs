using System.Text;
using System.Text.RegularExpressions;

namespace CodeIndex.Benchmarks.ContextCost;

/// <summary>
/// Models the context an LLM coding agent WITHOUT CodeIndex is forced to pull into its window to
/// answer a question, using only the primitives such an agent has: a ripgrep-style text search and
/// whole-/partial-file reads.
///
/// Every method returns the TEXT that would land in the agent's context — exactly what a CodeIndex
/// tool also returns — so both sides are measured in identical units (see <see cref="Tokens"/>).
///
/// Fairness rules — all of them bias AGAINST CodeIndex (they make the baseline look CHEAPER than it
/// really is), so the reported savings are a floor, not a best case:
///   • Corpus = the same *.cs set CodeIndex indexes, minus build output — i.e. what ripgrep sees.
///   • File reads are charged as RAW content: no "  123|" line-number prefixes (the real Read tool
///     adds them; we do not count them).
///   • Grep output is one "path:line: text" per hit — ripgrep's default, no surrounding noise.
/// </summary>
internal sealed class BaselineAgent
{
    private static readonly string[] ExcludedDirs =
    [
        "bin", "obj", ".git", ".vs", "node_modules", ".codeindex",
        "BenchmarkDotNet.Artifacts", "coverage-results", "nupkg", "TestResults",
    ];

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    private readonly string _repoRoot;
    private readonly List<string> _files; // absolute paths, sorted, exclusions applied

    public BaselineAgent(string repoRoot)
    {
        _repoRoot = Path.GetFullPath(repoRoot);
        _files = Directory
            .EnumerateFiles(_repoRoot, "*.cs", SearchOption.AllDirectories)
            .Where(p => !IsExcluded(p))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    public int FileCount => _files.Count;

    /// <summary>ripgrep-style search across the corpus: one "relpath:line: content" line per hit.</summary>
    public string Grep(string pattern, int contextLines = 0, bool ignoreCase = false, bool isRegex = false)
    {
        Regex rx = BuildRegex(pattern, isRegex, ignoreCase);
        StringBuilder sb = new();

        foreach (string file in _files)
        {
            string[]? lines = TryReadLines(file);
            if (lines is null)
            {
                continue;
            }

            string rel = Rel(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!rx.IsMatch(lines[i]))
                {
                    continue;
                }

                int from = Math.Max(0, i - contextLines);
                int to = Math.Min(lines.Length - 1, i + contextLines);
                for (int j = from; j <= to; j++)
                {
                    sb.Append(rel).Append(':').Append(j + 1).Append(": ").AppendLine(lines[j]);
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>First grep hit for a pattern: (repo-relative path, 1-based line), or null if none.</summary>
    public (string Rel, int Line)? Locate(string pattern, bool isRegex = false)
    {
        Regex rx = BuildRegex(pattern, isRegex, ignoreCase: false);
        foreach (string file in _files)
        {
            string[]? lines = TryReadLines(file);
            if (lines is null)
            {
                continue;
            }

            for (int i = 0; i < lines.Length; i++)
            {
                if (rx.IsMatch(lines[i]))
                {
                    return (Rel(file), i + 1);
                }
            }
        }

        return null;
    }

    /// <summary>Whole-file read (raw content, no line-number prefixes). Accepts a rel path or a bare filename.</summary>
    public string ReadWhole(string relOrName)
    {
        string abs = Resolve(relOrName);
        return File.Exists(abs) ? File.ReadAllText(abs) : string.Empty;
    }

    /// <summary>Bounded window read (1-based start) — models an agent reading around a grep hit.</summary>
    public string ReadRange(string relOrName, int startLine, int count)
    {
        string abs = Resolve(relOrName);
        if (!File.Exists(abs))
        {
            return string.Empty;
        }

        string[] lines = File.ReadAllLines(abs);
        int from = Math.Max(0, startLine - 1);
        int to = Math.Min(lines.Length, from + count);
        return from >= to ? string.Empty : string.Join('\n', lines[from..to]);
    }

    private static Regex BuildRegex(string pattern, bool isRegex, bool ignoreCase)
    {
        RegexOptions opts = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        return new Regex(isRegex ? pattern : Regex.Escape(pattern), opts, RegexTimeout);
    }

    private static string[]? TryReadLines(string file)
    {
        try
        {
            return File.ReadAllLines(file);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private bool IsExcluded(string path)
    {
        string rel = Path.GetRelativePath(_repoRoot, path);
        foreach (string seg in rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (ExcludedDirs.Contains(seg, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private string Rel(string abs) => Path.GetRelativePath(_repoRoot, abs).Replace('\\', '/');

    private string Resolve(string relOrName)
    {
        // Accept a repo-relative path, or a bare filename matched against the corpus.
        string direct = Path.GetFullPath(Path.Combine(_repoRoot, relOrName));
        if (File.Exists(direct))
        {
            return direct;
        }

        string name = Path.GetFileName(relOrName);
        return _files.FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase)) ?? direct;
    }
}
