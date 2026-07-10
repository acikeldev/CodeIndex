using System.ComponentModel;
using System.Text;
using CodeIndex.Abstractions;
using CodeIndex.Models;
using ModelContextProtocol.Server;

namespace CodeIndex.Mcp;

[McpServerToolType]
public static class IndexStatsTool
{
    [McpServerTool(Name = "index_stats")]
    [Description("Health/diagnostics for the index: what's indexed, how the current index was built (full/delta/cache) and when, the cache location + schema version, and the repo root. Use to answer 'is my index fresh/complete?' or 'why is it empty/stale?'.")]
    public static string IndexStats(ICodeIndexStore index, ICodeIndexCache cache)
    {
        BuildInfo b = index.LastBuild;
        StringBuilder sb = new StringBuilder();

        sb.AppendLine("# CodeIndex status");
        sb.AppendLine($"Repo root: {index.RepoRoot ?? "(not resolved)"}");
        string projectsText = index.TsProjectCount > 0
            ? $"{index.ProjectCount + index.TsProjectCount} projects ({index.ProjectCount} C#, {index.TsProjectCount} TS/SCSS)"
            : $"{index.ProjectCount} projects";
        sb.AppendLine($"Indexed: {projectsText}, {index.SourceFileCount} files, {index.TypeCount} types, {index.MemberCount} members");

        if (b.Kind == "none")
        {
            sb.AppendLine("Last build: none yet (index not built).");
        }
        else
        {
            string ageText = b.WhenUtc == DateTime.MinValue
                ? "unknown"
                : FormatAge(DateTime.UtcNow - b.WhenUtc) + " ago";
            sb.AppendLine($"Last build: {b.Kind} — {ageText} (took {b.DurationMs}ms; {b.Reparsed} reparsed, {b.Removed} removed)");
        }

        sb.AppendLine($"Cache: {cache.GetCachePath(index.CacheDirectory)} (schema v{cache.CurrentSchemaVersion})");
        return sb.ToString();
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age.TotalSeconds < 90)
        {
            return $"{(int)age.TotalSeconds}s";
        }

        if (age.TotalMinutes < 90)
        {
            return $"{(int)age.TotalMinutes}m";
        }

        if (age.TotalHours < 48)
        {
            return $"{(int)age.TotalHours}h";
        }

        return $"{(int)age.TotalDays}d";
    }
}
