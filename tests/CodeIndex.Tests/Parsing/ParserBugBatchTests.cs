using CodeIndex.Models;
using CodeIndex.Parsing;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Parsing;

/// <summary>Covers the wrong-answer bug-batch fixes at the parser level.</summary>
public class ParserBugBatchTests
{
    [Fact]
    public void Parse_IndexesNestedTypes()
    {
        string source = """
            namespace N;
            public class Outer
            {
                public class Inner { public void M() { } }
                public enum E { A, B }
            }
            """;

        SourceFileIndex result = ParseSnippet(source)!;

        result.Types.Should().Contain(t => t.Name == "Outer" && !t.IsNested);
        TypeInfo inner = result.Types.Should().ContainSingle(t => t.Name == "Inner").Which;
        inner.IsNested.Should().BeTrue();
        inner.Members.Should().Contain(m => m.Name == "M" && m.Kind == SymbolKind.Method);
        TypeInfo e = result.Types.Should().ContainSingle(t => t.Name == "E").Which;
        e.IsNested.Should().BeTrue();
        e.Kind.Should().Be(SymbolKind.Enum);
    }

    [Fact]
    public void Parse_SplitsMultiDeclaratorFieldsIntoOneMemberEach()
    {
        string source = """
            namespace N;
            public class C { public int a, b, c; }
            """;

        SourceFileIndex result = ParseSnippet(source)!;

        List<MemberInfo> fields = result.Types[0].Members.Where(m => m.Kind == SymbolKind.Field).ToList();
        fields.Should().HaveCount(3);
        fields.Should().Contain(f => f.Name == "a");
        fields.Should().Contain(f => f.Name == "b");
        fields.Should().Contain(f => f.Name == "c");
        fields.Should().OnlyContain(f => f.ReturnType == "int");
    }

    [Theory]
    [InlineData("public struct S { }", "S", SymbolKind.Struct)]
    [InlineData("public record R(int X);", "R", SymbolKind.Record)]
    [InlineData("public record struct RS(int Y);", "RS", SymbolKind.RecordStruct)]
    [InlineData("public record class RC(int Z);", "RC", SymbolKind.Record)]
    public void Parse_ClassifiesStructsAndRecords(string decl, string name, SymbolKind expected)
    {
        SourceFileIndex result = ParseSnippet($"namespace N;\n{decl}")!;
        TypeInfo t = result.Types.Should().ContainSingle(x => x.Name == name).Which;
        t.Kind.Should().Be(expected);
    }

    [Fact]
    public void Parse_CapturesPerTypeNamespaceInMultiNamespaceFile()
    {
        string source = """
            namespace A { public class X { } }
            namespace B { public class Y { } }
            """;

        SourceFileIndex result = ParseSnippet(source)!;

        result.Types.Should().ContainSingle(t => t.Name == "X").Which.Namespace.Should().Be("A");
        result.Types.Should().ContainSingle(t => t.Name == "Y").Which.Namespace.Should().Be("B");
    }

    [Fact]
    public void Parse_CapturesNestedNamespaceOnType()
    {
        string source = """
            namespace A.B
            {
                namespace C { public class Deep { } }
            }
            """;

        SourceFileIndex result = ParseSnippet(source)!;
        result.Types.Should().ContainSingle(t => t.Name == "Deep").Which.Namespace.Should().Be("A.B.C");
    }

    [Fact]
    public void Parse_RetainsTypeLessFileWithItsUsings()
    {
        string source = """
            using System;
            using System.Text;
            // no type declarations here
            """;

        SourceFileIndex? result = ParseSnippet(source);

        result.Should().NotBeNull();
        result!.Types.Should().BeEmpty();
        result.Usings.Should().Contain("System");
        result.Usings.Should().Contain("System.Text");
    }

    [Fact]
    public void Parse_KeepsEachBaseTypeAsSeparateListEntry_GenericsIntact()
    {
        string source = """
            namespace N;
            public class C : BaseClass, IFoo, IRepository<Entity, int> { }
            """;

        TypeInfo t = ParseSnippet(source)!.Types[0];

        t.BaseTypes.Should().NotBeNull();
        t.BaseTypes!.Should().HaveCount(3);
        t.BaseTypes![0].Should().Be("BaseClass");
        t.BaseTypes![1].Should().Be("IFoo");
        t.BaseTypes![2].Should().Be("IRepository<Entity, int>");
        t.BaseTypesDisplay.Should().Be("BaseClass, IFoo, IRepository<Entity, int>");
    }

    [Fact]
    public void Parse_MultiDeclaratorEventFieldSplits()
    {
        string source = """
            using System;
            namespace N;
            public class C { public event EventHandler A, B; }
            """;

        List<MemberInfo> events = ParseSnippet(source)!.Types[0].Members.Where(m => m.Kind == SymbolKind.Event).ToList();
        events.Should().HaveCount(2);
        events.Should().Contain(e => e.Name == "A");
        events.Should().Contain(e => e.Name == "B");
    }

    private static SourceFileIndex? ParseSnippet(string source)
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(@"C:\repo\Snippet.cs", source);
        return new SourceFileParser(fs).Parse(@"C:\repo\Snippet.cs", "TestProject");
    }
}
