using System.ComponentModel;
using CodeIndex.Abstractions;
using ModelContextProtocol.Server;

namespace CodeIndex.Mcp;

[McpServerToolType]
public static class StructuralSearchTool
{
    [McpServerTool(Name = "search_structural")]
    [Description("Find code by AST SHAPE — things text/regex can't reliably match. Pass a curated pattern name; call with an unknown/empty pattern to list them all. C# patterns: empty-catch, catch-all, async-void, blocking-async, public-field, throw-in-finally, not-implemented. TypeScript/TSX patterns (incl. repo house rules): inline-style, classname-interp, ts-ignore, default-export, console-log, any-type. The pattern name selects the language + engine (empty-catch resolves to the C# engine). Parses on demand — scope with project= on large repos.")]
    public static string SearchStructural(
        ICodeIndexStore index,
        [Description("Structural pattern name (e.g. 'empty-catch', 'async-void'). Empty/unknown → lists the vocabulary.")] string pattern = "",
        [Description("Optional project filter (e.g., 'MyApp.Core') — strongly recommended on large repos to bound parse cost.")] string? project = null,
        [Description("Max sample lines to emit across all files (default 40).")] int max = 40,
        [Description("Max sample lines shown per file (default 5); the true per-file count is still reported.")] int perFileMax = 5)
    {
        return index.SearchStructural(pattern, project, max, perFileMax);
    }
}
