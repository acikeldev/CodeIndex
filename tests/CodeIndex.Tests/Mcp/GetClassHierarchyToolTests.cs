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
            public class SmallCircle : Circle { }
            """);

        // Two same-named types in different namespaces to exercise the ambiguity path.
        _fs.AddFile(@"C:\repo\P\WidgetA.cs", "namespace N1; public class Widget { }");
        _fs.AddFile(@"C:\repo\P\WidgetB.cs", "namespace N2; public class Widget { }");

        // A hierarchy in a SUBFOLDER so the rendered path (Sub/Gadgets.cs) differs from the bare filename —
        // regression guard that get_class_hierarchy renders the project-relative path, not just the filename.
        _fs.AddFile(@"C:\repo\P\Sub\Gadgets.cs", """
            namespace N3;
            public interface IGadget { }
            public class Gizmo : IGadget { }
            """);

        // An inheritance CYCLE (illegal C#, but the syntax-only index can hold it) so the transitive walk's
        // cycle guard is exercised: it must terminate and never list the root as its own descendant.
        _fs.AddFile(@"C:\repo\P\Cycle.cs", """
            namespace N4;
            public class Ouro : Boros { }
            public class Boros : Ouro { }
            """);
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
    public void Implementor_RendersProjectRelativePath_NotBareFilename()
    {
        CodeIndexStore store = BuildStore();

        string output = GetClassHierarchyTool.GetClassHierarchy(store, "IGadget");

        // Subfolder file -> project-relative, forward-slashed path (consistent with the References section),
        // never the bare filename that made a path-seeking task re-fetch each implementor.
        output.Should().Contain("class Gizmo : IGadget [Sub/Gadgets.cs:");
        output.Should().NotContain("[Gadgets.cs:");
    }

    [Fact]
    public void Transitive_IncludesGrandchildrenWithDepthTags()
    {
        CodeIndexStore store = BuildStore();

        string direct = GetClassHierarchyTool.GetClassHierarchy(store, "IShape");
        string transitive = GetClassHierarchyTool.GetClassHierarchy(store, "IShape", transitive: true);

        // Direct: only the one-hop implementors — no grandchild, no depth tags.
        direct.Should().Contain("Derived/Implementors (2):");
        direct.Should().NotContain("SmallCircle");
        direct.Should().NotContain("[d1]");

        // Transitive: the whole subtree with depth markers — Circle/Square at d1, SmallCircle (Circle's child) at d2.
        transitive.Should().Contain("Derived/Implementors (transitive) — 3 across 2 level(s):");
        transitive.Should().Contain("[d1] class Circle : IShape [Shapes.cs");
        transitive.Should().Contain("[d1] class Square : IShape [Shapes.cs");
        transitive.Should().Contain("[d2] class SmallCircle : Circle [Shapes.cs");
    }

    [Fact]
    public void Transitive_CycleTerminatesAndDoesNotSelfList()
    {
        CodeIndexStore store = BuildStore();

        string output = GetClassHierarchyTool.GetClassHierarchy(store, "Ouro", transitive: true);

        // Boros (Ouro's one descendant) is listed once at depth 1; the cycle back to Ouro must terminate and
        // must NOT list the root as its own descendant entry.
        output.Should().Contain("Derived/Implementors (transitive) — 1 across 1 level(s):");
        output.Should().Contain("[d1] class Boros : Ouro [Cycle.cs");
        output.Should().NotContain("class Ouro : Boros");
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
