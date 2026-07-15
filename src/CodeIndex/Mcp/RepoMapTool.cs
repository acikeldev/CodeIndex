using System.ComponentModel;
using CodeIndex.Abstractions;
using ModelContextProtocol.Server;

namespace CodeIndex.Mcp;

[McpServerToolType]
public static class RepoMapTool
{
    [McpServerTool(Name = "repo_map")]
    [Description("Token-budgeted, PageRank-ranked overview of the MOST IMPORTANT symbols in the repo. Good FIRST call to orient in an unfamiliar codebase; pass focus= (comma-separated files/symbols) for the symbols most relevant to a task. Generated files excluded. Answers 'what matters here?' where search_symbol needs a name.")]
    public static string RepoMap(
        ICodeIndexStore index,
        [Description("Optional comma-separated files/symbols to focus ranking on; empty = global importance")] string? focus = null,
        [Description("Approx output token budget (default 2000, max 20000)")] int tokenBudget = 2000,
        [Description("Optional project filter; limits OUTPUT to that project, whole graph still informs ranking")] string? project = null)
    {
        IReadOnlyList<string> focusList = string.IsNullOrWhiteSpace(focus)
            ? []
            : focus.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return index.GetRepoMap(focusList, tokenBudget, project);
    }
}
