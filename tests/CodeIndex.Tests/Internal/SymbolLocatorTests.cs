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

    [Fact]
    public void EnclosingSymbol_ClosingBraceOfMember_StaysInsideMember()
    {
        // LineCount is inclusive of the closing brace, so the member's last line still resolves to the member
        // (not a fall-back to the enclosing type). Line 7 is Bar's closing '}'.
        SymbolLocator.EnclosingSymbol(BuildFile(), 7).Should().Be("Foo.Bar");
    }

    [Fact]
    public void EnclosingSymbol_ClosingBraceOfType_ResolvesToType()
    {
        // Line 8 is Foo's closing '}': inside the type's range but outside any member → the type name.
        SymbolLocator.EnclosingSymbol(BuildFile(), 8).Should().Be("Foo");
    }

    [Fact]
    public void EnclosingSymbol_DocCommentLine_ResolvesToEnclosingType()
    {
        // A member's StartLine excludes leading trivia (the /// doc comment), so a hit on the doc-comment line
        // falls before the member and resolves to the enclosing TYPE — coarser, but never a wrong symbol name.
        SymbolLocator.EnclosingSymbol(BuildFileWithDocComment(), 4).Should().Be("Calc");
    }

    private static SourceFileIndex BuildFileWithDocComment()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(@"C:\repo\App.slnx", "<Solution>\n  <Project Path=\"P/P.csproj\" />\n</Solution>\n");
        fs.AddFile(@"C:\repo\P\P.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        // 1 namespace / 2 class Calc / 3 { / 4 /// doc / 5 public int Add(int a) / 6 { / 7 return a; / 8 } / 9 }
        fs.AddFile(@"C:\repo\P\A.cs", "namespace N;\npublic class Calc\n{\n    /// <summary>needle</summary>\n    public int Add(int a)\n    {\n        return a;\n    }\n}");
        CodeIndexStore store = new(fs, new IndexCache(fs), new TsIndexCache(fs), CodeIndexConfig.Default);
        store.Build(@"C:\repo");
        return store.GetFileOutline("A.cs")!;
    }
}
