using System.ComponentModel;
using System.Text;
using CodeIndex.Abstractions;
using CodeIndex.Models;
using ModelContextProtocol.Server;

namespace CodeIndex.Mcp;

[McpServerToolType]
public static class ListProjectsTool
{
    [McpServerTool(Name = "list_projects")]
    [Description("List all indexed projects with their source file counts.")]
    public static string ListProjects(ICodeIndexStore index)
    {
        List<ProjectIndex> projects = index.ListProjects();
        if (projects.Count == 0)
        {
            return "No projects indexed.";
        }

        StringBuilder sb = new();
        foreach (ProjectIndex p in projects)
        {
            sb.AppendLine($"{p.Name} ({p.SourceFiles.Count} files)");
        }
        return sb.ToString();
    }
}
