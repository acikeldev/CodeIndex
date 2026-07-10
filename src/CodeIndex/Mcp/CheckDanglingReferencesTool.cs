using System.ComponentModel;
using CodeIndex.Abstractions;
using ModelContextProtocol.Server;

namespace CodeIndex.Mcp;

[McpServerToolType]
public static class CheckDanglingReferencesTool
{
    [McpServerTool(Name = "check_dangling_references")]
    [Description("For a TypeScript/TSX file: report UNUSED imports (imported but never referenced) and possibly-MISSING imports (a PascalCase identifier used in the file that is neither imported nor declared here, yet is a real project symbol defined in another indexed TS file — the classic post-merge build break where an import was dropped but a use of it remained). Syntactic + index-confirmed heuristic; C# only supports resolve_bare_name instead.")]
    public static string CheckDanglingReferences(
        ICodeIndexStore index,
        [Description("TS/TSX file to check — filename ('MyStore.ts') or full path.")] string file)
    {
        return index.CheckDanglingReferences(file);
    }
}
