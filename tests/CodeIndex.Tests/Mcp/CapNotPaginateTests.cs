using System.Reflection;
using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Mcp;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Mcp;

/// <summary>
/// Cap-not-paginate guard. SWE-agent's ACI study measured that a PAGING search underperforms both no-search and a
/// capped "here's the total, narrow it" search (paging 12% &lt; no-search 15.7% &lt; cap+narrow 18% resolve rate):
/// agents exhaustively page and burn the budget. CodeIndex therefore caps results, reports the TRUE total, and
/// steers to a narrower query (project=) or a higher cap — it never offers a cursor / next page. These tests keep
/// it that way: no tool may expose a pagination cursor, and the capped output must steer to narrowing.
/// </summary>
public sealed class CapNotPaginateTests
{
    // The pagination-cursor vocabulary. Caps (max, perFileMax, tokenBudget, lineCount, …) are fine; stateful
    // "give me the next slice" parameters are the anti-pattern.
    private static readonly string[] ForbiddenParamFragments =
        ["offset", "cursor", "page", "skip", "startindex", "continuation", "nexttoken", "fromindex"];

    [Fact]
    public void NoTool_ExposesAPaginationCursor()
    {
        foreach (MethodInfo tool in McpToolSurface.ToolMethods())
        {
            string toolName = McpToolSurface.ToolName(tool) ?? tool.Name;
            foreach (ParameterInfo p in tool.GetParameters().Where(McpToolSurface.IsSchemaParameter))
            {
                string lower = (p.Name ?? string.Empty).ToLowerInvariant();
                foreach (string fragment in ForbiddenParamFragments)
                {
                    lower.Contains(fragment).Should().BeFalse(
                        $"tool '{toolName}' parameter '{p.Name}' looks like pagination ('{fragment}'). "
                        + "Cap results and steer to a narrower query instead of paging — agents exhaustively page and burn the budget.");
                }
            }
        }
    }

    [Fact]
    public void FindReferences_CapsAndSteersToNarrow_NotToNextPage()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(@"C:\repo\App.slnx", "<Solution>\n  <Project Path=\"Core/Core.csproj\" />\n</Solution>\n");
        fs.AddFile(@"C:\repo\Core\Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        fs.AddFile(@"C:\repo\Core\Core.cs", "namespace N;\npublic class CoreService { public void Execute() { } }\n");
        // Several distinct consumer files so the result spans multiple files and must be capped.
        for (int i = 0; i < 6; i++)
        {
            fs.AddFile($@"C:\repo\Core\Consumer{i}.cs",
                $"namespace N;\npublic class Consumer{i} {{ private readonly CoreService _svc = new CoreService(); public void Run() {{ _svc.Execute(); }} }}\n");
        }

        CodeIndexStore store = new(fs, new IndexCache(fs), new TsIndexCache(fs), CodeIndexConfig.Default);
        store.Build(@"C:\repo");

        string output = FindReferencesTool.FindReferences(store, fs, "CoreService", max: 1, perFileMax: 1);

        // Capped, with the true total reported and a steer to narrow — never a cursor / next page.
        output.Should().Contain("total");
        output.Should().Contain("Narrow with project=");
        output.Should().NotContainEquivalentOf("next page");
        output.Should().NotContainEquivalentOf("cursor");
        output.Should().NotContainEquivalentOf("offset=");
    }
}
