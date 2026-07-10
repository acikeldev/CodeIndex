using CodeIndex.Internal;
using CodeIndex.Models;
using CodeIndex.Parsing;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Internal;

/// <summary>
/// Direct tests for the TS structural-search vocabulary (tree-sitter, house rules): each pattern fires on a
/// fixture exhibiting it; project scoping, generated exclusion, non-TS skipping, unreadable-file tolerance, and
/// the truncation/no-match rendering are all exercised against the analyzer itself.
///
/// The company suite drove these through CodeIndexStore / StructuralSearchTool; those store-level tests
/// (FindsEachTsPattern, UnknownPattern_ListsBothVocabularies, EmptyCatch_ResolvesToCSharpEngine, Tool_Wraps)
/// land with the store/tools wave. Their analyzer-level truth is reproduced here directly.
/// </summary>
public class TsStructuralSearchTests
{
    // One TSX file exhibiting every TS house-rule pattern (only needs to PARSE).
    private const string BadTsx = """
        // @ts-strict-ignore
        import { X } from './x';
        export default class Comp {
            bad(v: any): void {
                console.log('x');
                const y = v as any;
                const el = <div style={{ color: 'red' }} className={`a ${v}`}>{v}</div>;
                return el;
            }
        }
        """;

    private static (InMemoryFileSystem Fs, List<SourceFileIndex> Files) BuildBad(string project = "P", string path = @"C:\repo\web\Bad.tsx")
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(path, BadTsx);
        SourceFileIndex idx = new TypeScriptParser(fs).Parse(path, project)!;
        return (fs, [idx]);
    }

    [Theory]
    [InlineData("inline-style")]
    [InlineData("classname-interp")]
    [InlineData("ts-ignore")]
    [InlineData("default-export")]
    [InlineData("console-log")]
    [InlineData("any-type")]
    public void FindsEachTsPattern(string pattern)
    {
        (InMemoryFileSystem fs, List<SourceFileIndex> files) = BuildBad();

        string res = TsStructuralSearch.Search(fs, files, pattern, project: null, max: 40, perFileCap: 5);

        res.Should().Contain("Bad.tsx");
        res.Should().NotContain("No '");
    }

    [Fact]
    public void ConsoleLog_InPlainTsFile_UsesNonTsxGrammar()
    {
        // Exercises the .ts (non-.tsx) grammar branch, separate from the JSX-bearing fixture above.
        InMemoryFileSystem fs = new();
        string path = @"C:\repo\web\log.ts";
        fs.AddFile(path, "export function f(): void { console.log('hi'); console.warn('bye'); }");
        SourceFileIndex idx = new TypeScriptParser(fs).Parse(path, "P")!;

        string res = TsStructuralSearch.Search(fs, [idx], "console-log", project: null, max: 40, perFileCap: 5);

        res.Should().Contain("log.ts");
        res.Should().Contain("console-log: 2 matches");
    }

    [Theory]
    [InlineData("inline-style")]
    [InlineData("classname-interp")]
    [InlineData("ts-ignore")]
    [InlineData("default-export")]
    [InlineData("console-log")]
    [InlineData("any-type")]
    public void HasPattern_ForKnownPattern_ReturnsTrue(string pattern)
    {
        TsStructuralSearch.HasPattern(pattern).Should().BeTrue();
        // Case-insensitive + trimmed lookup.
        TsStructuralSearch.HasPattern("  " + pattern.ToUpperInvariant() + "  ").Should().BeTrue();
    }

    [Fact]
    public void HasPattern_ForUnknownPattern_ReturnsFalse()
    {
        TsStructuralSearch.HasPattern("bogus").Should().BeFalse();
    }

    [Fact]
    public void HasPattern_EmptyCatch_NotInTsVocabulary()
    {
        // empty-catch belongs to the C# engine; the TS vocabulary must NOT own it (would silently shadow it).
        TsStructuralSearch.HasPattern("empty-catch").Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void HasPattern_ForNullOrWhitespace_ReturnsFalse(string? pattern)
    {
        TsStructuralSearch.HasPattern(pattern!).Should().BeFalse();
    }

    [Fact]
    public void PatternHelp_ListsEveryPattern()
    {
        string help = TsStructuralSearch.PatternHelp();

        help.Should().Contain("inline-style");
        help.Should().Contain("classname-interp");
        help.Should().Contain("ts-ignore");
        help.Should().Contain("default-export");
        help.Should().Contain("console-log");
        help.Should().Contain("any-type");
    }

    [Fact]
    public void NoMatch_ReturnsNoMatchMessageWithDescription()
    {
        InMemoryFileSystem fs = new();
        string path = @"C:\repo\web\Clean.tsx";
        fs.AddFile(path, "export const value: number = 1;\n");
        SourceFileIndex idx = new TypeScriptParser(fs).Parse(path, "P")!;

        string res = TsStructuralSearch.Search(fs, [idx], "any-type", project: null, max: 40, perFileCap: 5);

        res.Should().StartWith("No 'any-type' matches.");
        res.Should().Contain("defeats type safety");
    }

    [Fact]
    public void ProjectFilter_ScopesToNamedProjectOnly()
    {
        InMemoryFileSystem fs = new();
        string a = @"C:\repo\a\BadA.tsx";
        string b = @"C:\repo\b\BadB.tsx";
        fs.AddFile(a, BadTsx);
        fs.AddFile(b, BadTsx);
        SourceFileIndex idxA = new TypeScriptParser(fs).Parse(a, "P1")!;
        SourceFileIndex idxB = new TypeScriptParser(fs).Parse(b, "P2")!;

        string res = TsStructuralSearch.Search(fs, [idxA, idxB], "any-type", project: "P1", max: 40, perFileCap: 5);

        res.Should().Contain("BadA.tsx");
        res.Should().NotContain("BadB.tsx");
        res.Should().Contain("in project 'P1'");
    }

    [Fact]
    public void ProjectFilter_NoMatch_IncludesScopeInMessage()
    {
        (InMemoryFileSystem fs, List<SourceFileIndex> files) = BuildBad(project: "P1");

        string res = TsStructuralSearch.Search(fs, files, "any-type", project: "P2", max: 40, perFileCap: 5);

        res.Should().StartWith("No 'any-type' matches in project 'P2'.");
    }

    [Fact]
    public void NonTypeScriptFile_IsIgnored()
    {
        InMemoryFileSystem fs = new();
        string cs = @"C:\repo\Proj\C.cs";
        fs.AddFile(cs, "namespace N; public class C { void M() { try { } catch { } } }");
        SourceFileIndex idx = new SourceFileParser(fs).Parse(cs, "Proj")!;

        string res = TsStructuralSearch.Search(fs, [idx], "any-type", project: null, max: 40, perFileCap: 5);

        res.Should().StartWith("No 'any-type' matches.");
    }

    [Fact]
    public void UnreadableFile_IsSkippedGracefully()
    {
        // Index entry whose backing file is absent from the FS → ReadAllText throws → the file is skipped,
        // not propagated, and the scan still completes.
        (InMemoryFileSystem fs, List<SourceFileIndex> files) = BuildBad();
        fs.DeleteFile(files[0].SourceFilePath);

        string res = TsStructuralSearch.Search(fs, files, "any-type", project: null, max: 40, perFileCap: 5);

        res.Should().StartWith("No 'any-type' matches.");
    }

    [Fact]
    public void Max_TruncatesAndReportsRemainder()
    {
        // Bad.tsx has two `any` occurrences (`: any`, `as any`); capping max at 1 forces the truncation footer.
        (InMemoryFileSystem fs, List<SourceFileIndex> files) = BuildBad();

        string res = TsStructuralSearch.Search(fs, files, "any-type", project: null, max: 1, perFileCap: 5);

        res.Should().Contain("any-type: 2 matches");
        res.Should().Contain("showing 1 of 2 matches");
    }

    [Fact]
    public void PerFileCap_LimitsHitsPerFile()
    {
        // perFileCap=1 with generous max still trims the second hit in the single file and reports the remainder.
        (InMemoryFileSystem fs, List<SourceFileIndex> files) = BuildBad();

        string res = TsStructuralSearch.Search(fs, files, "any-type", project: null, max: 40, perFileCap: 1);

        res.Should().Contain("showing 1 of 2 matches");
    }
}

/// <summary>
/// Isolated in its own collection because it mutates the process-wide <see cref="GeneratedFileClassifier"/>
/// glob set; shares the collection name with the classifier's own tests so they never run concurrently.
/// </summary>
[Collection("GeneratedFileClassifier")]
public class TsStructuralSearchGeneratedExclusionTests
{
    [Fact]
    public void GeneratedFile_IsExcludedFromSearch()
    {
        InMemoryFileSystem fs = new();
        string path = @"C:\repo\web\Bad.tsx";
        fs.AddFile(path, TsStructuralSearchFixture.BadTsx);
        SourceFileIndex idx = new TypeScriptParser(fs).Parse(path, "P")!;

        try
        {
            GeneratedFileClassifier.UseGlobs(["bad.tsx"]);
            string res = TsStructuralSearch.Search(fs, [idx], "any-type", project: null, max: 40, perFileCap: 5);
            res.Should().StartWith("No 'any-type' matches.");
        }
        finally
        {
            GeneratedFileClassifier.UseGlobs(GeneratedFileClassifier.DefaultGlobs);
        }
    }
}

/// <summary>Shared fixture text so the isolated generated-exclusion class can reuse the same TSX sample.</summary>
internal static class TsStructuralSearchFixture
{
    public const string BadTsx = """
        // @ts-strict-ignore
        export default class Comp {
            bad(v: any): void {
                const y = v as any;
                return;
            }
        }
        """;
}
