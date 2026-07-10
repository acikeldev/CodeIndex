using System.Text;
using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Mcp;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Mcp;

/// <summary>
/// Output-formatting tests for the get_project_dependencies MCP tool: forward references, reverse
/// dependents, the not-found + did-you-mean path, and the default truncation vs. full=true behaviour.
/// </summary>
public sealed class GetProjectDependenciesToolTests
{
    private const string Root = @"C:\repo";

    private static CodeIndexStore BuildStore(InMemoryFileSystem fs)
    {
        CodeIndexStore store = new(fs, new IndexCache(fs), new TsIndexCache(fs), CodeIndexConfig.Default);
        store.Build(Root);
        return store;
    }

    /// <summary>App references Core; Core is referenced by App.</summary>
    private static CodeIndexStore BuildSimpleGraph()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(@"C:\repo\App.slnx",
            "<Solution>\n  <Project Path=\"App/App.csproj\" />\n  <Project Path=\"Core/Core.csproj\" />\n</Solution>\n");
        fs.AddFile(@"C:\repo\App\App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"..\\Core\\Core.csproj\" /></ItemGroup></Project>\n");
        fs.AddFile(@"C:\repo\App\Svc.cs", "namespace App; public class Svc { }");
        fs.AddFile(@"C:\repo\Core\Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        fs.AddFile(@"C:\repo\Core\Thing.cs", "namespace Core; public class Thing { }");
        return BuildStore(fs);
    }

    [Fact]
    public void Project_WithReferences_And_NoDependents()
    {
        CodeIndexStore store = BuildSimpleGraph();

        string output = GetProjectDependenciesTool.GetProjectDependencies(store, "App");

        output.Should().Contain("# App");
        output.Should().Contain("References (1):");
        output.Should().Contain("  Core");
        output.Should().Contain("Referenced by: none");
    }

    [Fact]
    public void Project_ReferencedByOthers_ShowsDependents()
    {
        CodeIndexStore store = BuildSimpleGraph();

        string output = GetProjectDependenciesTool.GetProjectDependencies(store, "Core");

        output.Should().Contain("# Core");
        output.Should().Contain("References: none");
        output.Should().Contain("Referenced by (1):");
        output.Should().Contain("  App");
    }

    [Fact]
    public void UnknownProject_ReturnsNotFound_WithSuggestionAndHint()
    {
        CodeIndexStore store = BuildSimpleGraph();

        string output = GetProjectDependenciesTool.GetProjectDependencies(store, "Cor");

        output.Should().StartWith("Project 'Cor' not found.");
        output.Should().Contain("Did you mean");
        output.Should().Contain("Core");
        output.Should().Contain("Try list_projects for the exact project names.");
    }

    /// <summary>17 leaf projects each reference the same Shared project, so it has 17 dependents.</summary>
    private static CodeIndexStore BuildFanInGraph(int dependents)
    {
        InMemoryFileSystem fs = new();
        StringBuilder sln = new();
        sln.Append("<Solution>\n");
        sln.Append("  <Project Path=\"Shared/Shared.csproj\" />\n");
        for (int i = 0; i < dependents; i++)
        {
            sln.Append($"  <Project Path=\"P{i:D2}/P{i:D2}.csproj\" />\n");
        }
        sln.Append("</Solution>\n");
        fs.AddFile(@"C:\repo\Fan.slnx", sln.ToString());

        fs.AddFile(@"C:\repo\Shared\Shared.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        fs.AddFile(@"C:\repo\Shared\S.cs", "namespace Shared; public class S { }");
        for (int i = 0; i < dependents; i++)
        {
            fs.AddFile($@"C:\repo\P{i:D2}\P{i:D2}.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"..\\Shared\\Shared.csproj\" /></ItemGroup></Project>\n");
            fs.AddFile($@"C:\repo\P{i:D2}\C.cs", $"namespace P{i:D2}; public class C {{ }}");
        }
        return BuildStore(fs);
    }

    [Fact]
    public void ManyDependents_DefaultTruncatesToFifteen()
    {
        CodeIndexStore store = BuildFanInGraph(17);

        string output = GetProjectDependenciesTool.GetProjectDependencies(store, "Shared");

        output.Should().Contain("Referenced by (17, showing 15):");
        output.Should().Contain("  … 2 more (use full=true)");
    }

    [Fact]
    public void ManyDependents_FullFlagListsAll()
    {
        CodeIndexStore store = BuildFanInGraph(17);

        string output = GetProjectDependenciesTool.GetProjectDependencies(store, "Shared", full: true);

        output.Should().Contain("Referenced by (17):");
        output.Should().NotContain("more (use full=true)");
        output.Should().Contain("  P00");
        output.Should().Contain("  P16");
    }
}
