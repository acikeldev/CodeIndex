using CodeIndex.Models;
using CodeIndex.Parsing;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Parsing;

/// <summary>Wave 3 (TS/SCSS Phase 1): hand-rolled SCSS tokenizer.</summary>
public class ScssParserTests
{
    private const string ScssPath = @"C:\repo\web\styles.scss";

    private static SourceFileIndex ParseScss(string source, string path = ScssPath)
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(path, source);
        SourceFileIndex? r = new ScssParser(fs).Parse(path, "TestScssProj");
        r.Should().NotBeNull();
        return r!;
    }

    [Fact]
    public void ExtractsAllScssSymbolKinds()
    {
        SourceFileIndex r = ParseScss("""
            $primary: #333;
            %reset { margin: 0; }
            @mixin button-variant($color) { color: $color; }
            @function double($n) { @return $n * 2; }
            .toolbar { display: flex; }
            .toolbar__item { color: $primary; }
            """);

        r.Types.Should().Contain(t => t.Name == "primary" && t.Kind == SymbolKind.ScssVariable);
        r.Types.Should().Contain(t => t.Name == "reset" && t.Kind == SymbolKind.ScssPlaceholder);
        r.Types.Should().Contain(t => t.Name == "button-variant" && t.Kind == SymbolKind.ScssMixin);
        r.Types.Should().Contain(t => t.Name == "double" && t.Kind == SymbolKind.ScssFunction);
        r.Types.Should().Contain(t => t.Name == "toolbar" && t.Kind == SymbolKind.ScssSelector);
        r.Types.Should().Contain(t => t.Name == "toolbar__item" && t.Kind == SymbolKind.ScssSelector);
    }

    [Fact]
    public void IgnoresComments()
    {
        SourceFileIndex r = ParseScss("""
            // .commentedClass { }
            /* @mixin ignoredMixin() { } */
            .realClass { color: red; }
            """);

        r.Types.Should().Contain(t => t.Name == "realClass");
        r.Types.Should().NotContain(t => t.Name == "commentedClass");
        r.Types.Should().NotContain(t => t.Name == "ignoredMixin");
    }

    [Fact]
    public void DeduplicatesRepeatedSelector()
    {
        SourceFileIndex r = ParseScss("""
            .btn { color: red; }
            .btn { color: blue; }
            """);

        r.Types.Should().ContainSingle(t => t.Name == "btn" && t.Kind == SymbolKind.ScssSelector);
    }

    [Fact]
    public void UrlWithSchemeDoesNotTruncateLine()
    {
        // The `//` in a URL scheme must not be treated as a line comment (which would swallow the trailing $size).
        SourceFileIndex r = ParseScss("$logo: url(http://cdn/logo.png); $size: 40px;");
        r.Types.Should().Contain(t => t.Name == "logo" && t.Kind == SymbolKind.ScssVariable);
        r.Types.Should().Contain(t => t.Name == "size" && t.Kind == SymbolKind.ScssVariable);
    }

    [Fact]
    public void CapturesLineNumbers()
    {
        SourceFileIndex r = ParseScss("$a: 1;\n$b: 2;\n.c { }\n");
        r.Types.Should().ContainSingle(t => t.Name == "c").Which.StartLine.Should().Be(3);
    }

    [Fact]
    public void ReturnsNullWhenFileMissing()
    {
        InMemoryFileSystem fs = new();
        SourceFileIndex? r = new ScssParser(fs).Parse(@"C:\repo\web\nope.scss", "TestScssProj");
        r.Should().BeNull();
    }

    [Fact]
    public void MultiLineBlockCommentSuppressesSymbolsUntilClosed()
    {
        SourceFileIndex r = ParseScss("""
            /* opening
            $hidden: 1;
            .hiddenClass { } */
            .afterClass { color: red; }
            """);

        r.Types.Should().NotContain(t => t.Name == "hidden");
        r.Types.Should().NotContain(t => t.Name == "hiddenClass");
        r.Types.Should().Contain(t => t.Name == "afterClass" && t.Kind == SymbolKind.ScssSelector);
    }

    [Fact]
    public void PopulatesSourceFileIndexMetadata()
    {
        SourceFileIndex r = ParseScss(".x { }");
        r.FileName.Should().Be("styles.scss");
        r.SourceFilePath.Should().Be(ScssPath);
        r.ProjectName.Should().Be("TestScssProj");
        r.Namespace.Should().BeNull();
        r.Language.Should().Be(Language.Scss);
    }

    [Fact]
    public void ClassSelectorsInDeclarationBodyAreNotHarvested()
    {
        // Class harvest only scans the selector portion (before '{'); a dotted value after '{' is not a selector.
        SourceFileIndex r = ParseScss(".realSelector { background: url(icon.only-value); }");
        r.Types.Should().Contain(t => t.Name == "realSelector" && t.Kind == SymbolKind.ScssSelector);
        r.Types.Should().NotContain(t => t.Name == "only-value");
    }

    [Fact]
    public void EmptyFileYieldsNoTypes()
    {
        SourceFileIndex r = ParseScss(string.Empty);
        r.Types.Should().BeEmpty();
    }
}
