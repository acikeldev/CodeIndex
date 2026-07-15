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
    [Description("Find symbols by name across indexed C# projects. Default is compact one-line rows; detail='full' returns JSON with namespace, project, parentType, sourceFilePath. token_budget caps response size.")]
    public static string SearchSymbol(
        ICodeIndexStore index,
        IFileSystem fileSystem,
        [Description("Search query; case-insensitive match on symbol names")] string query,
        [Description("Optional kind filter: class, struct, record, interface, enum, method, property, field, constructor, event")] string? kind = null,
        [Description("Optional project filter")] string? project = null,
        [Description("'compact' (default) = one line per result; 'full' = JSON with all metadata")] string detail = "compact",
        [Description("Token budget for result rows; packed until exhausted (overrides the 50-row cap). When set, the speculative source appendix is skipped")] int? tokenBudget = null)
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

        bool full = detail.Equals("full", StringComparison.OrdinalIgnoreCase);
        string body = full
            ? FormatFullResults(results, tokenBudget, out int emitted)
            : FormatCompactResults(results, tokenBudget, out emitted);

        // Header states the TRUE total so the caller can tell a complete list from a truncated one and knows how
        // to narrow — previously it silently cut at 50 with no signal (measured: definitive hits dropped unseen).
        string header = emitted < total
            ? $"{total} matches, showing {emitted} — narrow with kind= / project=, or set token_budget.\n"
            : $"{total} match{(total == 1 ? string.Empty : "es")}.\n";

        // Footer + speculative source only on the compact (LLM-facing) body — never pollute detail=full's JSON.
        if (full)
        {
            return header + body;
        }

        // Exactly one match is the high-confidence signal that the source is the next thing the agent wants —
        // but skip speculation when the caller set an explicit token_budget (they're signalling cost-consciousness,
        // and the appendix is governed by SpeculateTokenBudget, not their cap).
        string appendix = total == 1 && tokenBudget is null
            ? SpeculativeAppendix.ForSource(index, fileSystem, results[0].SourceFilePath, results[0].StartLine, results[0].LineCount)
            : string.Empty;

        return header + body + appendix + Steering.SymbolDossierHint;
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
