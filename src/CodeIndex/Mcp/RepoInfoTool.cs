using System.ComponentModel;
using System.Text;
using CodeIndex.Abstractions;
using CodeIndex.Models;
using ModelContextProtocol.Server;

namespace CodeIndex.Mcp;

/// <summary>
/// Repo overview + index diagnostics in one tool (merges the former index_stats / list_projects / list_files —
/// three near-identical metadata schemas that each rode every turn). No argument: the indexed projects with file
/// counts plus build/cache health. project=: that project's source files.
/// </summary>
[McpServerToolType]
public static class RepoInfoTool
{
    [McpServerTool(Name = "repo_info")]
    [Description("Repo overview + index health. No argument: indexed projects with file counts, how/when the index was built (full/delta/cache), cache path + schema version, repo root. Pass project= to list that project's source files instead. Answers 'what's here?' and 'is my index fresh?'")]
    public static string RepoInfo(
        ICodeIndexStore index,
        ICodeIndexCache cache,
        [Description("Optional project; lists its source files instead of the overview")] string? project = null)
    {
        if (project is not null)
        {
            ProjectIndex? proj = index.GetProject(project);
            if (proj is null)
            {
                return $"Project '{project}' not found.";
            }

            return proj.SourceFiles.Count == 0
                ? $"Project '{project}' has no source files."
                : string.Join("\n", proj.SourceFiles);
        }

        BuildInfo b = index.LastBuild;
        StringBuilder sb = new();
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

        List<ProjectIndex> projects = index.ListProjects();
        sb.AppendLine();
        if (projects.Count == 0)
        {
            sb.AppendLine("No projects indexed.");
        }
        else
        {
            sb.AppendLine("Projects:");
            foreach (ProjectIndex p in projects)
            {
                sb.AppendLine($"  {p.Name} ({p.SourceFiles.Count} files)");
            }
        }

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
