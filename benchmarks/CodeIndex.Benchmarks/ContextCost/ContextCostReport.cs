using System.Text;
using CodeIndex.Abstractions;
using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Mcp;

namespace CodeIndex.Benchmarks.ContextCost;

/// <summary>
/// Context-cost benchmark. For a fixed suite of realistic code-intelligence questions it measures
/// how many tokens an agent must ingest to answer each one two ways:
///   • Baseline  — ripgrep + file reads (what an agent WITHOUT CodeIndex does), modelled by
///                 <see cref="BaselineAgent"/>, and
///   • CodeIndex — the actual MCP tool output, produced by calling the real tool code path in-process.
///
/// It prints a GitHub-flavoured markdown table. The numbers in the README come straight from here.
/// Reproduce:  <c>dotnet run -c Release -- context-cost</c>  (optionally pass a repo root as the next arg).
/// </summary>
internal static class ContextCostReport
{
    private sealed record Scenario(string Question, Func<string> Baseline, Func<string> CodeIndex);

    public static void Run(string[] args)
    {
        string repoRoot = ResolveRoot(args);

        IFileSystem fs = new FileSystem();
        CodeIndexStore index = new(fs, new IndexCache(fs), new TsIndexCache(fs), CodeIndexConfig.Default);
        index.Build(repoRoot);

        BaselineAgent baseline = new(repoRoot);
        List<Scenario> scenarios = BuildScenarios(index, fs, baseline);

        Console.WriteLine("# CodeIndex context-cost benchmark");
        Console.WriteLine();
        Console.WriteLine($"Repo: `{repoRoot}`  ");
        Console.WriteLine(
            $"Indexed: {index.ProjectCount} projects, {index.SourceFileCount} files, " +
            $"{index.TypeCount} types, {index.MemberCount} members  ");
        Console.WriteLine($"Baseline corpus: {baseline.FileCount} `.cs` files (build output excluded)");
        Console.WriteLine();
        Console.WriteLine("| # | Question a coding agent gets | Baseline (grep+read) | CodeIndex | Reduction |");
        Console.WriteLine("|---|------------------------------|---------------------:|----------:|----------:|");

        long totalBase = 0;
        long totalCi = 0;
        int n = 1;
        foreach (Scenario s in scenarios)
        {
            int b = Tokens.Estimate(s.Baseline());
            int c = Tokens.Estimate(s.CodeIndex());
            totalBase += b;
            totalCi += c;
            Console.WriteLine($"| {n++} | {s.Question} | {b:N0} | {c:N0} | −{Cut(b, c):F1}% |");
        }

        Console.WriteLine(
            $"| | **TOTAL ({scenarios.Count} tasks)** | **{totalBase:N0}** | **{totalCi:N0}** | " +
            $"**−{Cut(totalBase, totalCi):F1}%** |");
        Console.WriteLine();
        Console.WriteLine(
            "_Tokens = ceil(chars/4), applied identically to both sides. The baseline models a competent " +
            "grep+read agent and is charged conservatively (raw file content, no line-number prefixes), so " +
            "the reduction is a floor. Regenerate with `dotnet run -c Release -- context-cost`._");
    }

    private static List<Scenario> BuildScenarios(CodeIndexStore index, IFileSystem fs, BaselineAgent b)
    {
        return
        [
            // 1 — Enumerate a type's full API. grep finds only the declaration line, so the baseline must
            //     READ the whole file to see the members; CodeIndex returns just the signatures.
            new("List the full API of the `CodeIndexStore` class",
                () => b.Grep("class CodeIndexStore") + "\n" + b.ReadWhole("CodeIndexStore.cs"),
                () => GetTypeMembersTool.GetTypeMembers(index, "CodeIndexStore")),

            // 2 — Outline a large file. Baseline reads it whole; CodeIndex returns the structural outline.
            new("Outline the types & members of `RepositoryWatcher.cs`",
                () => b.ReadWhole("RepositoryWatcher.cs"),
                () => GetFileOutlineTool.GetFileOutline(index, fs, "RepositoryWatcher.cs", typesOnly: false)),

            // 3 — Read three related methods. Baseline greps each to locate it, then reads a 30-line window
            //     around the hit (the competent path); CodeIndex returns exactly the three bodies.
            new("Show the source of `IsGenerated`, `EstimateTokens`, `IsWithinRepo`",
                () => LocateAndRead(b, "IsGenerated(")
                    + LocateAndRead(b, "int EstimateTokens(")
                    + LocateAndRead(b, "bool IsWithinRepo("),
                () => GetContextBundleTool.GetContextBundle(index, fs, "IsGenerated,EstimateTokens,IsWithinRepo")),

            // 4 — Find implementors of an interface. There is no reliable single grep for "implements X",
            //     so the agent scans every usage of the name; CodeIndex returns the hierarchy directly.
            new("Which classes implement `IFileSystem`?",
                () => b.Grep("IFileSystem"),
                () => GetClassHierarchyTool.GetClassHierarchy(index, "IFileSystem")),

            // 5 — Who calls a method. HONESTY ANCHOR: grep is genuinely competitive (both emit one line per
            //     hit); CodeIndex still trims comments/substrings/generated files and groups by file.
            new("Find every use of the `ReadFileLines` method",
                () => b.Grep("ReadFileLines"),
                () => FindReferencesTool.FindReferences(index, fs, "ReadFileLines")),

            // 6 — Find a config key. HONESTY ANCHOR: another near-parity case for grep.
            new("Where is the `CODEINDEX_ROOT` env var read?",
                () => b.Grep("CODEINDEX_ROOT"),
                () => SearchTextTool.SearchText(index, fs, "CODEINDEX_ROOT")),

            // 7 — Orient in an unfamiliar repo. Baseline reads the 6 files an agent would open first
            //     (conservative — a real orientation pass opens more); CodeIndex ranks them in one call.
            new("New here — what are the most important types to read first?",
                () => b.ReadWhole("Program.cs")
                    + b.ReadWhole("CodeIndexStore.cs")
                    + b.ReadWhole("ICodeIndexStore.cs")
                    + b.ReadWhole("SolutionScanner.cs")
                    + b.ReadWhole("RepositoryWatcher.cs")
                    + b.ReadWhole("RepoMap.cs"),
                () => RepoMapTool.RepoMap(index, focus: null, tokenBudget: 2000, project: null)),

            // 8 — Find empty catch blocks. Grep dumps every catch (+context) for the agent to reason over;
            //     CodeIndex returns only the AST-confirmed empty ones.
            new("Are there any empty `catch` blocks?",
                () => b.Grep("catch", contextLines: 2),
                () => StructuralSearchTool.SearchStructural(index, "empty-catch", project: null, max: 40, perFileMax: 5)),

            // 9 — Understand a type AND its usage in one go. With grep you locate the class, read the whole file
            //     for its API, then grep the name for call sites; explain_symbol returns identity + members +
            //     source + references as one dossier — the dependent chain collapsed into a single call.
            new("Understand the `CodeIndexStore` class and where it's used",
                () => b.Grep("class CodeIndexStore") + "\n" + b.ReadWhole("CodeIndexStore.cs") + "\n" + b.Grep("CodeIndexStore"),
                () => ExplainSymbolTool.ExplainSymbol(index, fs, "CodeIndexStore")),

            // 10 — Which methods contain a phrase, and is each prod or test? grep prints the raw hit lines
            //     (prod/test IS derivable from the paths), but to name the ENCLOSING METHOD of each hit the
            //     agent must read a window around it; search_text tags every hit with its «Type.member» and
            //     prints a prod/test/generated tally in one call — the structural context grep cannot emit.
            new("Which methods contain the phrase \"not indexed\", and is each in production or test code?",
                () => GrepThenWindowEachHit(b, "not indexed"),
                () => SearchTextTool.SearchText(index, fs, "not indexed")),
        ];
    }

    private static string LocateAndRead(BaselineAgent b, string pattern)
    {
        string grep = b.Grep(pattern);
        (string Rel, int Line)? hit = b.Locate(pattern);
        string window = hit is { } h ? b.ReadRange(h.Rel, Math.Max(1, h.Line - 5), 30) : string.Empty;
        return grep + "\n" + window + "\n";
    }

    // Models "grep the token, then read a ~30-line window around EACH hit to recover its enclosing method" —
    // what an agent must do when the question asks which method each hit sits in. Prod/test is left to the
    // paths grep already prints; the per-hit windows are the extra cost search_text avoids by tagging inline.
    private static string GrepThenWindowEachHit(BaselineAgent b, string pattern)
    {
        string grep = b.Grep(pattern);
        StringBuilder sb = new();
        sb.Append(grep);
        foreach (string line in grep.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int firstColon = line.IndexOf(':');
            if (firstColon < 0)
            {
                continue;
            }

            int secondColon = line.IndexOf(':', firstColon + 1);
            if (secondColon < 0 || !int.TryParse(line[(firstColon + 1)..secondColon], out int hitLine))
            {
                continue;
            }

            string rel = line[..firstColon];
            sb.Append('\n').Append(b.ReadRange(rel, Math.Max(1, hitLine - 5), 30));
        }

        return sb.ToString();
    }

    private static double Cut(long baseline, long codeIndex) =>
        baseline == 0 ? 0 : (1.0 - ((double)codeIndex / baseline)) * 100.0;

    private static string ResolveRoot(string[] args)
    {
        // Invocation: context-cost [repoRoot]
        if (args.Length > 1 && Directory.Exists(args[1]))
        {
            return Path.GetFullPath(args[1]);
        }

        string? dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "CodeIndex.slnx")) || Directory.Exists(Path.Combine(dir, ".git")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException(
            "Could not locate the repo root (looked for CodeIndex.slnx / .git). Pass it explicitly: " +
            "dotnet run -c Release -- context-cost <path>");
    }
}
