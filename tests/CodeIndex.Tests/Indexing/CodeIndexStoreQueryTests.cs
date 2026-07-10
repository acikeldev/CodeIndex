using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Models;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Indexing;

/// <summary>
/// Exercises the store's query surface + the async refresh / TypeScript-segment paths over a mixed C#/TS/SCSS
/// fixture, so the whole dispatch layer (repo_map, structural search, call hierarchy, bare-name, dependency graph,
/// project listing, TS merge) is covered at the store level. The MCP tools wave adds the output-formatting layer.
/// </summary>
public sealed class CodeIndexStoreQueryTests
{
    private const string Root = @"C:\repo";
    private readonly InMemoryFileSystem _fs = new();

    public CodeIndexStoreQueryTests()
    {
        _fs.AddFile(@"C:\repo\App.slnx",
            "<Solution>\n  <Project Path=\"App/App.csproj\" />\n  <Project Path=\"Core/Core.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\App\App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"..\\Core\\Core.csproj\" /></ItemGroup></Project>\n");
        _fs.AddFile(@"C:\repo\App\Svc.cs", """
            using Core;
            namespace App;
            public class Svc
            {
                public void Run() { Helper(); try { } catch { } }
                private void Helper() { }
            }
            """);
        _fs.AddFile(@"C:\repo\Core\Core.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        _fs.AddFile(@"C:\repo\Core\Thing.cs", "namespace Core; public class Thing { }");

        // TypeScript / SCSS segment fixture.
        _fs.AddFile(@"C:\repo\web\tsconfig.json", "{ }");
        _fs.AddFile(@"C:\repo\web\a.ts", "export function greet(x: any) { return x; }\nexport class Widget { }\n");
        _fs.AddFile(@"C:\repo\web\styles.scss", ".btn { color: red; }\n");
    }

    private CodeIndexStore BuildCSharpOnly()
    {
        CodeIndexStore store = new(_fs, new IndexCache(_fs), new TsIndexCache(_fs), CodeIndexConfig.Default);
        store.Build(Root);
        return store;
    }

    private async Task<CodeIndexStore> BuildWithTypeScriptAsync()
    {
        CodeIndexStore store = BuildCSharpOnly();
        await store.RefreshTypeScriptAsync(Root, fullRebuild: true, CancellationToken.None);
        return store;
    }

    // ── C# query surface ───────────────────────────────────────────────────────

    [Fact]
    public void ListProjects_And_GetProject()
    {
        CodeIndexStore store = BuildCSharpOnly();

        store.ListProjects().Should().Contain(p => p.Name == "App").And.Contain(p => p.Name == "Core");
        store.GetProject("Core").Should().NotBeNull();
        store.GetProject("Nope").Should().BeNull();
    }

    [Fact]
    public void GetProjectDependencyInfo_ForwardAndReverse()
    {
        CodeIndexStore store = BuildCSharpOnly();

        store.GetProjectDependencyInfo("App")!.References.Should().Contain("Core");
        store.GetProjectDependencyInfo("Core")!.Dependents.Should().Contain("App");
        store.GetProjectDependencyInfo("Nope").Should().BeNull();
    }

    [Fact]
    public void ResolveBareName_ResolvesThroughUsing()
    {
        CodeIndexStore store = BuildCSharpOnly();

        BareNameResolution r = store.ResolveBareName("Svc.cs", "Thing");

        r.FileFound.Should().BeTrue();
        r.InScope.Should().Contain(c => c.FullyQualified == "Core.Thing");
    }

    [Fact]
    public void GetCallHierarchy_FindsCaller()
    {
        CodeIndexStore store = BuildCSharpOnly();

        string res = store.GetCallHierarchy("Helper", "callers", null, 40, 5, "csharp");

        res.Should().Contain("Run"); // Svc.Run() invokes Helper()
    }

    [Fact]
    public void SearchStructural_FindsCSharpSmell_AndReportsUnknownPattern()
    {
        CodeIndexStore store = BuildCSharpOnly();

        store.SearchStructural("empty-catch", null, 40, 5).Should().Contain("Svc.cs");
        store.SearchStructural("no-such-pattern", null, 40, 5).Should().Contain("Unknown structural pattern");
    }

    [Fact]
    public void GetRepoMap_RanksSymbols()
    {
        CodeIndexStore store = BuildCSharpOnly();

        string map = store.GetRepoMap([], 2000, null);

        map.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task RefreshAsync_RebuildsWithoutError()
    {
        CodeIndexStore store = BuildCSharpOnly();
        int before = store.TypeCount;

        await store.RefreshAsync(Root, fullRebuild: false, CancellationToken.None);

        store.TypeCount.Should().Be(before);
    }

    // ── TypeScript / SCSS segment ───────────────────────────────────────────────

    [Fact]
    public async Task RefreshTypeScript_IndexesTsAndScss_AndMergesIntoSnapshot()
    {
        CodeIndexStore store = await BuildWithTypeScriptAsync();

        store.TsProjectCount.Should().BeGreaterThan(0);
        store.AllSourceFiles.Should().Contain(f => f.FileName == "a.ts");
        store.AllSourceFiles.Should().Contain(f => f.FileName == "styles.scss");
        store.SearchSymbol("Widget", null, null).Should().Contain(r => r.Name == "Widget");
        // C# segment survives the TS merge.
        store.SearchSymbol("Svc", null, null).Should().Contain(r => r.Name == "Svc");
    }

    [Fact]
    public async Task SearchStructural_FindsTsPattern_AfterTypeScriptIndexed()
    {
        CodeIndexStore store = await BuildWithTypeScriptAsync();

        store.SearchStructural("any-type", null, 40, 5).Should().Contain("a.ts");
    }

    [Fact]
    public async Task CheckDanglingReferences_ProducesReportForTsFile()
    {
        CodeIndexStore store = await BuildWithTypeScriptAsync();

        store.CheckDanglingReferences("a.ts").Should().Contain("a.ts");
    }

    [Fact]
    public async Task GetCallHierarchy_Both_SpansCSharpAndTypeScript()
    {
        CodeIndexStore store = await BuildWithTypeScriptAsync();

        string res = store.GetCallHierarchy("greet", "both", null, 40, 5, "both");

        res.Should().Contain("C#");
        res.Should().Contain("TypeScript");
    }
}
