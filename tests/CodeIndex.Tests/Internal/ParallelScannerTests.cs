using CodeIndex.Internal;
using CodeIndex.Models;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Internal;

public sealed class ParallelScannerTests
{
    private static SourceFileIndex FileIndex(string path, string projectName = "MyProject") =>
        new()
        {
            FileName = Path.GetFileName(path),
            SourceFilePath = path,
            ProjectName = projectName,
        };

    private static string RenderSample(SourceFileIndex file, string[] lines, int index) => $"{index}:{lines[index]}";

    [Fact]
    public void Scan_CountsAllMatchingLines_WithoutEarlyBreak()
    {
        InMemoryFileSystem fs = new();
        const string path = @"C:\repo\A.cs";
        fs.AddFile(path, "hit\nmiss\nhit\nmiss\nhit");
        ParallelScanner scanner = new(fs);

        ParallelScanner.ScanOutcome outcome = scanner.Scan(
            new[] { FileIndex(path) },
            line => line == "hit",
            RenderSample,
            perFileCap: 100);

        outcome.SkippedFiles.Should().Be(0);
        outcome.Files.Should().HaveCount(1);
        ParallelScanner.FileHits hits = outcome.Files[0];
        hits.MatchCount.Should().Be(3);
        hits.Samples.Should().Equal("0:hit", "2:hit", "4:hit");
        hits.ProjectName.Should().Be("MyProject");
        hits.FileName.Should().Be("A.cs");
        hits.SourceFilePath.Should().Be(path);
        hits.IsGenerated.Should().BeFalse();
    }

    [Fact]
    public void Scan_CountsFullMatchesButCapsSamples_WhenPerFileCapPositive()
    {
        InMemoryFileSystem fs = new();
        const string path = @"C:\repo\B.cs";
        fs.AddFile(path, "hit\nhit\nhit\nhit\nhit");
        ParallelScanner scanner = new(fs);

        ParallelScanner.ScanOutcome outcome = scanner.Scan(
            new[] { FileIndex(path) },
            line => line == "hit",
            RenderSample,
            perFileCap: 2);

        ParallelScanner.FileHits hits = outcome.Files[0];
        hits.MatchCount.Should().Be(5);
        hits.Samples.Should().Equal("0:hit", "1:hit");
    }

    [Fact]
    public void Scan_CapturesAllSamples_WhenPerFileCapIsZeroOrNegative()
    {
        InMemoryFileSystem fs = new();
        const string path = @"C:\repo\C.cs";
        fs.AddFile(path, "hit\nhit\nhit");
        ParallelScanner scanner = new(fs);

        ParallelScanner.ScanOutcome zeroCap = scanner.Scan(
            new[] { FileIndex(path) }, line => line == "hit", RenderSample, perFileCap: 0);
        ParallelScanner.ScanOutcome negativeCap = scanner.Scan(
            new[] { FileIndex(path) }, line => line == "hit", RenderSample, perFileCap: -1);

        zeroCap.Files[0].Samples.Should().Equal("0:hit", "1:hit", "2:hit");
        negativeCap.Files[0].Samples.Should().Equal("0:hit", "1:hit", "2:hit");
    }

    [Fact]
    public void Scan_OmitsFilesWithNoMatches()
    {
        InMemoryFileSystem fs = new();
        const string path = @"C:\repo\D.cs";
        fs.AddFile(path, "nothing\nhere\nmatches");
        ParallelScanner scanner = new(fs);

        ParallelScanner.ScanOutcome outcome = scanner.Scan(
            new[] { FileIndex(path) },
            line => line == "hit",
            RenderSample,
            perFileCap: 10);

        outcome.Files.Should().BeEmpty();
        outcome.SkippedFiles.Should().Be(0);
    }

    [Fact]
    public void Scan_IncrementsSkipped_ForMissingFiles()
    {
        InMemoryFileSystem fs = new();
        const string present = @"C:\repo\Present.cs";
        fs.AddFile(present, "hit");
        ParallelScanner scanner = new(fs);

        ParallelScanner.ScanOutcome outcome = scanner.Scan(
            new[] { FileIndex(present), FileIndex(@"C:\repo\Missing.cs") },
            line => line == "hit",
            RenderSample,
            perFileCap: 10);

        outcome.SkippedFiles.Should().Be(1);
        outcome.Files.Should().HaveCount(1);
        outcome.Files[0].SourceFilePath.Should().Be(present);
    }

    [Fact]
    public void Scan_FlagsGeneratedFiles()
    {
        InMemoryFileSystem fs = new();
        const string path = @"C:\repo\Model.designer.cs";
        fs.AddFile(path, "hit");
        ParallelScanner scanner = new(fs);

        ParallelScanner.ScanOutcome outcome = scanner.Scan(
            new[] { FileIndex(path) },
            line => line == "hit",
            RenderSample,
            perFileCap: 10);

        outcome.Files[0].IsGenerated.Should().BeTrue();
    }

    [Fact]
    public void Scan_ReturnsEmptyOutcome_ForEmptyInput()
    {
        InMemoryFileSystem fs = new();
        ParallelScanner scanner = new(fs);

        ParallelScanner.ScanOutcome outcome = scanner.Scan(
            Array.Empty<SourceFileIndex>(),
            line => true,
            RenderSample,
            perFileCap: 10);

        outcome.Files.Should().BeEmpty();
        outcome.SkippedFiles.Should().Be(0);
    }

    [Fact]
    public void Scan_AggregatesAcrossManyFiles_WithCorrectCountsAndSkips()
    {
        InMemoryFileSystem fs = new();
        List<SourceFileIndex> inputs = new();
        for (int i = 0; i < 50; i++)
        {
            string path = $@"C:\repo\File{i}.cs";
            fs.AddFile(path, "hit\nmiss\nhit");
            inputs.Add(FileIndex(path));
        }

        // Add references to 10 files that were never created on the in-memory disk.
        for (int i = 0; i < 10; i++)
        {
            inputs.Add(FileIndex($@"C:\repo\Ghost{i}.cs"));
        }

        ParallelScanner scanner = new(fs);
        ParallelScanner.ScanOutcome outcome = scanner.Scan(
            inputs,
            line => line == "hit",
            RenderSample,
            perFileCap: 100);

        outcome.SkippedFiles.Should().Be(10);
        outcome.Files.Should().HaveCount(50);
        outcome.Files.Should().OnlyContain(f => f.MatchCount == 2);
    }
}
