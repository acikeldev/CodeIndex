using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Mcp;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Mcp;

/// <summary>
/// Output tests for <see cref="RepoInfoTool"/> over a real index: the no-argument overview (status + project
/// list) and the project= file listing (found / not-found). Age-formatting and mocked branches live in
/// <see cref="RepoInfoToolCoverageTests"/>.
/// </summary>
public sealed class RepoInfoToolTests
{
    private const string Root = @"C:\repo";
    private readonly InMemoryFileSystem _fs = new();

    private CodeIndexStore Build()
    {
        _fs.AddFile(@"C:\repo\App.slnx",
            "<Solution>\n  <Project Path=\"App/App.csproj\" />\n  <Project Path=\"Core/Core.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\App\App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        _fs.AddFile(@"C:\repo\App\A.cs", "namespace App; public class A { }");
        _fs.AddFile(@"C:\repo\App\B.cs", "namespace App; public class B { }");
        _fs.AddFile(@"C:\repo\Core\Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        _fs.AddFile(@"C:\repo\Core\Thing.cs", "namespace Core; public class Thing { }");
        CodeIndexStore store = new(_fs, new IndexCache(_fs), new TsIndexCache(_fs), CodeIndexConfig.Default);
        store.Build(Root);
        return store;
    }

    [Fact]
    public void Overview_ShowsStatusAndProjectList()
    {
        CodeIndexStore store = Build();

        string output = RepoInfoTool.RepoInfo(store, new IndexCache(_fs));

        output.Should().Contain("# CodeIndex status");
        output.Should().Contain(@"Repo root: C:\repo");
        output.Should().Contain("2 projects");
        output.Should().Contain("Cache:");
        output.Should().Contain("Projects:");
        output.Should().Contain("App (2 files)");
        output.Should().Contain("Core (1 files)");
    }

    [Fact]
    public void Project_ListsSourceFiles()
    {
        CodeIndexStore store = Build();

        string output = RepoInfoTool.RepoInfo(store, new IndexCache(_fs), project: "App");

        output.Should().Contain("A.cs");
        output.Should().Contain("B.cs");
        output.Should().NotContain("# CodeIndex status");   // drilled into the project, not the overview
    }

    [Fact]
    public void Project_NotFound_ReturnsMessage()
    {
        CodeIndexStore store = Build();

        string output = RepoInfoTool.RepoInfo(store, new IndexCache(_fs), project: "Nope");

        output.Should().Be("Project 'Nope' not found.");
    }
}
