using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Mcp;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Mcp;

/// <summary>
/// Targeted coverage for <see cref="GetTypeMembersTool"/> kind-filter switch arms (method/property/field/
/// constructor/event) and the member-group label switch (Fields/Events/Methods), plus the ambiguous,
/// partial-merge, and not-found/did-you-mean resolver branches surfaced through the tool.
/// </summary>
public sealed class GetTypeMembersToolCoverageTests
{
    private const string Root = @"C:\repo";

    private readonly InMemoryFileSystem _fs = new();

    public GetTypeMembersToolCoverageTests()
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

    private const string RichType = """
        namespace N;
        public class Rich
        {
            public int Field;
            public event System.Action Changed;
            public Rich() { }
            public int Prop { get; set; }
            public void Run() { }
        }
        """;

    [Fact]
    public void NoFilter_RendersAllGroupLabels()
    {
        AddApp("Rich.cs", RichType);
        CodeIndexStore store = Build();

        string result = GetTypeMembersTool.GetTypeMembers(store, "Rich");

        // Exercises the label switch arms: Constructor/Property/Field/Event/Method (lines 63-66).
        result.Should().Contain("Constructors:");
        result.Should().Contain("Properties:");
        result.Should().Contain("Fields:");
        result.Should().Contain("Events:");
        result.Should().Contain("Methods:");
        result.Should().Contain("Field");
        result.Should().Contain("Changed");
        result.Should().Contain("Prop");
        result.Should().Contain("Run");
    }

    [Fact]
    public void MethodFilter_KeepsOnlyMethods()
    {
        AddApp("Rich.cs", RichType);
        CodeIndexStore store = Build();

        string result = GetTypeMembersTool.GetTypeMembers(store, "Rich", kind: "method");

        result.Should().Contain("Methods:").And.Contain("Run");
        result.Should().NotContain("Fields:");
        result.Should().NotContain("Events:");
        result.Should().NotContain("Properties:");
    }

    [Fact]
    public void PropertyFilter_KeepsOnlyProperties()
    {
        AddApp("Rich.cs", RichType);
        CodeIndexStore store = Build();

        string result = GetTypeMembersTool.GetTypeMembers(store, "Rich", kind: "property");

        result.Should().Contain("Properties:").And.Contain("Prop");
        result.Should().NotContain("Methods:");
        result.Should().NotContain("Fields:");
    }

    [Fact]
    public void FieldFilter_KeepsOnlyFields()
    {
        AddApp("Rich.cs", RichType);
        CodeIndexStore store = Build();

        string result = GetTypeMembersTool.GetTypeMembers(store, "Rich", kind: "field");

        result.Should().Contain("Fields:").And.Contain("Field");
        result.Should().NotContain("Methods:");
        result.Should().NotContain("Events:");
    }

    [Fact]
    public void ConstructorFilter_KeepsOnlyConstructors()
    {
        AddApp("Rich.cs", RichType);
        CodeIndexStore store = Build();

        string result = GetTypeMembersTool.GetTypeMembers(store, "Rich", kind: "constructor");

        result.Should().Contain("Constructors:");
        result.Should().NotContain("Methods:");
        result.Should().NotContain("Fields:");
    }

    [Fact]
    public void EventFilter_KeepsOnlyEvents()
    {
        AddApp("Rich.cs", RichType);
        CodeIndexStore store = Build();

        string result = GetTypeMembersTool.GetTypeMembers(store, "Rich", kind: "event");

        result.Should().Contain("Events:").And.Contain("Changed");
        result.Should().NotContain("Methods:");
        result.Should().NotContain("Fields:");
    }

    [Fact]
    public void Ambiguous_ThenNamespaceDisambiguates()
    {
        AddApp("Dup.cs", "namespace A; public class Dup { public void A1() { } }");
        AddCore("Dup.cs", "namespace B; public class Dup { public void B1() { } }");
        CodeIndexStore store = Build();

        string ambiguous = GetTypeMembersTool.GetTypeMembers(store, "Dup");
        ambiguous.Should().StartWith("AMBIGUOUS:");
        ambiguous.Should().Contain("A").And.Contain("B");

        string picked = GetTypeMembersTool.GetTypeMembers(store, "Dup", @namespace: "B");
        picked.Should().Contain("B1");
        picked.Should().NotContain("AMBIGUOUS:");
    }

    [Fact]
    public void PartialClass_MergedAcrossTwoFiles()
    {
        AddApp("P.Part1.cs", "namespace N; public partial class P { public void One() { } }");
        AddApp("P.Part2.cs", "namespace N; public partial class P { public void Two() { } }");
        CodeIndexStore store = Build();

        string result = GetTypeMembersTool.GetTypeMembers(store, "P");

        result.Should().Contain("(partial: 2 files)");
        result.Should().Contain("One").And.Contain("Two");
    }

    [Fact]
    public void MissingType_ReturnsNotFoundWithGuidance()
    {
        AddApp("Rich.cs", RichType);
        CodeIndexStore store = Build();

        string result = GetTypeMembersTool.GetTypeMembers(store, "Nonexistent");

        result.Should().Contain("not found in index.");
        result.Should().Contain("search_symbol");
    }
}
