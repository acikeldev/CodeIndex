using System.ComponentModel;
using CodeIndex.Abstractions;
using ModelContextProtocol.Server;

namespace CodeIndex.Mcp;

[McpServerToolType]
public static class CallHierarchyTool
{
    [McpServerTool(Name = "call_hierarchy")]
    [Description("Who calls a method ('callers') or what it invokes ('callees'). Callers are grouped by file and labelled with the ENCLOSING member — sharper than find_references, which also hits declarations/comments/strings. Name-based, no overload/receiver resolution. Covers C# + TS/TSX; parses on demand, so scope 'callers' with project= on large repos (callees is cheap).")]
    public static string CallHierarchy(
        ICodeIndexStore index,
        [Description("Method name")] string method,
        [Description("'callers' (default), 'callees', or 'both'")] string direction = "callers",
        [Description("'both' (default, C#+TS), 'csharp', or 'typescript'")] string language = "both",
        [Description("Optional project filter; strongly recommended for 'callers' on large repos")] string? project = null,
        [Description("Max call sites/callees to emit (default 40)")] int max = 40,
        [Description("Max call sites per file for 'callers' (default 5)")] int perFileMax = 5)
    {
        return index.GetCallHierarchy(method, direction, project, max, perFileMax, language);
    }
}
