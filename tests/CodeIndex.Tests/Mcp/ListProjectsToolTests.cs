using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Mcp;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Mcp;

/// <summary>
/// Output-formatting tests for <see cref="ListProjectsTool"/>: verifies the per-project line format and file
/// counts over a two-project fixture, plus the empty-index message.
/// </summary>
public sealed class ListProjectsToolTests
{
    private const string Root = @"C:\repo";
    private readonly InMemoryFileSystem _fs = new();

    private CodeIndexStore Build()
    {
        CodeIndexStore store = new(_fs, new IndexCache(_fs), new TsIndexCache(_fs), CodeIndexConfig.Default);
        store.Build(Root);
        return store;
    }

    [Fact]
    public void ListProjects_ListsProjectsWithFileCounts()
    {
        _fs.AddFile(@"C:\repo\App.slnx",
            "<Solution>\n  <Project Path=\"App/App.csproj\" />\n  <Project Path=\"Core/Core.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\App\App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        _fs.AddFile(@"C:\repo\App\A.cs", "namespace App; public class A { }");
        _fs.AddFile(@"C:\repo\App\B.cs", "namespace App; public class B { }");
        _fs.AddFile(@"C:\repo\Core\Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        _fs.AddFile(@"C:\repo\Core\Thing.cs", "namespace Core; public class Thing { }");

        CodeIndexStore store = Build();

        string output = ListProjectsTool.ListProjects(store);

        output.Should().Contain("App (2 files)");
        output.Should().Contain("Core (1 files)");
    }

    [Fact]
    public void ListProjects_NoProjects_ReturnsEmptyMessage()
    {
        CodeIndexStore store = Build();

        string output = ListProjectsTool.ListProjects(store);

        output.Should().Be("No projects indexed.");
    }
}
