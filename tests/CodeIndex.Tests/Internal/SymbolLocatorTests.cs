using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Models;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Internal;

/// <summary>
/// <see cref="SymbolLocator.EnclosingSymbol"/> maps a line to the innermost Type.Member (or Type, or null)
/// containing it — the structural tag search_text puts on each hit.
/// </summary>
public sealed class SymbolLocatorTests
{
    private static SourceFileIndex BuildFile()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(@"C:\repo\App.slnx", "<Solution>\n  <Project Path=\"P/P.csproj\" />\n</Solution>\n");
        fs.AddFile(@"C:\repo\P\P.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        // 1 namespace / 2 class Foo / 3 { / 4 public void Bar() / 5 { / 6 int x = 1; / 7 } / 8 }
        fs.AddFile(@"C:\repo\P\A.cs", "namespace N;\npublic class Foo\n{\n    public void Bar()\n    {\n        int x = 1;\n    }\n}");
        CodeIndexStore store = new(fs, new IndexCache(fs), new TsIndexCache(fs), CodeIndexConfig.Default);
        store.Build(@"C:\repo");
        return store.GetFileOutline("A.cs")!;
    }

    [Fact]
    public void EnclosingSymbol_ResolvesMember()
    {
        SymbolLocator.EnclosingSymbol(BuildFile(), 6).Should().Be("Foo.Bar");   // body line of Bar
    }

    [Fact]
    public void EnclosingSymbol_ResolvesTypeWhenOutsideAnyMember()
    {
        SymbolLocator.EnclosingSymbol(BuildFile(), 2).Should().Be("Foo");       // class-declaration line
    }

    [Fact]
    public void EnclosingSymbol_ReturnsNullOutsideAnyType()
    {
        SymbolLocator.EnclosingSymbol(BuildFile(), 1).Should().BeNull();        // namespace line
    }
}
