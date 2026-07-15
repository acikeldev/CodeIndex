using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Mcp;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Mcp;

/// <summary>
/// Tool-output tests for <see cref="SearchTextTool.SearchText"/>: builds a tiny fixture repo over the in-memory
/// file system, then asserts on the grouped text-search output (summary line, per-file headers, context windows,
/// generated-file demotion, project filtering, regex handling and the not-found / invalid-regex messages).
/// </summary>
public sealed class SearchTextToolTests
{
    private const string Root = @"C:\repo";

    private readonly InMemoryFileSystem _fs = new();

    public SearchTextToolTests()
    {
        _fs.AddFile(@"C:\repo\Fixture.slnx",
            "<Solution>\n  <Project Path=\"Proj/Proj.csproj\" />\n  <Project Path=\"Other/Other.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\Proj\Proj.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n");
        _fs.AddFile(@"C:\repo\Other\Other.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n");
    }

    private void AddProjFile(string name, string content) =>
        _fs.AddFile(Path.Combine(@"C:\repo\Proj", name), content);

    private void AddOtherFile(string name, string content) =>
        _fs.AddFile(Path.Combine(@"C:\repo\Other", name), content);

    private CodeIndexStore Build()
    {
        CodeIndexStore store = new(_fs, new IndexCache(_fs), new TsIndexCache(_fs), CodeIndexConfig.Default);
        store.Build(Root);
        return store;
    }

    [Fact]
    public void LiteralMatch_GroupsByFileWithSummaryAndHeader()
    {
        AddProjFile("A.cs", "namespace N;\npublic class A { public void M() { string s = \"needle\"; } }");
        CodeIndexStore store = Build();

        string output = SearchTextTool.SearchText(store, _fs, "needle");

        output.Should().Contain("needle: 1 matches in 1 files");
        output.Should().Contain("A.cs(1)");
        output.Should().Contain("== Proj/A.cs ==");
        output.Should().Contain("needle");
        // The find_references-only 'Usage:' facet must NOT leak into search_text (which matches string literals
        // by design); the shared formatter keeps ClassifyReferences off for search_text.
        output.Should().NotContain("Usage:");
    }

    [Fact]
    public void NoMatch_ReturnsNotFoundMessage()
    {
        AddProjFile("A.cs", "namespace N;\npublic class A { }");
        CodeIndexStore store = Build();

        string output = SearchTextTool.SearchText(store, _fs, "absent-token");

        output.Should().Be("No matches found for 'absent-token'.");
    }

    [Fact]
    public void ProjectFilter_ExcludesOtherProject()
    {
        AddProjFile("A.cs", "namespace N;\npublic class A { /* marker */ }");
        AddOtherFile("B.cs", "namespace M;\npublic class B { /* marker */ }");
        CodeIndexStore store = Build();

        string output = SearchTextTool.SearchText(store, _fs, "marker", project: "Proj");

        output.Should().Contain("== Proj/A.cs ==");
        output.Should().NotContain("B.cs");
    }

    [Fact]
    public void CaseInsensitive_MatchesRegardlessOfCase()
    {
        AddProjFile("A.cs", "namespace N;\npublic class A { /* NEEDLE */ }");
        CodeIndexStore store = Build();

        SearchTextTool.SearchText(store, _fs, "needle").Should().Be("No matches found for 'needle'.");
        SearchTextTool.SearchText(store, _fs, "needle", ignoreCase: true).Should().Contain("== Proj/A.cs ==");
    }

    [Fact]
    public void Regex_MatchesPattern()
    {
        AddProjFile("A.cs", "namespace N;\npublic class A { int x = 12345; }");
        CodeIndexStore store = Build();

        string output = SearchTextTool.SearchText(store, _fs, @"\d{5}", isRegex: true);

        output.Should().Contain("== Proj/A.cs ==");
        output.Should().Contain("12345");
    }

    [Fact]
    public void InvalidRegex_ReturnsError()
    {
        AddProjFile("A.cs", "namespace N;\npublic class A { }");
        CodeIndexStore store = Build();

        string output = SearchTextTool.SearchText(store, _fs, "(unclosed", isRegex: true);

        output.Should().StartWith("Invalid regex:");
    }

    [Fact]
    public void ContextLines_EmitsSurroundingLines()
    {
        AddProjFile("A.cs", "line-before\nthe-needle-line\nline-after");
        CodeIndexStore store = Build();

        string output = SearchTextTool.SearchText(store, _fs, "the-needle-line", contextLines: 1);

        output.Should().Contain("line-before");
        output.Should().Contain("the-needle-line");
        output.Should().Contain("line-after");
    }

    [Fact]
    public void GeneratedFile_IsDemotedButCountedInSummary()
    {
        AddProjFile("A.cs", "namespace N;\npublic class A { /* marker */ }");
        AddProjFile("Gen.g.cs", "namespace N;\npublic partial class Gen { /* marker */ }");
        CodeIndexStore store = Build();

        string output = SearchTextTool.SearchText(store, _fs, "marker");

        // Both files counted in the summary; the generated one is tagged.
        output.Should().Contain("marker: 2 matches in 2 files");
        output.Should().Contain("Gen.g.cs(1, gen)");
        // Body order demotes the generated file below the hand-written one.
        output.IndexOf("== Proj/A.cs ==", StringComparison.Ordinal)
            .Should().BeLessThan(output.IndexOf("== Proj/Gen.g.cs ==", StringComparison.Ordinal));
    }

    [Fact]
    public void PerFileMax_LimitsSamplesButReportsTrueCount()
    {
        AddProjFile("A.cs", "marker\nmarker\nmarker\nmarker\nmarker");
        CodeIndexStore store = Build();

        string output = SearchTextTool.SearchText(store, _fs, "marker", perFileMax: 2);

        // True total (5) surfaces in the summary even though only 2 sample lines are emitted.
        output.Should().Contain("marker: 5 matches in 1 files");
        output.Should().Contain("A.cs(5)");
    }
}
