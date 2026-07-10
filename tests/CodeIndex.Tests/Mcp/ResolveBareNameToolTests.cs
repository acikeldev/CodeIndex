using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Mcp;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Mcp;

/// <summary>
/// Tool-output tests for <see cref="ResolveBareNameTool.ResolveBareName"/>: builds a tiny fixture repo with two
/// same-named types in different namespaces, then asserts on each output shape — file-not-found (with did-you-mean),
/// single RESOLVED binding, AMBIGUOUS (two imported candidates), alias resolution, UNRESOLVED, and the
/// "also defined but NOT imported" out-of-scope listing.
/// </summary>
public sealed class ResolveBareNameToolTests
{
    private const string Root = @"C:\repo";

    private readonly InMemoryFileSystem _fs = new();

    public ResolveBareNameToolTests()
    {
        _fs.AddFile(@"C:\repo\Fixture.slnx",
            "<Solution>\n  <Project Path=\"Proj/Proj.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\Proj\Proj.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n");

        // Two same-named types living in different namespaces.
        _fs.AddFile(@"C:\repo\Proj\Alpha.cs", "namespace Alpha;\npublic class Dup { }\n");
        _fs.AddFile(@"C:\repo\Proj\Beta.cs", "namespace Beta;\npublic class Dup { }\n");

        // A type unique to the file's own namespace.
        _fs.AddFile(@"C:\repo\Proj\Home.cs", "namespace App;\npublic class Home { }\npublic class Widget { }\n");

        // Imports BOTH namespaces that define Dup — bare Dup is ambiguous here.
        _fs.AddFile(@"C:\repo\Proj\AmbCaller.cs",
            "using Alpha;\nusing Beta;\n\nnamespace Client;\n\npublic class AmbCaller { }\n");

        // Imports only Alpha — bare Dup resolves to Alpha.Dup, Beta.Dup is out of scope.
        _fs.AddFile(@"C:\repo\Proj\ScopeCaller.cs",
            "using Alpha;\n\nnamespace Client2;\n\npublic class ScopeCaller { }\n");

        // An alias directive wins outright.
        _fs.AddFile(@"C:\repo\Proj\AliasHost.cs",
            "using D = Alpha.Dup;\n\nnamespace Client3;\n\npublic class AliasHost { }\n");
    }

    private CodeIndexStore Build()
    {
        CodeIndexStore store = new(_fs, new IndexCache(_fs), new TsIndexCache(_fs), CodeIndexConfig.Default);
        store.Build(Root);
        return store;
    }

    [Fact]
    public void FileNotIndexed_ReturnsNotIndexedMessageWithDidYouMean()
    {
        CodeIndexStore store = Build();

        string output = ResolveBareNameTool.ResolveBareName(store, "Nope.cs", "Dup");

        output.Should().Contain("File 'Nope.cs' is not indexed");
        output.Should().Contain("Try the exact file name");
    }

    [Fact]
    public void SameNamespaceType_ResolvesToSingleBinding()
    {
        CodeIndexStore store = Build();

        string output = ResolveBareNameTool.ResolveBareName(store, "Home.cs", "Widget");

        output.Should().StartWith("RESOLVED: bare 'Widget' in Home.cs (namespace App) binds to");
        output.Should().Contain("App.Widget");
        output.Should().Contain("[Home.cs]");
        output.Should().Contain("(same/enclosing namespace)");
    }

    [Fact]
    public void TwoImportedNamespaces_ReportsAmbiguous()
    {
        CodeIndexStore store = Build();

        string output = ResolveBareNameTool.ResolveBareName(store, "AmbCaller.cs", "Dup");

        output.Should().Contain("AMBIGUOUS: bare 'Dup' in AmbCaller.cs (namespace Client) could bind to 2 IMPORTED types");
        output.Should().Contain("CS0104");
        output.Should().Contain("Alpha.Dup");
        output.Should().Contain("Beta.Dup");
        output.Should().Contain("using Alpha;");
        output.Should().Contain("using Beta;");
    }

    [Fact]
    public void AliasDirective_ResolvesToAliasTarget()
    {
        CodeIndexStore store = Build();

        string output = ResolveBareNameTool.ResolveBareName(store, "AliasHost.cs", "D");

        output.Should().Contain("RESOLVED (alias): bare 'D' in AliasHost.cs (namespace Client3) binds to");
        output.Should().Contain("Alpha.Dup");
        output.Should().Contain("A `using D = ...;` directive makes this unambiguous.");
    }

    [Fact]
    public void UnknownIdentifier_ReportsUnresolved()
    {
        CodeIndexStore store = Build();

        string output = ResolveBareNameTool.ResolveBareName(store, "Home.cs", "Nonexistent");

        output.Should().Contain("UNRESOLVED: bare 'Nonexistent' in Home.cs (namespace App) matches no imported type");
    }

    [Fact]
    public void ImportedPlusUnimportedTwin_ResolvesAndListsOutOfScope()
    {
        CodeIndexStore store = Build();

        string output = ResolveBareNameTool.ResolveBareName(store, "ScopeCaller.cs", "Dup");

        // Alpha is imported -> single in-scope binding.
        output.Should().Contain("RESOLVED: bare 'Dup' in ScopeCaller.cs (namespace Client2) binds to");
        output.Should().Contain("Alpha.Dup");
        // Beta.Dup exists but is not imported here.
        output.Should().Contain("Also defined but NOT imported here (1)");
        output.Should().Contain("Beta.Dup");
    }
}
