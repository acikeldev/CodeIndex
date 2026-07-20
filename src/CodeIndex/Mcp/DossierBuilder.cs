using System.Text;
using CodeIndex.Abstractions;
using CodeIndex.Internal;

namespace CodeIndex.Mcp;

/// <summary>
/// Shared assembly logic for the one-call dossier tools (explain_symbol, prepare_change). Packs pre-rendered
/// sections under the shared response-token budget (<see cref="Output.MaxResponseTokens"/>): the first section is
/// always kept so the dossier is never empty, and once the budget is hit the remaining sections are replaced by a
/// single "call the tool directly" note rather than dropped silently. A dossier's whole point is to close a
/// dependent navigation chain (search → members → source → refs) in ONE round-trip instead of four — the client
/// re-bills the entire conversation every turn, so turns, not bytes, are the cost.
/// </summary>
internal static class DossierBuilder
{
    // Cap inline source so one large type/method can't dominate the dossier; the remainder is one call away.
    private const int MaxSourceLines = 160;

    // Leave headroom under the response cap for the header line and the truncation note.
    private const int Headroom = 500;

    public static string Assemble(string headerLine, IReadOnlyList<(string Heading, string Body)> sections)
    {
        StringBuilder sb = new();
        int budget = Output.MaxResponseTokens - Headroom;

        string header = headerLine + "\n\n";
        sb.Append(header);
        int used = Output.EstimateTokens(header);

        bool anyEmitted = false;
        bool truncated = false;
        foreach ((string heading, string body) in sections)
        {
            if (!TryAppendSection(sb, ref used, ref anyEmitted, budget, heading, body))
            {
                truncated = true;
                break;
            }
        }

        if (truncated)
        {
            sb.AppendLine("_Response budget reached — call get_symbol_source / find_references / call_hierarchy directly for anything omitted._");
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    /// <summary>True when the caller asked for the concise tier (signatures + references only, bodies omitted).
    /// Anything other than "concise" (including null) is the default detailed tier.</summary>
    public static bool IsConcise(string? verbosity) =>
        string.Equals(verbosity, "concise", StringComparison.OrdinalIgnoreCase);

    public static string RenderSource(ICodeIndexStore index, IFileSystem fileSystem, string sourceFilePath, string displayFile, int startLine, int lineCount, bool concise = false)
    {
        // Concise tier: omit the body entirely (the token-heaviest section) and leave an exact pointer. The
        // structural sections around it already carry the signatures, so this is the ~1/3-token "signatures + refs"
        // response — the agent opts back into the body with the named call only when it actually needs to read code.
        if (concise)
        {
            return $"_(concise — {lineCount}-line body omitted; get_symbol_source(\"{displayFile}\", {startLine}, {lineCount}) to read it)_\n";
        }

        int shown = Math.Min(lineCount, MaxSourceLines);
        string source = GetSymbolSourceTool.GetSymbolSource(index, fileSystem, sourceFilePath, startLine, shown);
        if (GetSymbolSourceTool.IsErrorResult(source))
        {
            // File changed since indexing — don't present the sentinel as code; point at an explicit read.
            return $"(source unavailable — call get_symbol_source(\"{displayFile}\", {startLine}, {lineCount}))";
        }

        if (lineCount > MaxSourceLines)
        {
            int rest = lineCount - MaxSourceLines;
            source += $"… {rest} more line(s) — get_symbol_source(\"{displayFile}\", {startLine + MaxSourceLines}, {rest})\n";
        }

        return source;
    }

    // Appends "## {heading}\n{body}" when it fits the budget. Returns false (caller stops) once the budget is
    // exhausted AND at least one section is already emitted — the first section is always kept.
    private static bool TryAppendSection(StringBuilder sb, ref int used, ref bool anyEmitted, int budget, string heading, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return true;
        }

        string block = $"## {heading}\n{body.TrimEnd()}\n\n";
        int cost = Output.EstimateTokens(block);
        if (used + cost > budget && anyEmitted)
        {
            return false;
        }

        sb.Append(block);
        used += cost;
        anyEmitted = true;
        return true;
    }
}
