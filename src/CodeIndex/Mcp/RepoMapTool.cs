using System.ComponentModel;
using CodeIndex.Abstractions;
using ModelContextProtocol.Server;

namespace CodeIndex.Mcp;

[McpServerToolType]
public static class RepoMapTool
{
    [McpServerTool(Name = "repo_map")]
    [Description("Ranked, token-budgeted overview of the MOST IMPORTANT symbols in the codebase — computed by PageRank over a symbol-mention graph (which files reference symbols defined in which other files). Great as a FIRST call to orient in an unfamiliar codebase, or pass focus= (comma-separated file or symbol names) to get the symbols most relevant to a specific task (personalized ranking). Generated files are excluded. Complements search_symbol (you know the name) — repo_map answers 'what matters here?'.")]
    public static string RepoMap(
        ICodeIndexStore index,
        [Description("Optional comma-separated file or symbol names to focus ranking on (personalized PageRank). Empty = global importance across the whole repo.")] string? focus = null,
        [Description("Approximate token budget for the output (default 2000; capped at 20000).")] int tokenBudget = 2000,
        [Description("Optional project filter (e.g., 'MyApp.Core') — limits the OUTPUT to that project; the whole graph still informs ranking.")] string? project = null)
    {
        IReadOnlyList<string> focusList = string.IsNullOrWhiteSpace(focus)
            ? []
            : focus.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return index.GetRepoMap(focusList, tokenBudget, project);
    }
}
