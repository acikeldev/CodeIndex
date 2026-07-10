using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Mcp;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Mcp;

/// <summary>
/// Output-formatting tests for get_class_hierarchy: header line, Inherits line, derived/implementor listing,
/// the empty-derived message, not-found (with did-you-mean), and the ambiguous-namespace disambiguation path.
/// </summary>
public sealed class GetClassHierarchyToolTests
{
    private const string Root = @"C:\repo";
    private readonly InMemoryFileSystem _fs = new();

    public GetClassHierarchyToolTests()
    {
        _fs.AddFile(@"C:\repo\App.slnx",
            "<Solution>\n  <Project Path=\"P/P.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\P\P.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");

        _fs.AddFile(@"C:\repo\P\Shapes.cs", """
            namespace N;
            public interface IShape { }
            public class Circle : IShape { }
            public class Square : IShape { }
            public class Animal { }
            public class Dog : Animal { }
            """);

        // Two same-named types in different namespaces to exercise the ambiguity path.
        _fs.AddFile(@"C:\repo\P\WidgetA.cs", "namespace N1; public class Widget { }");
        _fs.AddFile(@"C:\repo\P\WidgetB.cs", "namespace N2; public class Widget { }");
    }

    private CodeIndexStore BuildStore()
    {
        CodeIndexStore store = new(_fs, new IndexCache(_fs), new TsIndexCache(_fs), CodeIndexConfig.Default);
        store.Build(Root);
        return store;
    }

    [Fact]
    public void Interface_ListsImplementorsSortedByName()
    {
        CodeIndexStore store = BuildStore();

        string output = GetClassHierarchyTool.GetClassHierarchy(store, "IShape");

        output.Should().StartWith("# interface IShape [");
        output.Should().Contain("(P)");
        output.Should().Contain("Derived/Implementors (2):");
        output.Should().Contain("class Circle : IShape [Shapes.cs");
        output.Should().Contain("class Square : IShape [Shapes.cs");
        // Interfaces here have no base list.
        output.Should().NotContain("Inherits:");
        // Circle sorts before Square.
        output.IndexOf("Circle", StringComparison.Ordinal)
            .Should().BeLessThan(output.IndexOf("Square", StringComparison.Ordinal));
    }

    [Fact]
    public void BaseClass_ListsDerivedType()
    {
        CodeIndexStore store = BuildStore();

        string output = GetClassHierarchyTool.GetClassHierarchy(store, "Animal");

        output.Should().StartWith("# class Animal [");
        output.Should().Contain("Derived/Implementors (1):");
        output.Should().Contain("class Dog : Animal [Shapes.cs");
    }

    [Fact]
    public void DerivedType_ShowsInheritsLine_AndNoDerived()
    {
        CodeIndexStore store = BuildStore();

        string output = GetClassHierarchyTool.GetClassHierarchy(store, "Dog");

        output.Should().StartWith("# class Dog [");
        output.Should().Contain("Inherits: Animal");
        output.Should().Contain("No derived types or implementors found.");
    }

    [Fact]
    public void UnknownType_ReturnsNotFoundMessage()
    {
        CodeIndexStore store = BuildStore();

        string output = GetClassHierarchyTool.GetClassHierarchy(store, "Nonexistent");

        output.Should().Contain("Type 'Nonexistent'");
        output.Should().Contain("not found in index.");
    }

    [Fact]
    public void AmbiguousType_ReturnsDisambiguationMessage()
    {
        CodeIndexStore store = BuildStore();

        string output = GetClassHierarchyTool.GetClassHierarchy(store, "Widget");

        output.Should().StartWith("AMBIGUOUS:");
        output.Should().Contain("types named 'Widget'");
        output.Should().Contain("N1");
        output.Should().Contain("N2");
    }

    [Fact]
    public void NamespaceFilter_ResolvesAmbiguity()
    {
        CodeIndexStore store = BuildStore();

        string output = GetClassHierarchyTool.GetClassHierarchy(store, "Widget", @namespace: "N1");

        output.Should().StartWith("# class Widget [");
        output.Should().NotContain("AMBIGUOUS");
    }
}
