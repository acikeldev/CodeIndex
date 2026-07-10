using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Mcp;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Mcp;

/// <summary>
/// Output-formatting coverage for <see cref="SearchSymbolTool.SearchSymbol"/>: header wording (singular/plural,
/// truncated), compact vs full JSON bodies, token-budget packing, the default-limit fallback, the unknown-kind
/// guard, and the not-found "did you mean" path.
/// </summary>
public sealed class SearchSymbolToolTests
{
    private const string Root = @"C:\repo";
    private readonly InMemoryFileSystem _fs = new();

    public SearchSymbolToolTests()
    {
        _fs.AddFile(@"C:\repo\App.slnx",
            "<Solution>\n  <Project Path=\"App/App.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\App\App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n");
        _fs.AddFile(@"C:\repo\App\Widget.cs", """
            namespace App;
            public class Widget
            {
                public void DoWork() { }
            }
            """);
    }

    private CodeIndexStore Build()
    {
        CodeIndexStore store = new(_fs, new IndexCache(_fs), new TsIndexCache(_fs), CodeIndexConfig.Default);
        store.Build(Root);
        return store;
    }

    private CodeIndexStore BuildMany(int count)
    {
        System.Text.StringBuilder sb = new();
        sb.AppendLine("namespace App;");
        for (int i = 0; i < count; i++)
        {
            sb.AppendLine($"public class Repeated{i} {{ public void RepeatedMethod{i}() {{ }} }}");
        }
        _fs.AddFile(@"C:\repo\App\Many.cs", sb.ToString());
        return Build();
    }

    // ── unknown-kind guard ──────────────────────────────────────────────────────

    [Fact]
    public void UnknownKind_ReturnsGuardMessage()
    {
        CodeIndexStore store = Build();

        string result = SearchSymbolTool.SearchSymbol(store, "Widget", kind: "banana");

        result.Should().StartWith("Unknown kind 'banana'.");
        result.Should().Contain("Valid kinds:");
    }

    // ── not-found path ───────────────────────────────────────────────────────────

    [Fact]
    public void NoMatch_ReturnsNotFoundWithHints()
    {
        CodeIndexStore store = Build();

        string result = SearchSymbolTool.SearchSymbol(store, "Zzzznope");

        result.Should().StartWith("No symbols found matching 'Zzzznope'.");
        result.Should().Contain("search_text");
        result.Should().Contain("resolve_bare_name");
    }

    [Fact]
    public void NoMatch_NearName_OffersDidYouMean()
    {
        CodeIndexStore store = Build();

        // A near-miss of "Widget" should trigger the suggester.
        string result = SearchSymbolTool.SearchSymbol(store, "Widgat");

        result.Should().Contain("Did you mean");
        result.Should().Contain("Widget");
    }

    // ── compact body + header ─────────────────────────────────────────────────────

    [Fact]
    public void SingleMatch_CompactBody_UsesSingularHeader()
    {
        CodeIndexStore store = Build();

        string result = SearchSymbolTool.SearchSymbol(store, "Widget", kind: "class");

        result.Should().StartWith("1 match.\n");
        result.Should().Contain("Widget");
        result.Should().Contain("(App)");
        result.Should().Contain("Widget.cs:");
    }

    [Fact]
    public void MethodKindFilter_FindsMethod()
    {
        CodeIndexStore store = Build();

        string result = SearchSymbolTool.SearchSymbol(store, "DoWork", kind: "method");

        result.Should().StartWith("1 match.\n");
        result.Should().Contain("DoWork");
    }

    [Fact]
    public void ProjectFilter_ScopesResults()
    {
        CodeIndexStore store = Build();

        string result = SearchSymbolTool.SearchSymbol(store, "Widget", project: "App");

        result.Should().Contain("(App)");
    }

    // ── full JSON body ─────────────────────────────────────────────────────────────

    [Fact]
    public void FullDetail_ReturnsJsonWithMetadata()
    {
        CodeIndexStore store = Build();

        string result = SearchSymbolTool.SearchSymbol(store, "Widget", kind: "class", detail: "full");

        result.Should().StartWith("1 match.\n");
        string json = result[(result.IndexOf('\n') + 1)..];
        json.Should().StartWith("[").And.EndWith("]");
        json.Should().Contain("\"name\":\"Widget\"");
        json.Should().Contain("\"sourceFilePath\":");
        json.Should().NotContain("\n  "); // compact JSON, not indented
    }

    // ── default-limit fallback (no token budget) ────────────────────────────────────

    [Fact]
    public void ManyMatches_NoBudget_TruncatesToDefaultLimit()
    {
        CodeIndexStore store = BuildMany(60);

        string result = SearchSymbolTool.SearchSymbol(store, "RepeatedMethod", kind: "method");

        // 60 methods match, but the default limit caps the emitted rows at 50.
        result.Should().StartWith("60 matches, showing 50 —");
        result.Should().Contain("token_budget");
        result.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.Should().Be(51); // header + 50 rows
    }

    // ── token-budget packing ─────────────────────────────────────────────────────────

    [Fact]
    public void TokenBudget_Compact_PacksUntilExhausted()
    {
        CodeIndexStore store = BuildMany(60);

        // Tiny budget: only a handful of rows fit, but at least one is always emitted.
        string result = SearchSymbolTool.SearchSymbol(store, "RepeatedMethod", kind: "method", tokenBudget: 20);

        result.Should().StartWith("60 matches, showing ");
        int emittedRows = result.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1;
        emittedRows.Should().BeGreaterThan(0).And.BeLessThan(60);
    }

    [Fact]
    public void TokenBudget_Full_PacksJsonUntilExhausted()
    {
        CodeIndexStore store = BuildMany(60);

        string result = SearchSymbolTool.SearchSymbol(
            store, "RepeatedMethod", kind: "method", detail: "full", tokenBudget: 40);

        result.Should().StartWith("60 matches, showing ");
        string json = result[(result.IndexOf('\n') + 1)..];
        json.Should().StartWith("[").And.EndWith("]");
    }
}
