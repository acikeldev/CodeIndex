using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Mcp;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Mcp;

/// <summary>
/// Tool-output tests for get_context_bundle: multi-symbol resolution, header/source formatting,
/// per-file deduplication, namespace omission, not-found handling, and the missing-source-file branch.
/// </summary>
public sealed class GetContextBundleToolTests
{
    private const string Root = @"C:\repo";
    private const string SourcePath = @"C:\repo\P\A.cs";

    private readonly InMemoryFileSystem _fs = new();

    public GetContextBundleToolTests()
    {
        _fs.AddFile(@"C:\repo\App.slnx",
            "<Solution>\n  <Project Path=\"P/P.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\P\P.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n");
        _fs.AddFile(SourcePath,
            "namespace N;\npublic class Alpha\n{\n    public void MyMethod() { }\n}\n");
        _fs.AddFile(@"C:\repo\P\B.cs",
            "public class Beta\n{\n}\n");
    }

    private CodeIndexStore Build()
    {
        CodeIndexStore store = new(_fs, new IndexCache(_fs), new TsIndexCache(_fs), CodeIndexConfig.Default);
        store.Build(Root);
        return store;
    }

    [Fact]
    public void EmptyInput_ReturnsNoSymbolNamesMessage()
    {
        CodeIndexStore store = Build();

        string output = GetContextBundleTool.GetContextBundle(store, _fs, " , , ");

        output.Should().Be("No symbol names provided.");
    }

    [Fact]
    public void FoundSymbol_EmitsHeaderNamespaceAndNumberedSource()
    {
        CodeIndexStore store = Build();

        string output = GetContextBundleTool.GetContextBundle(store, _fs, "Alpha");

        output.Should().Contain("# ");
        output.Should().Contain("Namespace: N");
        output.Should().Contain("public class Alpha");
        // Numbered gutter (right-aligned width 5 + "| ").
        output.Should().MatchRegex(@"\s+\d+\| ");
    }

    [Fact]
    public void SymbolWithoutNamespace_OmitsNamespaceLine()
    {
        CodeIndexStore store = Build();

        string output = GetContextBundleTool.GetContextBundle(store, _fs, "Beta");

        output.Should().Contain("public class Beta");
        output.Should().NotContain("Namespace:");
    }

    [Fact]
    public void RepeatedSymbol_DeduplicatesSource()
    {
        CodeIndexStore store = Build();

        string output = GetContextBundleTool.GetContextBundle(store, _fs, "Alpha,Alpha");

        output.Should().Contain("(source already included above)");
    }

    [Fact]
    public void UnknownSymbolAmongFound_ReportsNotFoundButStillReturnsBundle()
    {
        CodeIndexStore store = Build();

        string output = GetContextBundleTool.GetContextBundle(store, _fs, "Alpha,DoesNotExist");

        output.Should().Contain("# DoesNotExist — not found");
        output.Should().Contain("public class Alpha");
    }

    [Fact]
    public void AllUnknown_ReturnsNoSymbolsFoundMessage()
    {
        CodeIndexStore store = Build();

        string output = GetContextBundleTool.GetContextBundle(store, _fs, "Zzz");

        output.Should().Be("No symbols found for any of: Zzz");
    }

    [Fact]
    public void MissingSourceFile_ReportsSourceFileNotFound()
    {
        CodeIndexStore store = Build();
        // The symbol stays in the in-memory index, but its backing file is gone at read time.
        _fs.DeleteFile(SourcePath);

        string output = GetContextBundleTool.GetContextBundle(store, _fs, "Alpha");

        output.Should().Contain($"Source file not found: {SourcePath}");
    }
}
