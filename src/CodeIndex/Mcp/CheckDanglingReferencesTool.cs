using System.ComponentModel;
using CodeIndex.Abstractions;
using ModelContextProtocol.Server;

namespace CodeIndex.Mcp;

[McpServerToolType]
public static class CheckDanglingReferencesTool
{
    [McpServerTool(Name = "check_dangling_references")]
    [Description("For a TS/TSX file: report UNUSED imports and possibly-MISSING imports — a PascalCase identifier used but neither imported nor declared here, yet defined in another indexed TS file (the classic post-merge break where an import was dropped but its use remained). Syntactic + index-confirmed heuristic; for C# use resolve_bare_name instead.")]
    public static string CheckDanglingReferences(
        ICodeIndexStore index,
        [Description("TS/TSX file to check — filename or full path")] string file)
    {
        return index.CheckDanglingReferences(file);
    }
}
