using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Mcp;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Mcp;

/// <summary>
/// Output-shape tests for <see cref="SuggestQueriesTool"/>: the overview counts line, the section headers,
/// the top-project / largest-file listings, the type-kind distribution, and the file/project-dependent
/// suggested queries (present when there is content, absent on an empty index).
/// </summary>
public sealed class SuggestQueriesToolTests
{
    private const string Root = @"C:\repo";

    private readonly InMemoryFileSystem _fs = new();

    public SuggestQueriesToolTests()
    {
        _fs.AddFile(@"C:\repo\App.slnx",
            "<Solution>\n  <Project Path=\"App/App.csproj\" />\n  <Project Path=\"Core/Core.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\App\App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n");
        _fs.AddFile(@"C:\repo\Core\Core.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n");
    }

    private void AddApp(string name, string content) =>
        _fs.AddFile(Path.Combine(@"C:\repo\App", name), content);

    private void AddCore(string name, string content) =>
        _fs.AddFile(Path.Combine(@"C:\repo\Core", name), content);

    private CodeIndexStore Build()
    {
        CodeIndexStore store = new(_fs, new IndexCache(_fs), new TsIndexCache(_fs), CodeIndexConfig.Default);
        store.Build(Root);
        return store;
    }

    [Fact]
    public void EmptyIndex_RendersHeaderAndStaticSuggestions_WithoutFileOrProjectLines()
    {
        CodeIndexStore store = Build();

        string result = SuggestQueriesTool.SuggestQueries(store);

        result.Should().StartWith("# CodeIndex Overview");
        result.Should().Contain("Projects:").And.Contain("Files:").And.Contain("Types:").And.Contain("Members:");
        result.Should().Contain("## Top Projects (by file count)");
        result.Should().Contain("## Largest Files (by symbol count)");
        result.Should().Contain("## Type Distribution");
        result.Should().Contain("## Suggested Queries");

        // Static suggestions always present.
        result.Should().Contain("search_symbol(query='Service', kind='class')");
        result.Should().Contain("get_class_hierarchy(type='IMyInterface')");
        result.Should().Contain("find_references(symbol='YourMethodName')");
        result.Should().Contain("search_text(query='TODO', ignoreCase=true)");

        // No files/projects with content → the content-dependent suggestions are omitted.
        result.Should().NotContain("get_file_outline(file=");
        result.Should().NotContain("repo_info(project=");
        result.Should().NotContain("get_project_dependencies(project=");
    }

    [Fact]
    public void PopulatedIndex_ListsTopProjectAndLargestFile_WithDependentSuggestions()
    {
        AddApp("BigService.cs", """
            namespace N;
            public class BigService
            {
                public int A { get; set; }
                public int B { get; set; }
                public void Run() { }
                public void Stop() { }
            }
            """);
        AddApp("Small.cs", "namespace N; public class Small { }");
        CodeIndexStore store = Build();

        string result = SuggestQueriesTool.SuggestQueries(store);

        // Top projects list includes App and shows a file count.
        result.Should().Contain("App (").And.Contain("files)");

        // Largest file line for the biggest symbol-count file.
        result.Should().Contain("BigService.cs");
        result.Should().Contain("types,").And.Contain("members").And.Contain("(App)");

        // Content-dependent suggestions now appear, seeded from the top file/project.
        result.Should().Contain("get_file_outline(file='BigService.cs')");
        result.Should().Contain("repo_info(project='App')");
        result.Should().Contain("get_project_dependencies(project='App')");
    }

    [Fact]
    public void TypeDistribution_CountsEachKind()
    {
        AddApp("Kinds.cs", """
            namespace N;
            public class PlainClass { }
            public static class StaticThing { }
            public abstract class AbstractThing { }
            public sealed class SealedThing { }
            public interface IThing { }
            public enum Colour { Red, Green }
            """);
        CodeIndexStore store = Build();

        string result = SuggestQueriesTool.SuggestQueries(store);

        result.Should().Contain("## Type Distribution");
        result.Should().Contain("Classes: 1").And.Contain("Static: 1").And.Contain("Abstract: 1").And.Contain("Sealed: 1");
        result.Should().Contain("Interfaces: 1").And.Contain("Enums: 1");
    }
}
