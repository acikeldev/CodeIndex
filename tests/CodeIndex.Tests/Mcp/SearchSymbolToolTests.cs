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

        string result = SearchSymbolTool.SearchSymbol(store, _fs, "Widget", kind: "banana");

        result.Should().StartWith("Unknown kind 'banana'.");
        result.Should().Contain("Valid kinds:");
    }

    // ── not-found path ───────────────────────────────────────────────────────────

    [Fact]
    public void NoMatch_ReturnsNotFoundWithHints()
    {
        CodeIndexStore store = Build();

        string result = SearchSymbolTool.SearchSymbol(store, _fs, "Zzzznope");

        result.Should().StartWith("No symbols found matching 'Zzzznope'.");
        result.Should().Contain("search_text");
        result.Should().Contain("resolve_bare_name");
    }

    [Fact]
    public void NoMatch_NearName_OffersDidYouMean()
    {
        CodeIndexStore store = Build();

        // A near-miss of "Widget" should trigger the suggester.
        string result = SearchSymbolTool.SearchSymbol(store, _fs, "Widgat");

        result.Should().Contain("Did you mean");
        result.Should().Contain("Widget");
    }

    // ── compact body + header ─────────────────────────────────────────────────────

    [Fact]
    public void SingleMatch_CompactBody_UsesSingularHeader()
    {
        CodeIndexStore store = Build();

        string result = SearchSymbolTool.SearchSymbol(store, _fs, "Widget", kind: "class");

        result.Should().StartWith("1 match.\n");
        result.Should().Contain("Widget");
        result.Should().Contain("(App)");
        result.Should().Contain("Widget.cs:");
    }

    [Fact]
    public void MethodKindFilter_FindsMethod()
    {
        CodeIndexStore store = Build();

        string result = SearchSymbolTool.SearchSymbol(store, _fs, "DoWork", kind: "method");

        result.Should().StartWith("1 match.\n");
        result.Should().Contain("DoWork");
    }

    [Fact]
    public void ProjectFilter_ScopesResults()
    {
        CodeIndexStore store = Build();

        string result = SearchSymbolTool.SearchSymbol(store, _fs, "Widget", project: "App");

        result.Should().Contain("(App)");
    }

    // ── full JSON body ─────────────────────────────────────────────────────────────

    [Fact]
    public void FullDetail_ReturnsJsonWithMetadata()
    {
        CodeIndexStore store = Build();

        string result = SearchSymbolTool.SearchSymbol(store, _fs, "Widget", kind: "class", detail: "full");

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

        string result = SearchSymbolTool.SearchSymbol(store, _fs, "RepeatedMethod", kind: "method");

        // 60 methods match, but the default limit caps the emitted rows at 50.
        result.Should().StartWith("60 matches, showing 50 —");
        result.Should().Contain("token_budget");
        // Strip the advisory footer before counting content lines.
        string rows = result.Replace(Steering.SymbolDossierHint, string.Empty);
        rows.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.Should().Be(51); // header + 50 rows
    }

    // ── success-path steering footer ───────────────────────────────────────────────

    [Fact]
    public void CompactSuccess_AppendsExplainSymbolFooter()
    {
        CodeIndexStore store = Build();

        string result = SearchSymbolTool.SearchSymbol(store, _fs, "Widget");

        result.Should().Contain("Tip: for this symbol");
        result.Should().Contain("explain_symbol");
    }

    [Fact]
    public void FullDetail_OmitsFooter_KeepingJsonClean()
    {
        CodeIndexStore store = Build();

        string result = SearchSymbolTool.SearchSymbol(store, _fs, "Widget", detail: "full");

        result.Should().NotContain("Tip:");
        result.TrimEnd().Should().EndWith("]");
    }

    // ── speculative source appendix ────────────────────────────────────────────────

    [Fact]
    public void SingleMatch_AppendsSpeculativeSource()
    {
        CodeIndexStore store = Build();

        string result = SearchSymbolTool.SearchSymbol(store, _fs, "Widget", kind: "class");

        result.Should().Contain("pre-fetched");
        result.Should().Contain("public class Widget");   // the pre-fetched source body
    }

    [Fact]
    public void SpeculateDisabled_OmitsAppendix()
    {
        CodeIndexStore store = new(_fs, new IndexCache(_fs), new TsIndexCache(_fs), new CodeIndexConfig { Speculate = false });
        store.Build(Root);

        string result = SearchSymbolTool.SearchSymbol(store, _fs, "Widget", kind: "class");

        result.Should().NotContain("pre-fetched");
    }

    [Fact]
    public void SingleMatch_OversizeSource_OmitsAppendix()
    {
        System.Text.StringBuilder sb = new();
        sb.AppendLine("namespace App;");
        sb.AppendLine("public class BigOne");
        sb.AppendLine("{");
        for (int i = 0; i < 200; i++)
        {
            sb.AppendLine($"    private int _f{i}; // padding pushes the source past the speculate token budget");
        }

        sb.AppendLine("}");
        _fs.AddFile(@"C:\repo\App\BigOne.cs", sb.ToString());
        CodeIndexStore store = Build();

        string result = SearchSymbolTool.SearchSymbol(store, _fs, "BigOne", kind: "class");

        result.Should().StartWith("1 match.\n");
        result.Should().NotContain("pre-fetched");   // source exceeds SpeculateTokenBudget -> left to an explicit call
    }

    [Fact]
    public void ManyMatches_NoSpeculativeAppendix()
    {
        CodeIndexStore store = BuildMany(60);

        string result = SearchSymbolTool.SearchSymbol(store, _fs, "RepeatedMethod", kind: "method");

        result.Should().NotContain("pre-fetched");   // only a single confident match gets the appendix
    }

    // ── token-budget packing ─────────────────────────────────────────────────────────

    [Fact]
    public void TokenBudget_Compact_PacksUntilExhausted()
    {
        CodeIndexStore store = BuildMany(60);

        // Tiny budget: only a handful of rows fit, but at least one is always emitted.
        string result = SearchSymbolTool.SearchSymbol(store, _fs, "RepeatedMethod", kind: "method", tokenBudget: 20);

        result.Should().StartWith("60 matches, showing ");
        int emittedRows = result.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1;
        emittedRows.Should().BeGreaterThan(0).And.BeLessThan(60);
    }

    [Fact]
    public void TokenBudget_Full_PacksJsonUntilExhausted()
    {
        CodeIndexStore store = BuildMany(60);

        string result = SearchSymbolTool.SearchSymbol(
            store, _fs, "RepeatedMethod", kind: "method", detail: "full", tokenBudget: 40);

        result.Should().StartWith("60 matches, showing ");
        string json = result[(result.IndexOf('\n') + 1)..];
        json.Should().StartWith("[").And.EndWith("]");
    }
}
