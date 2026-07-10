using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Mcp;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Mcp;

/// <summary>
/// Output-formatting coverage for <see cref="ListFilesTool"/>: the newline-joined file list for a known
/// project, and the not-found message for an unknown one.
/// </summary>
public sealed class ListFilesToolTests
{
    private const string Root = @"C:\repo";

    private static CodeIndexStore BuildStore()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(@"C:\repo\App.slnx",
            "<Solution>\n  <Project Path=\"App/App.csproj\" />\n</Solution>\n");
        fs.AddFile(@"C:\repo\App\App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        fs.AddFile(@"C:\repo\App\A.cs", "namespace App; public class A { public void M() { } }");
        fs.AddFile(@"C:\repo\App\B.cs", "namespace App; public class B { }");

        CodeIndexStore store = new(fs, new IndexCache(fs), new TsIndexCache(fs), CodeIndexConfig.Default);
        store.Build(Root);
        return store;
    }

    [Fact]
    public void ListFiles_KnownProject_ReturnsNewlineJoinedSourceFiles()
    {
        CodeIndexStore store = BuildStore();

        string result = ListFilesTool.ListFiles(store, "App");

        string[] lines = result.Split('\n');
        lines.Should().Contain(l => l.EndsWith("A.cs"));
        lines.Should().Contain(l => l.EndsWith("B.cs"));
        result.Should().NotContain("not found");
    }

    [Fact]
    public void ListFiles_UnknownProject_ReturnsNotFoundMessage()
    {
        CodeIndexStore store = BuildStore();

        string result = ListFilesTool.ListFiles(store, "Nope");

        result.Should().Be("Project 'Nope' not found.");
    }
}
