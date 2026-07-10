using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Mcp;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Mcp;

/// <summary>
/// Output-formatting coverage for the <c>index_stats</c> health tool: the "not built" branch, the built
/// branch (build kind + age + counts), the C#-only vs C#+TS project summary, and the cache line.
/// </summary>
public sealed class IndexStatsToolTests
{
    private const string Root = @"C:\repo";
    private readonly InMemoryFileSystem _fs = new();

    public IndexStatsToolTests()
    {
        _fs.AddFile(@"C:\repo\App.slnx",
            "<Solution>\n  <Project Path=\"App/App.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\App\App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        _fs.AddFile(@"C:\repo\App\A.cs", "namespace App; public class A { public void M() { } }");
    }

    private CodeIndexStore NewStore()
    {
        return new CodeIndexStore(_fs, new IndexCache(_fs), new TsIndexCache(_fs), CodeIndexConfig.Default);
    }

    [Fact]
    public void ReportsNotBuiltBeforeAnyBuild()
    {
        CodeIndexStore store = NewStore();

        string output = IndexStatsTool.IndexStats(store, new IndexCache(_fs));

        output.Should().Contain("# CodeIndex status");
        output.Should().Contain("Repo root: (not resolved)");
        output.Should().Contain("Last build: none yet (index not built).");
    }

    [Fact]
    public void ReportsRepoRootAndCountsAfterBuild()
    {
        CodeIndexStore store = NewStore();
        store.Build(Root);

        string output = IndexStatsTool.IndexStats(store, new IndexCache(_fs));

        output.Should().Contain($"Repo root: {Root}");
        output.Should().Contain("1 projects, ");
        output.Should().Contain("types, ");
        output.Should().Contain("members");
    }

    [Fact]
    public void ReportsBuildKindAndFreshAge()
    {
        CodeIndexStore store = NewStore();
        store.Build(Root);

        string output = IndexStatsTool.IndexStats(store, new IndexCache(_fs));

        // A just-completed build reports as "full" with a seconds-scale age.
        output.Should().Contain("Last build: full — ");
        output.Should().Contain("s ago (took ");
        output.Should().Contain("reparsed, ");
        output.Should().Contain("removed)");
    }

    [Fact]
    public async Task IncludesTypeScriptBreakdownWhenTsProjectsPresent()
    {
        _fs.AddFile(@"C:\repo\web\tsconfig.json", "{ }");
        _fs.AddFile(@"C:\repo\web\a.ts", "export class Widget { }\n");
        _fs.AddFile(@"C:\repo\web\styles.scss", ".btn { color: red; }\n");

        CodeIndexStore store = NewStore();
        store.Build(Root);
        await store.RefreshTypeScriptAsync(Root, fullRebuild: true, CancellationToken.None);

        string output = IndexStatsTool.IndexStats(store, new IndexCache(_fs));

        output.Should().Contain("C#, ");
        output.Should().Contain("TS/SCSS)");
    }

    [Fact]
    public void ReportsCacheLocationAndSchemaVersion()
    {
        CodeIndexStore store = NewStore();
        store.Build(Root);
        IndexCache cache = new IndexCache(_fs);

        string output = IndexStatsTool.IndexStats(store, cache);

        output.Should().Contain($"Cache: {cache.GetCachePath(store.CacheDirectory)}");
        output.Should().Contain($"(schema v{cache.CurrentSchemaVersion})");
    }
}
