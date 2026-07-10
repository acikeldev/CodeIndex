using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Mcp;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Mcp;

/// <summary>
/// Output-formatting coverage for <see cref="StructuralSearchTool.SearchStructural"/>: a C# pattern hit,
/// the empty/unknown-pattern vocabulary listing, the no-match message, the project scope filter, and the
/// TypeScript engine dispatch — all asserted against the exact tool output strings.
/// </summary>
public sealed class StructuralSearchToolTests
{
    private const string Root = @"C:\repo";
    private readonly InMemoryFileSystem _fs = new();

    public StructuralSearchToolTests()
    {
        _fs.AddFile(@"C:\repo\App.slnx",
            "<Solution>\n  <Project Path=\"App/App.csproj\" />\n  <Project Path=\"Core/Core.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\App\App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        _fs.AddFile(@"C:\repo\App\Svc.cs", """
            namespace App;
            public class Svc
            {
                public void Run() { try { } catch { } }
            }
            """);
        _fs.AddFile(@"C:\repo\Core\Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        _fs.AddFile(@"C:\repo\Core\Thing.cs", "namespace Core; public class Thing { }");
    }

    private CodeIndexStore BuildStore()
    {
        CodeIndexStore store = new(_fs, new IndexCache(_fs), new TsIndexCache(_fs), CodeIndexConfig.Default);
        store.Build(Root);
        return store;
    }

    [Fact]
    public void MatchingPattern_RendersHeaderAndSnippet()
    {
        CodeIndexStore store = BuildStore();

        string result = StructuralSearchTool.SearchStructural(store, "empty-catch");

        result.Should().Contain("empty-catch: 1 matches in 1 files");
        result.Should().Contain("== Svc.cs (App) — 1 ==");
        result.Should().Contain("catch");
    }

    [Fact]
    public void UnknownPattern_ListsVocabulary()
    {
        CodeIndexStore store = BuildStore();

        string result = StructuralSearchTool.SearchStructural(store, "does-not-exist");

        result.Should().StartWith("Unknown structural pattern 'does-not-exist'.");
        result.Should().Contain("C# patterns:");
        result.Should().Contain("empty-catch");
        result.Should().Contain("TypeScript/TSX patterns:");
    }

    [Fact]
    public void EmptyPattern_ListsVocabulary()
    {
        CodeIndexStore store = BuildStore();

        string result = StructuralSearchTool.SearchStructural(store, string.Empty);

        result.Should().StartWith("Unknown structural pattern ''.");
        result.Should().Contain("C# patterns:");
    }

    [Fact]
    public void ValidPatternWithNoHits_ReportsNoMatches()
    {
        CodeIndexStore store = BuildStore();

        string result = StructuralSearchTool.SearchStructural(store, "not-implemented");

        result.Should().StartWith("No 'not-implemented' matches.");
    }

    [Fact]
    public void ProjectFilter_ScopesAndAppearsInOutput()
    {
        CodeIndexStore store = BuildStore();

        string result = StructuralSearchTool.SearchStructural(store, "empty-catch", project: "Core");

        result.Should().StartWith("No 'empty-catch' matches in project 'Core'.");
    }

    [Fact]
    public async Task TypeScriptPattern_DispatchesToTsEngine()
    {
        CodeIndexStore store = BuildStore();
        _fs.AddFile(@"C:\repo\web\tsconfig.json", "{ }");
        _fs.AddFile(@"C:\repo\web\a.ts", "export function greet(x: any) { return x; }\n");
        await store.RefreshTypeScriptAsync(Root, fullRebuild: true, CancellationToken.None);

        string result = StructuralSearchTool.SearchStructural(store, "any-type");

        result.Should().Contain("any-type");
        result.Should().Contain("a.ts");
    }
}
