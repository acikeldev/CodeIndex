using System.ComponentModel;
using CodeIndex.Abstractions;
using CodeIndex.Models;
using ModelContextProtocol.Server;

namespace CodeIndex.Mcp;

[McpServerToolType]
public static class ListFilesTool
{
    [McpServerTool(Name = "list_files")]
    [Description("List all source files in a project.")]
    public static string ListFiles(
        ICodeIndexStore index,
        [Description("Project name (e.g., 'MyApp.Core')")] string project)
    {
        ProjectIndex? proj = index.GetProject(project);
        if (proj is null)
        {
            return $"Project '{project}' not found.";
        }

        return string.Join("\n", proj.SourceFiles);
    }
}
