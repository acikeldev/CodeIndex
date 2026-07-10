using CodeIndex.Models;
using CodeIndex.Parsing;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Parsing;

public class SourceFileParserTests
{
    [Fact]
    public void Parse_CapturesSingleConstructor_WithCorrectSignature()
    {
        string source = """
            namespace N;
            public class C
            {
                public C(int x, string y) { }
            }
            """;

        SourceFileIndex? result = ParseSnippet(source);

        MemberInfo ctor = result!.Types[0].Members.Should().ContainSingle(m => m.Kind == SymbolKind.Constructor).Which;
        ctor.Name.Should().Be("C");
        ctor.Signature.Should().Be("C(int, string)");
    }

    [Fact]
    public void Parse_CapturesMultipleConstructors_OrderedByParamSignature()
    {
        string source = """
            namespace N;
            public class C
            {
                public C(string s) { }
                public C() { }
                public C(int i) { }
            }
            """;

        SourceFileIndex? result = ParseSnippet(source);

        List<MemberInfo> ctors = result!.Types[0].Members
            .Where(m => m.Kind == SymbolKind.Constructor)
            .ToList();

        ctors.Should().HaveCount(3);
        // Empty-param ctor sorts first (empty string), then 'int', then 'string'
        ctors[0].Signature.Should().Be("C()");
        ctors[1].Signature.Should().Be("C(int)");
        ctors[2].Signature.Should().Be("C(string)");
    }

    [Fact]
    public void Parse_CapturesStaticConstructor()
    {
        string source = """
            namespace N;
            public class C
            {
                static C() { }
                public C() { }
            }
            """;

        SourceFileIndex? result = ParseSnippet(source);

        List<MemberInfo> ctors = result!.Types[0].Members
            .Where(m => m.Kind == SymbolKind.Constructor)
            .ToList();
        ctors.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_CapturesPrimaryConstructor()
    {
        string source = """
            namespace N;
            public class C(int x, string y)
            {
                public int X => x;
            }
            """;

        SourceFileIndex? result = ParseSnippet(source);

        MemberInfo ctor = result!.Types[0].Members.Should().ContainSingle(m => m.Kind == SymbolKind.Constructor).Which;
        ctor.Name.Should().Be("C");
        ctor.Signature.Should().Be("C(int, string)");
    }

    [Fact]
    public void Parse_CapturesRecordPrimaryConstructor()
    {
        string source = """
            namespace N;
            public record Person(string Name, int Age);
            """;

        SourceFileIndex? result = ParseSnippet(source);

        MemberInfo ctor = result!.Types[0].Members.Should().ContainSingle(m => m.Kind == SymbolKind.Constructor).Which;
        ctor.Name.Should().Be("Person");
        ctor.Signature.Should().Be("Person(string, int)");
    }

    [Fact]
    public void Parse_CapturesEventField()
    {
        string source = """
            using System;
            namespace N;
            public class C
            {
                public event EventHandler Changed;
            }
            """;

        SourceFileIndex? result = ParseSnippet(source);

        MemberInfo ev = result!.Types[0].Members.Should().ContainSingle(m => m.Kind == SymbolKind.Event).Which;
        ev.Name.Should().Be("Changed");
        ev.Signature.Should().Be("event EventHandler Changed");
    }

    [Fact]
    public void Parse_CapturesEventProperty()
    {
        string source = """
            using System;
            namespace N;
            public class C
            {
                public event EventHandler Changed
                {
                    add { }
                    remove { }
                }
            }
            """;

        SourceFileIndex? result = ParseSnippet(source);

        MemberInfo ev = result!.Types[0].Members.Should().ContainSingle(m => m.Kind == SymbolKind.Event).Which;
        ev.Name.Should().Be("Changed");
        ev.Signature.Should().Contain("event");
    }

    [Fact]
    public void Parse_StillCapturesMethodsPropertiesFields()
    {
        string source = """
            namespace N;
            public class C
            {
                public int Field;
                public int Property { get; set; }
                public void Method() { }
            }
            """;

        SourceFileIndex? result = ParseSnippet(source);

        TypeInfo t = result!.Types[0];
        t.Members.Should().Contain(m => m.Kind == SymbolKind.Field && m.Name == "Field");
        t.Members.Should().Contain(m => m.Kind == SymbolKind.Property && m.Name == "Property");
        t.Members.Should().Contain(m => m.Kind == SymbolKind.Method && m.Name == "Method");
    }

    [Fact]
    public void Parse_ConstructorLineNumbersAreCorrect()
    {
        string source =
            "namespace N;\n" +
            "public class C\n" +
            "{\n" +
            "    public C() { }\n" +
            "}\n";

        SourceFileIndex? result = ParseSnippet(source);

        MemberInfo ctor = result!.Types[0].Members.Should().ContainSingle(m => m.Kind == SymbolKind.Constructor).Which;
        ctor.StartLine.Should().Be(4);
    }

    private static SourceFileIndex? ParseSnippet(string source)
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(@"C:\repo\Snippet.cs", source);
        return new SourceFileParser(fs).Parse(@"C:\repo\Snippet.cs", "TestProject");
    }
}
