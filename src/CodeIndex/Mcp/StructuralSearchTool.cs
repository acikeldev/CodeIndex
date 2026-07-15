using System.ComponentModel;
using CodeIndex.Abstractions;
using ModelContextProtocol.Server;

namespace CodeIndex.Mcp;

[McpServerToolType]
public static class StructuralSearchTool
{
    [McpServerTool(Name = "search_structural")]
    [Description("Find code by AST SHAPE — what text/regex can't reliably match. Pass a curated pattern name; empty/unknown lists the vocabulary. C#: empty-catch, catch-all, async-void, blocking-async, public-field, throw-in-finally, not-implemented. TS/TSX (incl. repo house rules): inline-style, classname-interp, ts-ignore, default-export, console-log, any-type. The pattern name selects the language+engine. Parses on demand — scope with project= on large repos.")]
    public static string SearchStructural(
        ICodeIndexStore index,
        [Description("Pattern name (e.g. 'empty-catch'); empty/unknown lists the vocabulary")] string pattern = "",
        [Description("Optional project filter; strongly recommended on large repos to bound parse cost")] string? project = null,
        [Description("Max sample lines across all files (default 40)")] int max = 40,
        [Description("Max sample lines per file (default 5); true count still reported")] int perFileMax = 5)
    {
        return index.SearchStructural(pattern, project, max, perFileMax);
    }
}
