using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Indexing;

/// <summary>Wave 1 non-blocking startup: the fast cache-preload path (LoadCachedSnapshot) that lets the MCP host
/// start immediately, and the cold-start fallback contract. The original real-disk temp-dir fixture is replaced by
/// the shared in-memory file system (so a "writer" and a "reader" store see the same cache); assertions unchanged.</summary>
public sealed class StartupTests
{
    private const string Root = @"C:\repo";

    private readonly InMemoryFileSystem _fs = new();

    public StartupTests()
    {
        _fs.AddFile(@"C:\repo\Fixture.slnx",
            "<Solution>\n  <Project Path=\"Proj/Proj.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\Proj\Proj.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n");
        _fs.AddFile(@"C:\repo\Proj\A.cs", "namespace N; public class Alpha { public void M() { } }");
    }

    private CodeIndexStore NewStore() =>
        new(_fs, new IndexCache(_fs), new TsIndexCache(_fs), CodeIndexConfig.Default);

    [Fact]
    public void LoadCachedSnapshot_NoCache_ReturnsFalse_AndStaysEmpty()
    {
        CodeIndexStore store = NewStore();
        bool loaded = store.LoadCachedSnapshot(Root);

        Assert.False(loaded);
        Assert.Equal(0, store.ProjectCount);
        Assert.Empty(store.SearchSymbol("Alpha", null, null)); // usable, not dead — just empty until built
    }

    [Fact]
    public void LoadCachedSnapshot_WithCache_ReturnsTrue_AndPopulatesWithoutScan()
    {
        CodeIndexStore writer = NewStore();
        writer.Build(Root); // writes the versioned cache
        int expectedTypes = writer.TypeCount;

        CodeIndexStore reader = NewStore();
        bool loaded = reader.LoadCachedSnapshot(Root);

        Assert.True(loaded);
        Assert.Equal(expectedTypes, reader.TypeCount);
        Assert.Contains(reader.SearchSymbol("Alpha", null, null), r => r.Name == "Alpha");
    }

    [Fact]
    public void LoadCachedSnapshot_ThenDeltaRefresh_PicksUpEditsMadeWhileDown()
    {
        NewStore().Build(Root); // seed the cache

        CodeIndexStore store = NewStore();
        Assert.True(store.LoadCachedSnapshot(Root));
        Assert.Empty(store.SearchSymbol("Beta", null, null)); // not in the cached snapshot yet

        // A file added "while the server was down", then the background revalidation (a plain Rebuild) runs.
        _fs.AddFile(@"C:\repo\Proj\B.cs", "namespace N; public class Beta { }");
        store.Build(Root);

        Assert.Contains(store.SearchSymbol("Beta", null, null), r => r.Name == "Beta");
    }

    [Fact]
    public async Task RefreshAsync_CompletesAndPublishes()
    {
        CodeIndexStore store = NewStore();
        Assert.False(store.LoadCachedSnapshot(Root));

        await store.RefreshAsync(Root);

        Assert.True(store.ProjectCount >= 1);
        Assert.Contains(store.SearchSymbol("Alpha", null, null), r => r.Name == "Alpha");
    }
}
