using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Mcp;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Mcp;

/// <summary>
/// Output-level tests for <see cref="GetSymbolSourceTool"/>: line-window formatting, filename resolution,
/// the path-containment refusal, missing-file handling, and the past-end-of-file guard.
/// </summary>
public sealed class GetSymbolSourceToolTests
{
    private const string Root = @"C:\repo";

    private readonly InMemoryFileSystem _fs = new();

    public GetSymbolSourceToolTests()
    {
        _fs.AddFile(@"C:\repo\App.slnx",
            "<Solution>\n  <Project Path=\"P/P.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\P\P.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n");
        _fs.AddFile(@"C:\repo\P\A.cs", "namespace N;\npublic class A\n{\n    public void M() { }\n}");
    }

    private CodeIndexStore Build()
    {
        CodeIndexStore store = new(_fs, new IndexCache(_fs), new TsIndexCache(_fs), CodeIndexConfig.Default);
        store.Build(Root);
        return store;
    }

    [Fact]
    public void ReadsRequestedWindow_WithLineNumberGutter()
    {
        CodeIndexStore store = Build();

        string result = GetSymbolSourceTool.GetSymbolSource(store, _fs, @"C:\repo\P\A.cs", 2, 2);

        result.Should().Contain("    2| public class A");
        result.Should().Contain("    3| {");
        result.Should().NotContain("namespace N;");
        result.Should().NotContain("public void M()");
    }

    [Fact]
    public void ResolvesBareFilename_ToIndexedPath()
    {
        CodeIndexStore store = Build();

        string result = GetSymbolSourceTool.GetSymbolSource(store, _fs, "A.cs", 1, 1);

        result.Should().Contain("    1| namespace N;");
    }

    [Fact]
    public void ClampsCount_ToEndOfFile()
    {
        CodeIndexStore store = Build();

        string result = GetSymbolSourceTool.GetSymbolSource(store, _fs, @"C:\repo\P\A.cs", 4, 100);

        result.Should().Contain("    4|     public void M() { }");
        result.Should().Contain("    5| }");
    }

    [Fact]
    public void RefusesPath_OutsideRepoAndNotIndexed()
    {
        CodeIndexStore store = Build();

        string result = GetSymbolSourceTool.GetSymbolSource(store, _fs, @"C:\Users\me\.aws\credentials", 1, 5);

        result.Should().StartWith("Refused:");
        result.Should().Contain("outside the repository");
    }

    [Fact]
    public void ReportsNotFound_WhenWithinRepoButMissing()
    {
        CodeIndexStore store = Build();

        string result = GetSymbolSourceTool.GetSymbolSource(store, _fs, @"C:\repo\P\Ghost.cs", 1, 5);

        result.Should().StartWith("Source file not found:");
        result.Should().Contain("Ghost.cs");
    }

    [Fact]
    public void ReportsBeyondFileLength_WhenStartPastEnd()
    {
        CodeIndexStore store = Build();

        string result = GetSymbolSourceTool.GetSymbolSource(store, _fs, @"C:\repo\P\A.cs", 100, 5);

        result.Should().Contain("Start line 100 is beyond file length (5 lines).");
    }
}
