using System.ComponentModel;
using CodeIndex.Abstractions;
using ModelContextProtocol.Server;

namespace CodeIndex.Mcp;

[McpServerToolType]
public static class CallHierarchyTool
{
    [McpServerTool(Name = "call_hierarchy")]
    [Description("Heuristic call hierarchy (C# via Roslyn, TS/TSX via tree-sitter): 'callers' = who invokes a method (grouped by file, each labelled with its ENCLOSING member — sharper than find_references, which also matches declarations/comments/strings); 'callees' = what a method invokes. Name-based, no overload/receiver resolution. language='both' (default) spans C# AND TS in one call (great for a method called from both); or 'csharp'/'typescript'. Parses on demand — 'callers' scans all files of the chosen language(s), so scope with project= on large repos; 'callees' is cheap (only the declaring file).")]
    public static string CallHierarchy(
        ICodeIndexStore index,
        [Description("Method name (e.g. 'GetItems')")] string method,
        [Description("'callers' (default), 'callees', or 'both'")] string direction = "callers",
        [Description("'both' (default, C#+TS), 'csharp', or 'typescript'")] string language = "both",
        [Description("Optional project filter (e.g., 'MyApp.Core') — strongly recommended for 'callers' on large repos.")] string? project = null,
        [Description("Max call sites / callees to emit (default 40)")] int max = 40,
        [Description("Max call sites shown per file for 'callers' (default 5)")] int perFileMax = 5)
    {
        return index.GetCallHierarchy(method, direction, project, max, perFileMax, language);
    }
}
