using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Mcp;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Mcp;

/// <summary>
/// Output-formatting tests for <see cref="RepoMapTool"/>: verifies the ranked-map header/footer over a
/// small two-project fixture, focus= parsing, the project filter, and the empty-index message.
/// </summary>
public sealed class RepoMapToolTests
{
    private const string Root = @"C:\repo";
    private readonly InMemoryFileSystem _fs = new();

    private CodeIndexStore Build()
    {
        CodeIndexStore store = new(_fs, new IndexCache(_fs), new TsIndexCache(_fs), CodeIndexConfig.Default);
        store.Build(Root);
        return store;
    }

    private void SeedTwoProjects()
    {
        _fs.AddFile(@"C:\repo\App.slnx",
            "<Solution>\n  <Project Path=\"App/App.csproj\" />\n  <Project Path=\"Core/Core.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\App\App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        _fs.AddFile(@"C:\repo\App\Consumer.cs",
            "namespace App; public class Consumer { public void Run() { Widget w = new Widget(); w.Do(); } }");
        _fs.AddFile(@"C:\repo\Core\Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        _fs.AddFile(@"C:\repo\Core\Widget.cs",
            "namespace Core; public class Widget { public void Do() { } }");
    }

    [Fact]
    public void RepoMap_RendersRankedHeaderAndSymbols()
    {
        SeedTwoProjects();
        CodeIndexStore store = Build();

        string output = RepoMapTool.RepoMap(store);

        output.Should().Contain("# Repo map — most important symbols (PageRank-ranked, token-budgeted)");
        output.Should().Contain("Widget");
    }

    [Fact]
    public void RepoMap_FocusParsesCommaSeparatedNames()
    {
        SeedTwoProjects();
        CodeIndexStore store = Build();

        // Comma-separated, whitespace-padded focus tokens should be split/trimmed and not throw.
        string output = RepoMapTool.RepoMap(store, focus: " Widget , Consumer ");

        output.Should().Contain("# Repo map — most important symbols (PageRank-ranked, token-budgeted)");
    }

    [Fact]
    public void RepoMap_ProjectFilterLimitsOutput()
    {
        SeedTwoProjects();
        CodeIndexStore store = Build();

        string output = RepoMapTool.RepoMap(store, project: "Core");

        output.Should().Contain("Widget");
        output.Should().NotContain("(App)");
    }

    [Fact]
    public void RepoMap_EmptyIndex_ReturnsNoSymbolsMessage()
    {
        CodeIndexStore store = Build();

        string output = RepoMapTool.RepoMap(store);

        output.Should().Be("repo_map: no hand-written symbols indexed (empty index or all generated).");
    }
}
