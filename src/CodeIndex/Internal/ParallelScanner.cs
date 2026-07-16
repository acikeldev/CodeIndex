using System.Collections.Concurrent;
using CodeIndex.Abstractions;
using CodeIndex.Models;

namespace CodeIndex.Internal;

/// <summary>
/// Parallel full-scan primitive shared by find_references and search_text. Reads each candidate file ONCE, counts
/// ALL matching lines (no early-break, so totals are true and cross-file breadth is real), and captures up to
/// <c>perFileCap</c> rendered sample lines per file. Deterministic output ordering is the caller's concern
/// (the grouped-output renderer sorts explicitly), so no ordered-parallel is needed. The FileReader is built from
/// the injected file system and the caller's matcher/renderer must be thread-safe (they are — cached regexes and
/// the line clipper are).
/// </summary>
internal sealed class ParallelScanner
{
    internal sealed record FileHits(
        string ProjectName,
        string SourceFilePath,
        string FileName,
        int MatchCount,
        bool IsGenerated,
        IReadOnlyList<string> Samples,
        int NonCodeMatchCount = 0);

    internal sealed record ScanOutcome(IReadOnlyList<FileHits> Files, int SkippedFiles);

    private readonly FileReader _fileReader;

    public ParallelScanner(IFileSystem fileSystem)
    {
        _fileReader = new FileReader(fileSystem);
    }

    public ScanOutcome Scan(
        IReadOnlyList<SourceFileIndex> files,
        Func<string, bool> lineMatcher,
        Func<SourceFileIndex, string[], int, string> renderSample,
        int perFileCap,
        Func<string, bool>? nonCodeLineClassifier = null)
    {
        int skipped = 0;
        ConcurrentBag<FileHits> bag = new();

        Parallel.ForEach(files, file =>
        {
            FileReader.ReadResult read = _fileReader.ReadFileLines(file.SourceFilePath);
            if (!read.Success)
            {
                Interlocked.Increment(ref skipped);
                return;
            }

            string[] lines = read.Lines!;
            int count = 0;
            int nonCode = 0;
            List<string>? samples = null;
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lineMatcher(lines[i]))
                {
                    continue;
                }

                count++;
                // Tallied over ALL matched lines (not just the sampled ones) so the facet stays accurate under
                // per-file/max truncation of the body.
                if (nonCodeLineClassifier is not null && nonCodeLineClassifier(lines[i]))
                {
                    nonCode++;
                }

                if (perFileCap <= 0 || (samples?.Count ?? 0) < perFileCap)
                {
                    (samples ??= []).Add(renderSample(file, lines, i));
                }
            }

            if (count > 0)
            {
                bag.Add(new FileHits(file.ProjectName, file.SourceFilePath, file.FileName, count,
                    GeneratedFileClassifier.IsGenerated(file.SourceFilePath),
                    samples ?? (IReadOnlyList<string>)[], nonCode));
            }
        });

        return new ScanOutcome([.. bag], skipped);
    }
}
