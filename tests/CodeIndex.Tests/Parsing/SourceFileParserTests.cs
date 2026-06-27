using CodeIndex.Abstractions;
using CodeIndex.Models;
using CodeIndex.Parsing;

namespace CodeIndex.Tests.Parsing;

public sealed class SourceFileParserTests
{
    private readonly IFileSystem _fs = Substitute.For<IFileSystem>();
    private readonly SourceFileParser _parser;
    private static readonly DateTime FixedDate = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public SourceFileParserTests()
    {
        _parser = new SourceFileParser(_fs);
    }

    // ── ShouldParse ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("UserService.cs", true)]
    [InlineData("UserService.Designer.cs", false)]
    [InlineData("UserService.designer.cs", false)]
    [InlineData("Generated.g.cs", false)]
    [InlineData("Generated.g.i.cs", false)]
    public void ShouldParse_RespectsSkippedSuffixes(string fileName, bool expected)
    {
        SourceFileParser.ShouldParse(fileName).Should().Be(expected);
    }

    // ── Parse: file skipping ─────────────────────────────────────────────────

    [Fact]
    public void Parse_DesignerFile_ReturnsNull()
    {
        SourceFileIndex? result = _parser.Parse(@"C:\Repo\Foo.Designer.cs");

        result.Should().BeNull();
        _fs.DidNotReceive().ReadAllText(Arg.Any<string>());
    }

    [Fact]
    public void Parse_FileDoesNotExist_ReturnsNull()
    {
        _fs.FileExists(@"C:\Repo\Missing.cs").Returns(false);

        SourceFileIndex? result = _parser.Parse(@"C:\Repo\Missing.cs");

        result.Should().BeNull();
    }

    // ── Parse: metadata ──────────────────────────────────────────────────────

    [Fact]
    public void Parse_ValidFile_ReturnsCorrectFileName()
    {
        SetupFile(@"C:\Repo\UserService.cs", "namespace MyApp; public class UserService {}");

        SourceFileIndex result = Parse(@"C:\Repo\UserService.cs");

        result.FileName.Should().Be("UserService.cs");
        result.FullPath.Should().Be(@"C:\Repo\UserService.cs");
        result.IndexedAtUtc.Should().Be(FixedDate);
    }

    // ── Parse: namespaces ────────────────────────────────────────────────────

    [Fact]
    public void Parse_FileScopedNamespace_ExtractsNamespace()
    {
        SetupFile(@"C:\f.cs", "namespace MyApp.Services; public class Foo {}");

        SourceFileIndex result = Parse(@"C:\f.cs");

        result.Namespace.Should().Be("MyApp.Services");
    }

    [Fact]
    public void Parse_BlockScopedNamespace_ExtractsNamespace()
    {
        SetupFile(@"C:\f.cs", """
            namespace MyApp.Services
            {
                public class Foo {}
            }
            """);

        SourceFileIndex result = Parse(@"C:\f.cs");

        result.Namespace.Should().Be("MyApp.Services");
    }

    [Fact]
    public void Parse_NoNamespace_ReturnsEmptyString()
    {
        SetupFile(@"C:\f.cs", "public class Foo {}");

        SourceFileIndex result = Parse(@"C:\f.cs");

        result.Namespace.Should().BeEmpty();
    }

    // ── Parse: type kinds ────────────────────────────────────────────────────

    [Theory]
    [InlineData("public class Foo {}", SymbolKind.Class)]
    [InlineData("public static class Foo {}", SymbolKind.StaticClass)]
    [InlineData("public abstract class Foo {}", SymbolKind.AbstractClass)]
    [InlineData("public sealed class Foo {}", SymbolKind.SealedClass)]
    [InlineData("public interface IFoo {}", SymbolKind.Interface)]
    [InlineData("public struct Foo {}", SymbolKind.Struct)]
    [InlineData("public record Foo {}", SymbolKind.Record)]
    public void Parse_ClassVariants_ClassifiesKindCorrectly(string source, SymbolKind expectedKind)
    {
        SetupFile(@"C:\f.cs", source);

        SourceFileIndex result = Parse(@"C:\f.cs");

        result.Types.Should().ContainSingle()
            .Which.Kind.Should().Be(expectedKind);
    }

    [Fact]
    public void Parse_Enum_ReturnsEnumKind()
    {
        SetupFile(@"C:\f.cs", "public enum Color { Red, Green, Blue }");

        SourceFileIndex result = Parse(@"C:\f.cs");

        result.Types.Should().ContainSingle()
            .Which.Kind.Should().Be(SymbolKind.Enum);
    }

    // ── Parse: type names and namespaces ────────────────────────────────────

    [Fact]
    public void Parse_SingleClass_ExtractsNameAndNamespace()
    {
        SetupFile(@"C:\f.cs", "namespace App; public class UserService {}");

        SourceFileIndex result = Parse(@"C:\f.cs");

        TypeInfo type = result.Types.Should().ContainSingle().Subject;
        type.Name.Should().Be("UserService");
        type.Namespace.Should().Be("App");
    }

    [Fact]
    public void Parse_MultipleTypes_ReturnsAll()
    {
        SetupFile(@"C:\f.cs", """
            namespace App;
            public class A {}
            public interface IB {}
            public enum C { X }
            """);

        SourceFileIndex result = Parse(@"C:\f.cs");

        result.Types.Should().HaveCount(3);
        result.Types.Select(t => t.Name).Should().BeEquivalentTo("A", "IB", "C");
    }

    // ── Parse: base types ────────────────────────────────────────────────────

    [Fact]
    public void Parse_ClassWithBaseTypes_ExtractsBaseTypes()
    {
        SetupFile(@"C:\f.cs",
            "namespace App; public class Svc : BaseService, IService {}");

        SourceFileIndex result = Parse(@"C:\f.cs");

        result.Types.Should().ContainSingle()
            .Which.BaseTypes.Should().BeEquivalentTo("BaseService", "IService");
    }

    [Fact]
    public void Parse_ClassWithoutBaseTypes_HasEmptyBaseTypes()
    {
        SetupFile(@"C:\f.cs", "public class Foo {}");

        SourceFileIndex result = Parse(@"C:\f.cs");

        result.Types.Should().ContainSingle()
            .Which.BaseTypes.Should().BeEmpty();
    }

    // ── Parse: members ───────────────────────────────────────────────────────

    [Fact]
    public void Parse_Method_ExtractsMember()
    {
        SetupFile(@"C:\f.cs", """
            namespace App;
            public class Svc
            {
                public string GetName() { return "x"; }
            }
            """);

        SourceFileIndex result = Parse(@"C:\f.cs");

        MemberInfo member = result.Types.Single().Members.Should().ContainSingle().Subject;
        member.Name.Should().Be("GetName");
        member.Kind.Should().Be(SymbolKind.Method);
        member.ReturnType.Should().Be("string");
    }

    [Fact]
    public void Parse_Constructor_ExtractsMember()
    {
        SetupFile(@"C:\f.cs", """
            namespace App;
            public class Svc
            {
                public Svc() {}
            }
            """);

        SourceFileIndex result = Parse(@"C:\f.cs");

        MemberInfo member = result.Types.Single().Members.Should().ContainSingle().Subject;
        member.Name.Should().Be("Svc");
        member.Kind.Should().Be(SymbolKind.Constructor);
        member.ReturnType.Should().BeNull();
    }

    [Fact]
    public void Parse_Property_ExtractsMember()
    {
        SetupFile(@"C:\f.cs", """
            namespace App;
            public class Svc
            {
                public int Count { get; set; }
            }
            """);

        SourceFileIndex result = Parse(@"C:\f.cs");

        MemberInfo member = result.Types.Single().Members.Should().ContainSingle().Subject;
        member.Name.Should().Be("Count");
        member.Kind.Should().Be(SymbolKind.Property);
        member.ReturnType.Should().Be("int");
    }

    [Fact]
    public void Parse_Field_ExtractsMember()
    {
        SetupFile(@"C:\f.cs", """
            namespace App;
            public class Svc
            {
                private readonly int _count;
            }
            """);

        SourceFileIndex result = Parse(@"C:\f.cs");

        MemberInfo member = result.Types.Single().Members.Should().ContainSingle().Subject;
        member.Name.Should().Be("_count");
        member.Kind.Should().Be(SymbolKind.Field);
        member.ReturnType.Should().Be("int");
    }

    [Fact]
    public void Parse_EventDeclaration_ExtractsMember()
    {
        SetupFile(@"C:\f.cs", """
            namespace App;
            public class Svc
            {
                public event EventHandler? Changed { add {} remove {} }
            }
            """);

        SourceFileIndex result = Parse(@"C:\f.cs");

        MemberInfo member = result.Types.Single().Members.Should().ContainSingle().Subject;
        member.Name.Should().Be("Changed");
        member.Kind.Should().Be(SymbolKind.Event);
    }

    [Fact]
    public void Parse_EventField_ExtractsMember()
    {
        SetupFile(@"C:\f.cs", """
            namespace App;
            public class Svc
            {
                public event EventHandler? Changed;
            }
            """);

        SourceFileIndex result = Parse(@"C:\f.cs");

        MemberInfo member = result.Types.Single().Members.Should().ContainSingle().Subject;
        member.Name.Should().Be("Changed");
        member.Kind.Should().Be(SymbolKind.Event);
    }

    // ── Parse: line numbers ──────────────────────────────────────────────────

    [Fact]
    public void Parse_Type_HasCorrectStartLine()
    {
        SetupFile(@"C:\f.cs", """
            namespace App;

            public class Svc
            {
            }
            """);

        SourceFileIndex result = Parse(@"C:\f.cs");

        result.Types.Single().StartLine.Should().Be(3);
    }

    [Fact]
    public void Parse_Method_HasCorrectLineNumbers()
    {
        SetupFile(@"C:\f.cs", """
            namespace App;
            public class Svc
            {
                public void Run()
                {
                }
            }
            """);

        SourceFileIndex result = Parse(@"C:\f.cs");

        MemberInfo method = result.Types.Single().Members.Single();
        method.StartLine.Should().Be(4);
        method.EndLine.Should().BeGreaterThanOrEqualTo(5);
    }

    // ── Parse: unknown member kinds ──────────────────────────────────────────

    [Fact]
    public void Parse_Destructor_IsNotExtractedAsMember()
    {
        SetupFile(@"C:\f.cs", """
            namespace App;
            public class Svc
            {
                ~Svc() {}
            }
            """);

        SourceFileIndex result = Parse(@"C:\f.cs");

        // Destructors are DestructorDeclarationSyntax — not handled, falls to _ => null
        result.Types.Single().Members.Should().BeEmpty();
    }

    // ── Parse: empty file ────────────────────────────────────────────────────

    [Fact]
    public void Parse_EmptyFile_ReturnsIndexWithNoTypes()
    {
        SetupFile(@"C:\f.cs", string.Empty);

        SourceFileIndex result = Parse(@"C:\f.cs");

        result.Types.Should().BeEmpty();
        result.Namespace.Should().BeEmpty();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private void SetupFile(string path, string source)
    {
        _fs.FileExists(path).Returns(true);
        _fs.ReadAllText(path).Returns(source);
        _fs.GetLastWriteTimeUtc(path).Returns(FixedDate);
    }

    private SourceFileIndex Parse(string path)
    {
        SourceFileIndex? result = _parser.Parse(path);
        result.Should().NotBeNull($"Parse({path}) should succeed");
        return result!;
    }
}
