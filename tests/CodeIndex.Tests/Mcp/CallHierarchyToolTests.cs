using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Mcp;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Mcp;

/// <summary>
/// Tool-output tests for <see cref="CallHierarchyTool"/>. The tool is a thin delegate over
/// <see cref="CodeIndexStore.GetCallHierarchy"/>, so these build a tiny real store fixture and assert on the
/// rendered text for each direction and language selector.
/// </summary>
public sealed class CallHierarchyToolTests
{
    private const string Root = @"C:\repo";

    private readonly InMemoryFileSystem _fs = new();

    public CallHierarchyToolTests()
    {
        _fs.AddFile(@"C:\repo\Fixture.slnx",
            "<Solution>\n  <Project Path=\"Proj/Proj.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\Proj\Proj.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n");
        _fs.AddFile(@"C:\repo\Proj\A.cs",
            "namespace N;\npublic class A\n{\n    public void Foo() { }\n    public void Bar() { Foo(); }\n}\n");
    }

    private CodeIndexStore Build()
    {
        CodeIndexStore store = new(_fs, new IndexCache(_fs), new TsIndexCache(_fs), CodeIndexConfig.Default);
        store.Build(Root);
        return store;
    }

    [Fact]
    public void Callers_Csharp_ReportsInvocationWithEnclosingMember()
    {
        CodeIndexStore store = Build();

        string output = CallHierarchyTool.CallHierarchy(store, "Foo", direction: "callers", language: "csharp");

        output.Should().Contain("Foo: 1 call sites in 1 files");
        output.Should().Contain("== A.cs (Proj) — 1 ==");
        output.Should().Contain("Bar: public void Bar() { Foo(); }");
    }

    [Fact]
    public void Callers_Csharp_NoMatch_ReportsNoInvocations()
    {
        CodeIndexStore store = Build();

        string output = CallHierarchyTool.CallHierarchy(store, "Nonexistent", direction: "callers", language: "csharp");

        output.Should().Contain("no invocations of 'Nonexistent' found");
    }

    [Fact]
    public void Callees_Csharp_ListsInvokedNames()
    {
        CodeIndexStore store = Build();

        string output = CallHierarchyTool.CallHierarchy(store, "Bar", direction: "callees", language: "csharp");

        output.Should().Contain("Bar calls (1 distinct callees)");
        output.Should().Contain("A.cs (Proj)");
        output.Should().Contain("Foo (×1)");
    }

    [Fact]
    public void Callees_Csharp_UnknownMethod_ReportsNotIndexed()
    {
        CodeIndexStore store = Build();

        string output = CallHierarchyTool.CallHierarchy(store, "Ghost", direction: "callees", language: "csharp");

        output.Should().Contain("no C# method named 'Ghost' is indexed");
    }

    [Fact]
    public void Both_Direction_Csharp_ShowsCallersAndCallees()
    {
        CodeIndexStore store = Build();

        string output = CallHierarchyTool.CallHierarchy(store, "Foo", direction: "both", language: "csharp");

        output.Should().Contain("Foo: 1 call sites in 1 files");
        output.Should().Contain("Foo calls");
    }

    [Fact]
    public void Language_Both_SpansCsharpAndTypeScriptSections()
    {
        CodeIndexStore store = Build();

        string output = CallHierarchyTool.CallHierarchy(store, "Foo", direction: "callers", language: "both");

        output.Should().Contain("## C#");
        output.Should().Contain("## TypeScript");
        output.Should().Contain("Foo: 1 call sites in 1 files");
    }

    [Fact]
    public void Language_TypeScript_WithNoTsFiles_ReportsNoInvocations()
    {
        CodeIndexStore store = Build();

        string output = CallHierarchyTool.CallHierarchy(store, "Foo", direction: "callers", language: "typescript");

        output.Should().Contain("call_hierarchy(TS callers): no invocations of 'Foo' found");
    }

    [Fact]
    public void ProjectFilter_ScopesToNamedProject()
    {
        CodeIndexStore store = Build();

        string output = CallHierarchyTool.CallHierarchy(store, "Foo", direction: "callers", language: "csharp", project: "Proj");

        output.Should().Contain("Foo: 1 call sites in 1 files");
    }
}
