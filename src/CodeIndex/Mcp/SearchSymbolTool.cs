using System.ComponentModel;
using System.Text;
using System.Text.Json;
using CodeIndex.Abstractions;
using CodeIndex.Internal;
using CodeIndex.Models;
using ModelContextProtocol.Server;

namespace CodeIndex.Mcp;

[McpServerToolType]
public static class SearchSymbolTool
{
    private const int DefaultLimit = 50;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Compact output — this tool's value proposition is token efficiency. Indented JSON
        // wastes ~20% of tokens on whitespace the LLM doesn't need.
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [McpServerTool(Name = "search_symbol")]
    [Description("Find symbols by name across all indexed C# projects. Default returns compact one-line format. Use detail='full' for JSON with namespace, project, parentType, sourceFilePath. Use token_budget to cap response size.")]
    public static string SearchSymbol(
        ICodeIndexStore index,
        [Description("Search query - case-insensitive match against symbol names")] string query,
        [Description("Optional kind filter: class, struct, record, interface, enum, method, property, field, constructor, event")] string? kind = null,
        [Description("Optional project name filter (e.g., 'MyApp.Core')")] string? project = null,
        [Description("'compact' (default) = one-line per result, 'full' = JSON with all metadata")] string detail = "compact",
        [Description("Token budget cap. Results packed until budget exhausted. Overrides default limit of 50.")] int? tokenBudget = null)
    {
        if (kind is not null && !KindFilter.IsValidSymbolKind(kind))
        {
            return $"Unknown kind '{kind}'. Valid kinds: {KindFilter.ValidSymbolKindsList()}.";
        }

        List<SymbolSearchResult> results = index.SearchSymbol(query, kind, project);
        if (results.Count == 0)
        {
            return $"No symbols found matching '{query}'."
                + NameSuggester.DidYouMean(query, index.SymbolNames(project))
                + " Try search_text for string/comment/config matches, or resolve_bare_name for a bare type in a specific file.";
        }

        int total = results.Count;

        // When no token budget is provided, fall back to the default count limit so
        // huge result sets (e.g. query='Get') don't dump 10k+ rows on the caller.
        if (tokenBudget is null && results.Count > DefaultLimit)
        {
            results = results.Take(DefaultLimit).ToList();
        }

        string body = detail.Equals("full", StringComparison.OrdinalIgnoreCase)
            ? FormatFullResults(results, tokenBudget, out int emitted)
            : FormatCompactResults(results, tokenBudget, out emitted);

        // Header states the TRUE total so the caller can tell a complete list from a truncated one and knows how
        // to narrow — previously it silently cut at 50 with no signal (measured: definitive hits dropped unseen).
        string header = emitted < total
            ? $"{total} matches, showing {emitted} — narrow with kind= / project=, or set token_budget.\n"
            : $"{total} match{(total == 1 ? string.Empty : "es")}.\n";

        return header + body;
    }

    private static string FormatFullResults(List<SymbolSearchResult> results, int? tokenBudget, out int emitted)
    {
        if (tokenBudget is null)
        {
            emitted = results.Count;
            return JsonSerializer.Serialize(results, JsonOptions);
        }

        List<SymbolSearchResult> packed = [];
        int used = 0;
        foreach (SymbolSearchResult r in results)
        {
            string json = JsonSerializer.Serialize(r, JsonOptions);
            int tokens = Output.EstimateTokens(json);
            if (used + tokens > tokenBudget.Value && packed.Count > 0)
            {
                break;
            }
            packed.Add(r);
            used += tokens;
        }
        emitted = packed.Count;
        return JsonSerializer.Serialize(packed, JsonOptions);
    }

    private static string FormatCompactResults(List<SymbolSearchResult> results, int? tokenBudget, out int emitted)
    {
        StringBuilder sb = new();
        int tokensUsed = 0;
        emitted = 0;

        foreach (SymbolSearchResult r in results)
        {
            string sig = r.Signature ?? r.Name;
            string line = $"{sig} [{r.File}:{r.StartLine}+{r.LineCount}] ({r.Project})";

            if (tokenBudget is not null)
            {
                int lineTokens = Output.EstimateTokens(line);
                if (tokensUsed + lineTokens > tokenBudget.Value && sb.Length > 0)
                {
                    break;
                }
                tokensUsed += lineTokens;
            }

            sb.AppendLine(line);
            emitted++;
        }
        return sb.ToString().TrimEnd();
    }
}
