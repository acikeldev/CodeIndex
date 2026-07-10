using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Mcp;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Mcp;

/// <summary>
/// Output-shape tests for <see cref="GetTypeMembersTool"/>: header formatting, kind grouping/filtering,
/// the unknown-kind and not-found/ambiguity messages, partial-type merging, and the empty-members case.
/// </summary>
public sealed class GetTypeMembersToolTests
{
    private const string Root = @"C:\repo";

    private readonly InMemoryFileSystem _fs = new();

    public GetTypeMembersToolTests()
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
    public void UnknownKind_ReturnsValidKindsMessage()
    {
        AddApp("Svc.cs", "namespace N; public class Svc { public void M() { } }");
        CodeIndexStore store = Build();

        string result = GetTypeMembersTool.GetTypeMembers(store, "Svc", kind: "banana");

        result.Should().StartWith("Unknown member kind 'banana'.");
        result.Should().Contain("Valid kinds:").And.Contain("method").And.Contain("constructor");
    }

    [Fact]
    public void KnownType_RendersHeaderAndGroupedMembers()
    {
        AddApp("Svc.cs", """
            namespace N;
            public class Svc
            {
                public Svc() { }
                public int Count { get; set; }
                public void Run() { }
            }
            """);
        CodeIndexStore store = Build();

        string result = GetTypeMembersTool.GetTypeMembers(store, "Svc");

        result.Should().Contain("# ").And.Contain("Svc").And.Contain("(App)");
        result.Should().Contain("Constructors:");
        result.Should().Contain("Properties:");
        result.Should().Contain("Methods:");
        result.Should().Contain("Count");
        result.Should().Contain("Run");
    }

    [Fact]
    public void KindFilter_RestrictsToRequestedKind()
    {
        AddApp("Svc.cs", """
            namespace N;
            public class Svc
            {
                public int Count { get; set; }
                public void Run() { }
            }
            """);
        CodeIndexStore store = Build();

        string result = GetTypeMembersTool.GetTypeMembers(store, "Svc", kind: "property");

        result.Should().Contain("Properties:").And.Contain("Count");
        result.Should().NotContain("Methods:");
        result.Should().NotContain("Run");
    }

    [Fact]
    public void CtorAlias_IsAcceptedAndFilters()
    {
        AddApp("Svc.cs", """
            namespace N;
            public class Svc
            {
                public Svc() { }
                public void Run() { }
            }
            """);
        CodeIndexStore store = Build();

        string result = GetTypeMembersTool.GetTypeMembers(store, "Svc", kind: "ctor");

        result.Should().Contain("Constructors:");
        result.Should().NotContain("Methods:");
    }

    [Fact]
    public void NotFound_ReturnsResolverErrorWithGuidance()
    {
        AddApp("Svc.cs", "namespace N; public class Svc { }");
        CodeIndexStore store = Build();

        string result = GetTypeMembersTool.GetTypeMembers(store, "DoesNotExist");

        result.Should().Contain("not found in index.");
        result.Should().Contain("search_symbol");
    }

    [Fact]
    public void AmbiguousAcrossNamespaces_ReturnsDisambiguationMessage()
    {
        AddApp("Thing.cs", "namespace App.One; public class Thing { public void A() { } }");
        AddCore("Thing.cs", "namespace Core.Two; public class Thing { public void B() { } }");
        CodeIndexStore store = Build();

        string ambiguous = GetTypeMembersTool.GetTypeMembers(store, "Thing");
        ambiguous.Should().StartWith("AMBIGUOUS:");
        ambiguous.Should().Contain("App.One").And.Contain("Core.Two");

        string picked = GetTypeMembersTool.GetTypeMembers(store, "Thing", @namespace: "One");
        picked.Should().Contain("# ").And.Contain("Thing");
        picked.Should().Contain("A");
        picked.Should().NotContain("AMBIGUOUS:");
    }

    [Fact]
    public void ProjectFilter_Disambiguates()
    {
        AddApp("Thing.cs", "namespace App.One; public class Thing { public void A() { } }");
        AddCore("Thing.cs", "namespace Core.Two; public class Thing { public void B() { } }");
        CodeIndexStore store = Build();

        string result = GetTypeMembersTool.GetTypeMembers(store, "Thing", project: "Core");

        result.Should().Contain("(Core)").And.Contain("B");
        result.Should().NotContain("AMBIGUOUS:");
    }

    [Fact]
    public void PartialType_MergesPartsAndReportsFileCount()
    {
        AddApp("Widget.Part1.cs", "namespace N; public partial class Widget { public void First() { } }");
        AddApp("Widget.Part2.cs", "namespace N; public partial class Widget { public void Second() { } }");
        CodeIndexStore store = Build();

        string result = GetTypeMembersTool.GetTypeMembers(store, "Widget");

        result.Should().Contain("(partial: 2 files)");
        result.Should().Contain("First").And.Contain("Second");
    }

    [Fact]
    public void EmptyType_ReportsNoMembers()
    {
        AddApp("Empty.cs", "namespace N; public class Empty { }");
        CodeIndexStore store = Build();

        string result = GetTypeMembersTool.GetTypeMembers(store, "Empty");

        result.Should().Contain("# ").And.Contain("Empty");
        result.Should().Contain("No members.");
    }
}
