using System.Text;

namespace CodeIndex.Internal;

/// <summary>
/// Renders find_references / search_text results grouped BY FILE: a leading file-frequency summary (breadth in one
/// call), then one "== Project/relative/path ==" header per file (filename amortized once) with up to N sample
/// lines/file, generated files DEMOTED (never dropped), and an honest truncation footer with TRUE totals.
/// </summary>
internal static class GroupedMatchOutput
{
    internal sealed record Options(string Subject, string Label, int Max, int PerFileMax, bool IncludeGenerated, int SummaryTopFiles = 12);

    /// <summary>Render one match (or a context window) WITHOUT the filename — the file header carries it.</summary>
    public static string RenderSample(string[] lines, int matchIndex, int contextLines, string? focus)
    {
        if (contextLines <= 0)
        {
            return $"{matchIndex + 1}| {Output.ClipLine(lines[matchIndex].TrimStart(), focus)}";
        }

        int from = Math.Max(0, matchIndex - contextLines);
        int to = Math.Min(lines.Length - 1, matchIndex + contextLines);
        StringBuilder sb = new();
        for (int j = from; j <= to; j++)
        {
            string marker = j == matchIndex ? ">" : " ";
            sb.Append($"{marker}{j + 1,5}| {Output.ClipLine(lines[j], focus)}");
            if (j < to)
            {
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    public static string Format(
        IReadOnlyList<ParallelScanner.FileHits> hits,
        IReadOnlyDictionary<string, string> projectDirByName,
        int skippedFiles,
        Options opt)
    {
        int totalMatches = hits.Sum(h => h.MatchCount);
        int fileCount = hits.Count;

        // Summary line — over ALL hits (including generated), most-frequent first.
        List<ParallelScanner.FileHits> byFreq = hits
            .OrderByDescending(h => h.MatchCount)
            .ThenBy(h => h.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        IEnumerable<string> top = byFreq.Take(opt.SummaryTopFiles)
            .Select(h => $"{h.FileName}({h.MatchCount}{(h.IsGenerated ? ", gen" : string.Empty)})");
        string summaryList = string.Join(", ", top);
        if (fileCount > opt.SummaryTopFiles)
        {
            summaryList += $", … +{fileCount - opt.SummaryTopFiles} more";
        }
        string summary = $"{opt.Subject}: {totalMatches} {opt.Label} in {fileCount} files — {summaryList}";

        // Body order: demote generated (unless includeGenerated) → count desc → relative path.
        List<ParallelScanner.FileHits> ordered = hits
            .OrderBy(h => !opt.IncludeGenerated && h.IsGenerated ? 1 : 0)
            .ThenByDescending(h => h.MatchCount)
            .ThenBy(h => RelPath(h, projectDirByName), StringComparer.OrdinalIgnoreCase)
            .ToList();

        StringBuilder body = new();
        int emitted = 0;
        int shownFiles = 0;
        foreach (ParallelScanner.FileHits h in ordered)
        {
            if (emitted >= opt.Max)
            {
                break;
            }
            int take = Math.Min(h.Samples.Count, opt.Max - emitted);
            if (take <= 0)
            {
                break;
            }
            body.AppendLine($"== {h.ProjectName}/{RelPath(h, projectDirByName)} ==");
            for (int i = 0; i < take; i++)
            {
                body.AppendLine(h.Samples[i]);
            }
            emitted += take;
            shownFiles++;
        }

        StringBuilder sb = new();
        sb.AppendLine(summary);
        sb.AppendLine();
        sb.Append(body);

        // Footer — only when the body is not the whole story.
        bool truncated = shownFiles < fileCount || emitted < totalMatches;
        if (truncated)
        {
            int genMatches = hits.Where(h => h.IsGenerated).Sum(h => h.MatchCount);
            int genFiles = hits.Count(h => h.IsGenerated);
            string genNote = !opt.IncludeGenerated && genMatches > 0
                ? $"{genMatches} {opt.Label} in {genFiles} generated file(s) sorted last (includeGenerated=true to promote). "
                : string.Empty;
            sb.AppendLine();
            sb.AppendLine($"Showing {emitted} sampled line(s) from {shownFiles} of {fileCount} files (≤{opt.PerFileMax}/file, {totalMatches} total). {genNote}Narrow with project= or raise max=/perFileMax=.");
        }

        if (skippedFiles > 0)
        {
            sb.AppendLine($"Note: {skippedFiles} file(s) could not be read and were skipped.");
        }

        return sb.ToString().TrimEnd();
    }

    private static string RelPath(ParallelScanner.FileHits h, IReadOnlyDictionary<string, string> projectDirByName) =>
        RelPath(h.SourceFilePath, h.ProjectName, projectDirByName);

    /// <summary>
    /// Project-relative, forward-slashed path for <paramref name="sourceFilePath"/> — the SAME logic the References
    /// section's file headers use — so type-scoped tools (get_class_hierarchy) render paths CONSISTENT with
    /// find_references / search_text. Falls back to the bare filename when the project directory is unknown.
    /// </summary>
    internal static string RelPath(string sourceFilePath, string projectName, IReadOnlyDictionary<string, string> projectDirByName) =>
        projectDirByName.TryGetValue(projectName, out string? dir)
            ? Path.GetRelativePath(dir, sourceFilePath).Replace('\\', '/')
            : Path.GetFileName(sourceFilePath);
}
