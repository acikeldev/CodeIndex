using System.ComponentModel;
using CodeIndex.Abstractions;
using ModelContextProtocol.Server;

namespace CodeIndex.Mcp;

[McpServerToolType]
public static class TraceCallsTool
{
    [McpServerTool(Name = "trace_calls")]
    [Description("Transitive call trace in ONE call: follow what a method invokes ('callees' — downstream execution flow) or what invokes it ('callers' — upstream impact) across MULTIPLE levels, instead of hand-running call_hierarchy per node. Returns an indented tree with each node's definition site + signature. C# only; nodes are Type.Method bound via receiver type (pass Type.Method to pick a root), interface calls descend into implementations, '?' name-only matches are shown but not followed. 'callers' inverts the whole project graph, so pass project= on large repos. Cycles/repeats shown once; bounded by depth + maxNodes. Pass includeBodies=true to also inline every traced node's full source in the same response.")]
    public static string TraceCalls(
        ICodeIndexStore index,
        [Description("Method name to start from, or Type.Method to pick one type")] string method,
        [Description("'callees' (default — what it calls, downstream) or 'callers' (what calls it, upstream)")] string direction = "callees",
        [Description("Levels to follow below the root (default 3, clamped to 1–6)")] int depth = 3,
        [Description("Optional project filter; recommended for 'callers' on large repos")] string? project = null,
        [Description("Max nodes to emit before truncating (default 40, clamped to 5–200)")] int maxNodes = 40,
        [Description("Also inline each traced node's full source body in the same response (default false — the tree stays lean; set true for one self-sufficient result with all the code)")] bool includeBodies = false)
    {
        int clampedDepth = Math.Clamp(depth, 1, 6);
        int clampedNodes = Math.Clamp(maxNodes, 5, 200);
        return index.GetCallTrace(method, direction, project, clampedDepth, clampedNodes, includeBodies);
    }
}
