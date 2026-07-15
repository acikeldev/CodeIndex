using System.ComponentModel;
using System.Text;
using CodeIndex.Abstractions;
using CodeIndex.Internal;
using CodeIndex.Models;
using ModelContextProtocol.Server;

namespace CodeIndex.Mcp;

[McpServerToolType]
public static class GetProjectDependenciesTool
{
    private const int DefaultDependentsShown = 15;

    [McpServerTool(Name = "get_project_dependencies")]
    [Description("Project-level dependencies: what a project references and what references it, from the index-time graph (exact project-name matching). The reverse-dependents list is truncated by default — pass full=true for all.")]
    public static string GetProjectDependencies(
        ICodeIndexStore index,
        [Description("Project name")] string project,
        [Description("List every dependent (default false shows first 15 with a count)")] bool full = false)
    {
        ProjectDependencyInfo? info = index.GetProjectDependencyInfo(project);
        if (info is null)
        {
            return $"Project '{project}' not found."
                + NameSuggester.DidYouMean(project, index.ProjectNames())
                + " Try repo_info for the exact project names.";
        }

        StringBuilder sb = new();
        sb.AppendLine($"# {info.Name}");

        if (info.References.Count > 0)
        {
            sb.AppendLine($"References ({info.References.Count}):");
            foreach (string r in info.References)
            {
                sb.AppendLine($"  {r}");
            }
        }
        else
        {
            sb.AppendLine("References: none");
        }

        sb.AppendLine();

        if (info.Dependents.Count == 0)
        {
            sb.AppendLine("Referenced by: none");
        }
        else if (full || info.Dependents.Count <= DefaultDependentsShown)
        {
            sb.AppendLine($"Referenced by ({info.Dependents.Count}):");
            foreach (string d in info.Dependents)
            {
                sb.AppendLine($"  {d}");
            }
        }
        else
        {
            sb.AppendLine($"Referenced by ({info.Dependents.Count}, showing {DefaultDependentsShown}):");
            foreach (string d in info.Dependents.Take(DefaultDependentsShown))
            {
                sb.AppendLine($"  {d}");
            }
            sb.AppendLine($"  … {info.Dependents.Count - DefaultDependentsShown} more (use full=true)");
        }

        return sb.ToString();
    }
}
