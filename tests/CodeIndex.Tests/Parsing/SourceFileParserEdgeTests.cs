using CodeIndex.Models;
using CodeIndex.Parsing;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Parsing;

/// <summary>Edge/branch coverage for <see cref="SourceFileParser"/> beyond the ported behavior oracle.</summary>
public sealed class SourceFileParserEdgeTests
{
    private static SourceFileIndex Parse(string code)
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(@"C:\repo\F.cs", code);
        return new SourceFileParser(fs).Parse(@"C:\repo\F.cs", "P")!;
    }

    [Fact]
    public void Parse_MissingFile_ReturnsNull()
    {
        InMemoryFileSystem fs = new();
        new SourceFileParser(fs).Parse(@"C:\repo\nope.cs", "P").Should().BeNull();
    }

    [Fact]
    public void Parse_UsingStatic_IsSkipped()
    {
        SourceFileIndex f = Parse("using static System.Math;\nusing System;\nclass C { }");

        f.Usings.Should().Contain("System");
        f.Usings.Should().NotContain("System.Math");
    }

    [Fact]
    public void Parse_UsingAlias_CapturedAsAlias_NotAsPlainUsing()
    {
        SourceFileIndex f = Parse("using Db = App.Data.Database;\nclass C { }");

        f.UsingAliases.Should().ContainKey("Db").WhoseValue.Should().Be("App.Data.Database");
        f.Usings.Should().NotContain("App.Data.Database");
    }

    [Fact]
    public void Parse_DuplicatePlainUsing_KeptOnce()
    {
        SourceFileIndex f = Parse("using System;\nusing System;\nclass C { }");

        f.Usings.Count(u => u == "System").Should().Be(1);
    }

    [Fact]
    public void Parse_MethodOverloads_AllCaptured()
    {
        SourceFileIndex f = Parse("class C { void M() { } void M(int a) { } void M(string s) { } }");

        f.Types.Single().Members.Where(m => m.Name == "M").Should().HaveCount(3);
    }

    [Theory]
    [InlineData("static class C { }", SymbolKind.StaticClass)]
    [InlineData("abstract class C { }", SymbolKind.AbstractClass)]
    [InlineData("sealed class C { }", SymbolKind.SealedClass)]
    [InlineData("class C { }", SymbolKind.Class)]
    [InlineData("struct C { }", SymbolKind.Struct)]
    [InlineData("interface C { }", SymbolKind.Interface)]
    [InlineData("record C(int X);", SymbolKind.Record)]
    [InlineData("record struct C(int X);", SymbolKind.RecordStruct)]
    public void Parse_ClassifiesTypeKind(string code, SymbolKind expected)
    {
        Parse(code).Types.Single(t => t.Name == "C").Kind.Should().Be(expected);
    }

    [Fact]
    public void Parse_TypeKeyword_IncludesModifiers()
    {
        Parse("static class C { }").Types.Single().TypeKeyword.Should().Be("static class");
        Parse("abstract class D { }").Types.Single().TypeKeyword.Should().Be("abstract class");
        Parse("sealed class E { }").Types.Single().TypeKeyword.Should().Be("sealed class");
    }

    [Fact]
    public void Parse_RecordStruct_KeywordReadsNaturally()
    {
        Parse("record struct C(int X);").Types.Single().TypeKeyword.Should().Be("record struct");
    }
}
