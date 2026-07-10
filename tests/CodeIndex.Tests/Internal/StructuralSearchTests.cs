using CodeIndex.Internal;
using CodeIndex.Models;
using CodeIndex.Parsing;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Internal;

/// <summary>search_structural: the curated Roslyn AST-pattern vocabulary finds each pattern, lists the vocabulary
/// on an unknown pattern, excludes generated files, and honors the project filter. Exercises the analyzer
/// directly over an in-memory file system (the store/tool-level wrappers are covered with the store wave).</summary>
public class StructuralSearchTests
{
    private const string ProjectName = "Proj";

    // One file exhibiting every curated pattern (only needs to PARSE — matchers are purely syntactic).
    private const string SmellsSource = """
        using System;
        using System.Threading.Tasks;
        namespace N;
        public class Smells
        {
            public int PublicField;
            public async void FireAndForget() { }
            public void Blocking() { var a = Task.FromResult(1).Result; Task.Delay(1).Wait(); }
            public void Swallow() { try { } catch { } }
            public void Broad() { try { } catch (Exception) { } }
            public void Fin() { try { } finally { throw new Exception(); } }
            public int Stub() { throw new NotImplementedException(); }
        }
        """;

    private static (InMemoryFileSystem Fs, List<SourceFileIndex> Files) BuildSmells()
    {
        InMemoryFileSystem fs = new();
        string path = @"C:\repo\Proj\Smells.cs";
        fs.AddFile(path, SmellsSource);
        SourceFileIndex index = new SourceFileParser(fs).Parse(path, ProjectName)!;
        return (fs, [index]);
    }

    private static SourceFileIndex Parse(InMemoryFileSystem fs, string path, string content)
    {
        fs.AddFile(path, content);
        return new SourceFileParser(fs).Parse(path, ProjectName)!;
    }

    [Theory]
    [InlineData("empty-catch")]
    [InlineData("catch-all")]
    [InlineData("async-void")]
    [InlineData("blocking-async")]
    [InlineData("public-field")]
    [InlineData("throw-in-finally")]
    [InlineData("not-implemented")]
    public void FindsEachPattern(string pattern)
    {
        (InMemoryFileSystem fs, List<SourceFileIndex> files) = BuildSmells();

        string res = StructuralSearch.Search(fs, files, pattern, project: null, max: 40, perFileCap: 5);

        res.Should().Contain("Smells.cs");
        res.Should().NotContain("No '");          // not the empty-result message
        res.Should().NotContain("Unknown structural");
    }

    [Fact]
    public void UnknownPattern_ListsVocabulary()
    {
        (InMemoryFileSystem fs, List<SourceFileIndex> files) = BuildSmells();

        string res = StructuralSearch.Search(fs, files, "bogus", project: null, max: 40, perFileCap: 5);

        res.Should().Contain("Unknown structural pattern");
        res.Should().Contain("empty-catch");
        res.Should().Contain("async-void");
    }

    [Fact]
    public void EmptyPattern_ListsVocabulary()
    {
        (InMemoryFileSystem fs, List<SourceFileIndex> files) = BuildSmells();

        string res = StructuralSearch.Search(fs, files, "   ", project: null, max: 40, perFileCap: 5);

        res.Should().Contain("Unknown structural pattern");
    }

    [Fact]
    public void ExcludesGeneratedFiles()
    {
        InMemoryFileSystem fs = new();
        List<SourceFileIndex> files =
        [
            Parse(fs, @"C:\repo\Proj\Smells.cs", SmellsSource),
            Parse(fs, @"C:\repo\Proj\Gen.g.cs",
                "namespace N;\npublic class Gen { public void M() { try { } catch { } } }\n"),
        ];

        string res = StructuralSearch.Search(fs, files, "empty-catch", project: null, max: 40, perFileCap: 5);

        res.Should().Contain("Smells.cs");
        res.Should().NotContain("Gen.g.cs");
    }

    [Fact]
    public void ProjectFilter_NoMatchMessage()
    {
        (InMemoryFileSystem fs, List<SourceFileIndex> files) = BuildSmells();

        string res = StructuralSearch.Search(fs, files, "empty-catch", project: "NoSuchProject", max: 40, perFileCap: 5);

        res.Should().Contain("No 'empty-catch' matches");
        res.Should().Contain("in project 'NoSuchProject'");
    }

    [Fact]
    public void MatchHeader_ReportsCountsAndFileGrouping()
    {
        (InMemoryFileSystem fs, List<SourceFileIndex> files) = BuildSmells();

        string res = StructuralSearch.Search(fs, files, "empty-catch", project: null, max: 40, perFileCap: 5);

        // Both `catch { }` and `catch (Exception) { }` have empty bodies, so empty-catch matches both.
        res.Should().Contain("empty-catch: 2 matches in 1 files");
        res.Should().Contain("== Smells.cs (Proj) — 2 ==");
    }

    [Fact]
    public void CatchAll_MatchesBothEmptyAndTypedExceptionCatches()
    {
        (InMemoryFileSystem fs, List<SourceFileIndex> files) = BuildSmells();

        // Smells.cs has `catch { }` (empty, no declaration) and `catch (Exception)` (typed) — both are catch-all.
        string res = StructuralSearch.Search(fs, files, "catch-all", project: null, max: 40, perFileCap: 5);

        res.Should().Contain("catch-all: 2 matches in 1 files");
    }

    [Fact]
    public void MaxCap_TruncatesAndReportsRemainder()
    {
        InMemoryFileSystem fs = new();
        List<SourceFileIndex> files =
        [
            Parse(fs, @"C:\repo\Proj\Multi.cs", """
                namespace N;
                public class Multi
                {
                    public void A() { try { } catch { } }
                    public void B() { try { } catch { } }
                    public void C() { try { } catch { } }
                }
                """),
        ];

        string res = StructuralSearch.Search(fs, files, "empty-catch", project: null, max: 2, perFileCap: 5);

        res.Should().Contain("… showing 2 of 3 matches");
    }

    [Fact]
    public void PerFileCap_LimitsSamplesPerFile()
    {
        InMemoryFileSystem fs = new();
        List<SourceFileIndex> files =
        [
            Parse(fs, @"C:\repo\Proj\Multi.cs", """
                namespace N;
                public class Multi
                {
                    public void A() { try { } catch { } }
                    public void B() { try { } catch { } }
                    public void C() { try { } catch { } }
                }
                """),
        ];

        string res = StructuralSearch.Search(fs, files, "empty-catch", project: null, max: 40, perFileCap: 2);

        res.Should().Contain("empty-catch: 3 matches in 1 files");
        res.Should().Contain("… showing 2 of 3 matches");
    }

    [Fact]
    public void PublicField_ExcludesReadonlyAndConst()
    {
        InMemoryFileSystem fs = new();
        List<SourceFileIndex> files =
        [
            Parse(fs, @"C:\repo\Proj\Fields.cs", """
                namespace N;
                public class Fields
                {
                    public int Mutable;
                    public readonly int Ro = 1;
                    public const int Cn = 2;
                }
                """),
        ];

        string res = StructuralSearch.Search(fs, files, "public-field", project: null, max: 40, perFileCap: 5);

        res.Should().Contain("public-field: 1 matches in 1 files");
    }

    [Fact]
    public void HasPattern_KnownAndUnknown()
    {
        StructuralSearch.HasPattern("empty-catch").Should().BeTrue();
        StructuralSearch.HasPattern("  async-void  ").Should().BeTrue();  // trims
        StructuralSearch.HasPattern("EMPTY-CATCH").Should().BeTrue();     // case-insensitive
        StructuralSearch.HasPattern("bogus").Should().BeFalse();
        StructuralSearch.HasPattern("   ").Should().BeFalse();
        StructuralSearch.HasPattern("").Should().BeFalse();
    }

    [Fact]
    public void PatternHelp_ListsEveryPattern()
    {
        string help = StructuralSearch.PatternHelp();

        help.Should().Contain("empty-catch");
        help.Should().Contain("catch-all");
        help.Should().Contain("async-void");
        help.Should().Contain("blocking-async");
        help.Should().Contain("public-field");
        help.Should().Contain("throw-in-finally");
        help.Should().Contain("not-implemented");
    }
}
