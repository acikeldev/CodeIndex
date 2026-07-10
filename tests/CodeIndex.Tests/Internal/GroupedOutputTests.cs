using System.Collections.Generic;
using System.IO;
using CodeIndex.Internal;

namespace CodeIndex.Tests.Internal;

/// <summary>
/// File-grouped scan output for find_references + search_text: frequency summary, total cap, generated demotion,
/// true totals, truncation footer, skipped-file note, and single-sample rendering.
/// </summary>
public class GroupedOutputTests
{
    private static readonly string ProjDir = Path.Combine("repo", "Proj");

    private static ParallelScanner.FileHits Hit(string name, int count, bool generated, params string[] samples) =>
        new("Proj", Path.Combine(ProjDir, name), name, count, generated, samples);

    private static IReadOnlyDictionary<string, string> DirMap() =>
        new Dictionary<string, string> { ["Proj"] = ProjDir };

    private static GroupedMatchOutput.Options Opt(
        int max = 100,
        int perFileMax = 5,
        bool includeGenerated = false,
        int summaryTopFiles = 12,
        string subject = "Widget",
        string label = "refs") =>
        new(subject, label, max, perFileMax, includeGenerated, summaryTopFiles);

    // --- Format: summary + grouping + true totals -------------------------------------------------

    [Fact]
    public void Format_GroupsByFile_WithFrequencySummary_AndTrueTotals()
    {
        List<ParallelScanner.FileHits> hits =
        [
            Hit("A.cs", 2, false, "1| Widget();", "2| Widget();"),
            Hit("B.cs", 1, false, "1| Widget();"),
        ];

        string res = GroupedMatchOutput.Format(hits, DirMap(), 0, Opt());

        // True total (3 refs across 2 files), most-frequent first, no lying "N+".
        res.Should().StartWith("Widget: 3 refs in 2 files — A.cs(2), B.cs(1)");
        res.Should().NotContain("+ matches");
        // One header per file.
        res.Should().Contain("== Proj/A.cs ==");
        res.Should().Contain("== Proj/B.cs ==");
        // Fully shown → no truncation footer.
        res.Should().NotContain("Showing");
    }

    [Fact]
    public void Format_GeneratedFile_TaggedInSummary()
    {
        List<ParallelScanner.FileHits> hits =
        [
            Hit("Hand.cs", 1, false, "1| Token();"),
            Hit("Gen.g.cs", 1, true, "1| Token();"),
        ];

        string res = GroupedMatchOutput.Format(hits, DirMap(), 0, Opt(subject: "Token"));

        res.Should().Contain("Gen.g.cs(1, gen)");
        // Hand-written file header precedes the generated file header in the body.
        int handPos = res.IndexOf("== Proj/Hand.cs ==", StringComparison.Ordinal);
        int genPos = res.IndexOf("== Proj/Gen.g.cs ==", StringComparison.Ordinal);
        handPos.Should().BeGreaterThanOrEqualTo(0);
        genPos.Should().BeGreaterThanOrEqualTo(0);
        handPos.Should().BeLessThan(genPos);
    }

    [Fact]
    public void Format_IncludeGenerated_DoesNotDemote()
    {
        List<ParallelScanner.FileHits> hits =
        [
            Hit("Hand.cs", 1, false, "1| Token();"),
            Hit("Gen.g.cs", 3, true, "1| Token();", "2| Token();", "3| Token();"),
        ];

        string res = GroupedMatchOutput.Format(hits, DirMap(), 0, Opt(includeGenerated: true, subject: "Token"));

        // With generated promoted, higher count wins → generated first.
        int genPos = res.IndexOf("== Proj/Gen.g.cs ==", StringComparison.Ordinal);
        int handPos = res.IndexOf("== Proj/Hand.cs ==", StringComparison.Ordinal);
        genPos.Should().BeGreaterThanOrEqualTo(0);
        genPos.Should().BeLessThan(handPos);
    }

    // --- Truncation footer ------------------------------------------------------------------------

    [Fact]
    public void Format_MaxCap_TruncatesBodyButReportsTrueTotals()
    {
        List<ParallelScanner.FileHits> hits =
        [
            Hit("A.cs", 2, false, "1| Widget();", "2| Widget();"),
            Hit("B.cs", 1, false, "1| Widget();"),
        ];

        string res = GroupedMatchOutput.Format(hits, DirMap(), 0, Opt(max: 1));

        // Only the first sample of the first file is emitted.
        res.Should().Contain("== Proj/A.cs ==");
        res.Should().NotContain("== Proj/B.cs ==");
        res.Should().Contain("Showing 1 sampled line(s) from 1 of 2 files (≤5/file, 3 total).");
        res.Should().Contain("Narrow with project= or raise max=/perFileMax=.");
        // No generated files → no generated note.
        res.Should().NotContain("generated file(s) sorted last");
    }

    [Fact]
    public void Format_TruncatedWithGenerated_EmitsGeneratedNote()
    {
        List<ParallelScanner.FileHits> hits =
        [
            Hit("Hand.cs", 1, false, "1| Token();"),
            Hit("Gen.g.cs", 2, true, "1| Token();", "2| Token();"),
        ];

        string res = GroupedMatchOutput.Format(hits, DirMap(), 0, Opt(max: 1, subject: "Token"));

        res.Should().Contain("2 refs in 1 generated file(s) sorted last (includeGenerated=true to promote).");
    }

    [Fact]
    public void Format_TruncatedWithIncludeGenerated_SuppressesGeneratedNote()
    {
        List<ParallelScanner.FileHits> hits =
        [
            Hit("Gen.g.cs", 2, true, "1| Token();", "2| Token();"),
            Hit("Hand.cs", 1, false, "1| Token();"),
        ];

        string res = GroupedMatchOutput.Format(hits, DirMap(), 0, Opt(max: 1, includeGenerated: true, subject: "Token"));

        res.Should().Contain("Showing 1 sampled line(s)");
        res.Should().NotContain("generated file(s) sorted last");
    }

    // --- Summary overflow -------------------------------------------------------------------------

    [Fact]
    public void Format_ManyFiles_SummaryShowsTopThenMoreCount()
    {
        List<ParallelScanner.FileHits> hits =
        [
            Hit("A.cs", 3, false, "1| X"),
            Hit("B.cs", 2, false, "1| X"),
            Hit("C.cs", 1, false, "1| X"),
        ];

        string res = GroupedMatchOutput.Format(hits, DirMap(), 0, Opt(summaryTopFiles: 2, subject: "X", label: "hits"));

        res.Should().StartWith("X: 6 hits in 3 files — A.cs(3), B.cs(2), … +1 more");
    }

    // --- Skipped-file note ------------------------------------------------------------------------

    [Fact]
    public void Format_SkippedFiles_AppendsNote()
    {
        List<ParallelScanner.FileHits> hits = [Hit("A.cs", 1, false, "1| Widget();")];

        string res = GroupedMatchOutput.Format(hits, DirMap(), 2, Opt());

        res.Should().Contain("Note: 2 file(s) could not be read and were skipped.");
    }

    [Fact]
    public void Format_NoSkippedFiles_OmitsNote()
    {
        List<ParallelScanner.FileHits> hits = [Hit("A.cs", 1, false, "1| Widget();")];

        string res = GroupedMatchOutput.Format(hits, DirMap(), 0, Opt());

        res.Should().NotContain("could not be read");
    }

    // --- RelPath fallback -------------------------------------------------------------------------

    [Fact]
    public void Format_UnknownProjectDir_FallsBackToFileName()
    {
        List<ParallelScanner.FileHits> hits = [Hit("A.cs", 1, false, "1| Widget();")];
        // Empty map → RelPath cannot resolve → uses FileName.
        IReadOnlyDictionary<string, string> empty = new Dictionary<string, string>();

        string res = GroupedMatchOutput.Format(hits, empty, 0, Opt());

        res.Should().Contain("== Proj/A.cs ==");
    }

    [Fact]
    public void Format_ZeroTakeStopsBody()
    {
        // A file whose scanner produced no sample rows (Samples empty) forces take <= 0 → body loop stops.
        List<ParallelScanner.FileHits> hits =
        [
            Hit("Empty.cs", 4, false),
        ];

        string res = GroupedMatchOutput.Format(hits, DirMap(), 0, Opt());

        // No header emitted (take was 0), but the summary still reports the true total.
        res.Should().StartWith("Widget: 4 refs in 1 files — Empty.cs(4)");
        res.Should().NotContain("== Proj/Empty.cs ==");
        res.Should().Contain("Showing 0 sampled line(s) from 0 of 1 files");
    }

    // --- RenderSample -----------------------------------------------------------------------------

    [Fact]
    public void RenderSample_NoContext_RendersSingleTrimmedLine()
    {
        string[] lines = ["    Widget();"];

        string res = GroupedMatchOutput.RenderSample(lines, 0, 0, focus: null);

        res.Should().Be("1| Widget();");
    }

    [Fact]
    public void RenderSample_WithContext_MarksMatchAndJoinsWithNewlines()
    {
        string[] lines = ["l0", "l1", "l2"];

        string res = GroupedMatchOutput.RenderSample(lines, 1, 1, focus: null);

        res.Should().Be("     1| l0\n>    2| l1\n     3| l2");
    }

    [Fact]
    public void RenderSample_WithContext_ClampsAtBoundaries()
    {
        string[] lines = ["only"];

        string res = GroupedMatchOutput.RenderSample(lines, 0, 3, focus: null);

        // from/to both clamp to 0 → single marked line, no trailing newline.
        res.Should().Be(">    1| only");
    }
}
