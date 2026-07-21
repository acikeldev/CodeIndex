using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Mcp;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Mcp;

/// <summary>
/// Output-contract tests for <see cref="TraceCallsTool"/>: the transitive trace follows the callee chain
/// (downstream) and caller chain (upstream) across multiple levels in one call, honours the depth limit, caps at
/// maxNodes, marks cycles once, and reports a graceful miss. This is the server-side multi-hop composite — the
/// point is that a whole navigation chain collapses into a single tool result.
/// </summary>
public sealed class TraceCallsToolTests
{
    private const string Root = @"C:\repo";
    private readonly InMemoryFileSystem _fs = new();

    // Alpha -> Beta -> Gamma (linear chain); Ping <-> Pong (cycle). Distinct names so the name-based matcher is exact.
    private void Seed()
    {
        _fs.AddFile(@"C:\repo\App.slnx", "<Solution>\n  <Project Path=\"Core/Core.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\Core\Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        _fs.AddFile(@"C:\repo\Core\Flow.cs",
            "namespace N;\npublic class Flow\n{\n"
            + "    public void Alpha() { Beta(); }\n"
            + "    public void Beta() { Gamma(); }\n"
            + "    public void Gamma() { }\n"
            + "    public void Ping() { Pong(); }\n"
            + "    public void Pong() { Ping(); }\n"
            + "}\n");
    }

    private CodeIndexStore Build()
    {
        CodeIndexStore store = new(_fs, new IndexCache(_fs), new TsIndexCache(_fs), CodeIndexConfig.Default);
        store.Build(Root);
        return store;
    }

    [Fact]
    public void Callees_FollowsChainTransitively()
    {
        Seed();
        CodeIndexStore store = Build();

        string output = TraceCallsTool.TraceCalls(store, "Alpha", direction: "callees", depth: 3);

        output.Should().Contain("callees of Alpha");
        output.Should().Contain("Beta");
        output.Should().Contain("Gamma");
        // Gamma is reached only by descending through Beta, so it must be more deeply indented than Beta.
        int betaIndent = IndentOf(output, "Beta");
        int gammaIndent = IndentOf(output, "Gamma");
        gammaIndent.Should().BeGreaterThan(betaIndent, "Gamma is a transitive (2nd-level) callee reached via Beta");
    }

    [Fact]
    public void InlinesNodeSignatures_AndCarriesAntiReReadNote()
    {
        Seed();
        CodeIndexStore store = Build();

        string output = TraceCallsTool.TraceCalls(store, "Alpha", direction: "callees", depth: 3, includeBodies: true);

        // 1a: each node carries its signature inline (in the tree).
        output.Should().Contain("void Beta()");
        // includeBodies=true inlines the FULL body of every traced node in the SAME response, with a do-not-open
        // note. (Measured caveat: this does NOT stop the Claude Code agent over-reading — kept as an opt-in.)
        output.Should().Contain("## Bodies");
        output.Should().Contain("### Beta");
        output.Should().Contain("### Gamma");
        output.Should().Contain("do NOT open");
    }

    [Fact]
    public void IncludeBodiesFalse_ReturnsLeanTreeOnly()
    {
        Seed();
        CodeIndexStore store = Build();

        string output = TraceCallsTool.TraceCalls(store, "Alpha", direction: "callees", depth: 3, includeBodies: false);

        output.Should().Contain("Beta");           // tree still present
        output.Should().NotContain("## Bodies");   // but no inlined bodies
    }

    [Fact]
    public void Depth1_StopsAtDirectChildren()
    {
        Seed();
        CodeIndexStore store = Build();

        // includeBodies:false so a callee's inlined body (which legitimately calls the grandchild) can't leak the
        // grandchild name — we are testing tree DEPTH, not the bodies section.
        string output = TraceCallsTool.TraceCalls(store, "Alpha", direction: "callees", depth: 1, includeBodies: false);

        output.Should().Contain("Beta");        // direct callee
        output.Should().NotContain("Gamma");    // transitive — beyond depth 1
    }

    [Fact]
    public void Callers_FollowsChainUpstream()
    {
        Seed();
        CodeIndexStore store = Build();

        string output = TraceCallsTool.TraceCalls(store, "Gamma", direction: "callers", depth: 3);

        output.Should().Contain("callers of Gamma");
        output.Should().Contain("Beta");   // Beta calls Gamma
        output.Should().Contain("Alpha");  // Alpha calls Beta (transitive upstream)
    }

    [Fact]
    public void Cycle_IsMarkedOnceNotInfinite()
    {
        Seed();
        CodeIndexStore store = Build();

        string output = TraceCallsTool.TraceCalls(store, "Ping", direction: "callees", depth: 5);

        output.Should().Contain("Pong");
        output.Should().Contain("already traced");   // Ping re-encountered through Pong, shown once
    }

    [Fact]
    public void MaxNodes_Truncates()
    {
        // A fan-out wider than the maxNodes floor (5), so the cap actually bites.
        _fs.AddFile(@"C:\repo\App.slnx", "<Solution>\n  <Project Path=\"Core/Core.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\Core\Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        _fs.AddFile(@"C:\repo\Core\Wide.cs",
            "namespace N;\npublic class Wide\n{\n"
            + "    public void Hub() { A1(); A2(); A3(); A4(); A5(); A6(); A7(); A8(); }\n"
            + "    public void A1() { }\n    public void A2() { }\n    public void A3() { }\n    public void A4() { }\n"
            + "    public void A5() { }\n    public void A6() { }\n    public void A7() { }\n    public void A8() { }\n"
            + "}\n");
        CodeIndexStore store = Build();

        string output = TraceCallsTool.TraceCalls(store, "Hub", direction: "callees", depth: 2, maxNodes: 5);

        output.Should().Contain("truncated");
    }

    [Fact]
    public void UnknownMethod_ReturnsGracefulMiss()
    {
        Seed();
        CodeIndexStore store = Build();

        string output = TraceCallsTool.TraceCalls(store, "NoSuchMethod");

        output.Should().Contain("no C# method named 'NoSuchMethod'");
    }

    // Leading-space count of the first line containing token — the tree indent for that node.
    private static int IndentOf(string output, string token)
    {
        foreach (string line in output.Split('\n'))
        {
            if (line.Contains(token, StringComparison.Ordinal))
            {
                return line.Length - line.TrimStart().Length;
            }
        }

        return -1;
    }
}
