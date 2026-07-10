using CodeIndex.Internal;
using CodeIndex.Models;
using CodeIndex.Parsing;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Internal;

/// <summary>
/// Supplemental coverage for <see cref="TsStructuralSearch"/> branches not exercised by
/// <see cref="TsStructuralSearchTests"/>: multi-file rendering (the <c>OrderByDescending</c>/<c>ThenBy</c>
/// grouping), the outer-loop <c>max</c> break that stops before every matching file is shown, and the
/// <c>IsConsoleCall</c> early-out for a call whose callee is a plain identifier rather than a member access.
/// </summary>
public class TsStructuralSearchCoverageTests
{
    private static SourceFileIndex Parse(InMemoryFileSystem fs, string path, string project) =>
        new TypeScriptParser(fs).Parse(path, project)!;

    [Fact]
    public void MultipleFiles_GroupsAndOrdersByCountThenFileName()
    {
        // Two files with EQUAL match counts force the ThenBy(FileName) tiebreak inside Render's grouping,
        // exercising the OrderByDescending(Count)/ThenBy(FileName) key selectors across >1 group.
        InMemoryFileSystem fs = new();
        string beta = @"C:\repo\web\Beta.ts";
        string alpha = @"C:\repo\web\Alpha.ts";
        fs.AddFile(beta, "export function b(): void { console.log('b'); }");
        fs.AddFile(alpha, "export function a(): void { console.log('a'); }");
        SourceFileIndex idxBeta = Parse(fs, beta, "P");
        SourceFileIndex idxAlpha = Parse(fs, alpha, "P");

        string res = TsStructuralSearch.Search(fs, [idxBeta, idxAlpha], "console-log", project: null, max: 40, perFileCap: 5);

        res.Should().Contain("console-log: 2 matches in 2 files");
        res.Should().Contain("Alpha.ts");
        res.Should().Contain("Beta.ts");
        // Tiebroken alphabetically by file name → Alpha listed before Beta.
        res.IndexOf("Alpha.ts", StringComparison.Ordinal)
            .Should().BeLessThan(res.IndexOf("Beta.ts", StringComparison.Ordinal));
    }

    [Fact]
    public void Max_BreaksOuterFileLoopBeforeShowingEveryFile()
    {
        // max=1 across two single-match files: the first file emits its one hit, then the OUTER loop's
        // `if (emitted >= max) break;` fires at the top of the second iteration (a different branch from the
        // per-file inner cap). Footer reports 1 file shown of 2.
        InMemoryFileSystem fs = new();
        string alpha = @"C:\repo\web\Alpha.ts";
        string beta = @"C:\repo\web\Beta.ts";
        fs.AddFile(alpha, "export function a(): void { console.log('a'); }");
        fs.AddFile(beta, "export function b(): void { console.log('b'); }");
        SourceFileIndex idxAlpha = Parse(fs, alpha, "P");
        SourceFileIndex idxBeta = Parse(fs, beta, "P");

        string res = TsStructuralSearch.Search(fs, [idxAlpha, idxBeta], "console-log", project: null, max: 1, perFileCap: 5);

        res.Should().Contain("console-log: 2 matches in 2 files");
        res.Should().Contain("showing 1 of 2 matches across 1/2 files");
    }

    [Fact]
    public void ConsoleLog_PlainIdentifierCall_IsNotAMatch()
    {
        // `doThing()` is a call_expression whose callee is an identifier (not a member_expression), so
        // IsConsoleCall must reject it at the `callee.Type != "member_expression"` guard → zero matches.
        InMemoryFileSystem fs = new();
        string path = @"C:\repo\web\plain.ts";
        fs.AddFile(path, "export function f(): void { doThing(); helper(1, 2); }");
        SourceFileIndex idx = Parse(fs, path, "P");

        string res = TsStructuralSearch.Search(fs, [idx], "console-log", project: null, max: 40, perFileCap: 5);

        res.Should().StartWith("No 'console-log' matches.");
    }

    [Fact]
    public void ConsoleLog_NonConsoleMemberCall_IsNotAMatch()
    {
        // `obj.method()` is a member_expression call, but the object is not `console`, so IsConsoleCall falls
        // through the object identity check → zero matches (complements the plain-identifier guard above).
        InMemoryFileSystem fs = new();
        string path = @"C:\repo\web\member.ts";
        fs.AddFile(path, "export function f(): void { logger.info('hi'); }");
        SourceFileIndex idx = Parse(fs, path, "P");

        string res = TsStructuralSearch.Search(fs, [idx], "console-log", project: null, max: 40, perFileCap: 5);

        res.Should().StartWith("No 'console-log' matches.");
    }

    [Fact]
    public void EmptyFile_ParsesWithNoMatches()
    {
        // An empty source still runs the parse path and yields no descendants that match; real behavior is the
        // no-match message (also guards the tree-handling path for degenerate input).
        InMemoryFileSystem fs = new();
        string path = @"C:\repo\web\empty.ts";
        fs.AddFile(path, string.Empty);
        SourceFileIndex idx = Parse(fs, path, "P");

        string res = TsStructuralSearch.Search(fs, [idx], "console-log", project: null, max: 40, perFileCap: 5);

        res.Should().StartWith("No 'console-log' matches.");
    }
}
