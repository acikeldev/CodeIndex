using System.ComponentModel;
using System.Text;
using CodeIndex.Abstractions;
using CodeIndex.Models;
using ModelContextProtocol.Server;

namespace CodeIndex.Mcp;

[McpServerToolType]
public static class SuggestQueriesTool
{
    [McpServerTool(Name = "suggest_queries")]
    [Description("Get index overview and suggested search queries. Great first call when starting to explore a codebase — shows top projects, largest files, type distribution, and ready-to-run example queries.")]
    public static string SuggestQueries(ICodeIndexStore index)
    {
        StringBuilder sb = new();

        sb.AppendLine("# CodeIndex Overview");
        // Unified project total so it agrees with the merged (C#+TS) file/type/member counts and the Top Projects
        // list below (which includes TS projects via ListProjects).
        int totalProjects = index.ProjectCount + index.TsProjectCount;
        sb.AppendLine($"Projects: {totalProjects}  |  Files: {index.SourceFileCount}  |  Types: {index.TypeCount}  |  Members: {index.MemberCount}");
        sb.AppendLine();

        // Top projects by file count
        List<ProjectIndex> projects = index.ListProjects()
            .OrderByDescending(p => p.SourceFiles.Count)
            .Take(10)
            .ToList();

        sb.AppendLine("## Top Projects (by file count)");
        foreach (ProjectIndex p in projects)
        {
            sb.AppendLine($"  {p.Name} ({p.SourceFiles.Count} files)");
        }
        sb.AppendLine();

        // Largest files by type+member count
        List<(SourceFileIndex File, int Weight)> largestFiles = index.AllSourceFiles
            .Select(f => (File: f, Weight: f.Types.Count + f.Types.Sum(t => t.Members.Count)))
            .OrderByDescending(x => x.Weight)
            .Take(10)
            .ToList();

        sb.AppendLine("## Largest Files (by symbol count)");
        foreach ((SourceFileIndex file, int weight) in largestFiles)
        {
            sb.AppendLine($"  {file.FileName} — {file.Types.Count} types, {file.Types.Sum(t => t.Members.Count)} members ({file.ProjectName})");
        }
        sb.AppendLine();

        // Type kind distribution
        int classes = 0, staticClasses = 0, abstractClasses = 0, sealedClasses = 0, interfaces = 0, enums = 0;
        foreach (SourceFileIndex file in index.AllSourceFiles)
        {
            foreach (TypeInfo type in file.Types)
            {
                switch (type.Kind)
                {
                    case SymbolKind.Class: classes++; break;
                    case SymbolKind.StaticClass: staticClasses++; break;
                    case SymbolKind.AbstractClass: abstractClasses++; break;
                    case SymbolKind.SealedClass: sealedClasses++; break;
                    case SymbolKind.Interface: interfaces++; break;
                    case SymbolKind.Enum: enums++; break;
                }
            }
        }

        sb.AppendLine("## Type Distribution");
        sb.AppendLine($"  Classes: {classes}  |  Static: {staticClasses}  |  Abstract: {abstractClasses}  |  Sealed: {sealedClasses}");
        sb.AppendLine($"  Interfaces: {interfaces}  |  Enums: {enums}");
        sb.AppendLine();

        // Suggested queries
        sb.AppendLine("## Suggested Queries");
        sb.AppendLine("  search_symbol(query='Service', kind='class')    — find service classes");
        sb.AppendLine("  search_symbol(query='Controller', kind='class') — find controllers");
        sb.AppendLine("  get_class_hierarchy(type='IMyInterface')   — interface implementors");

        if (largestFiles.Count > 0)
        {
            string topFile = largestFiles[0].File.FileName;
            sb.AppendLine($"  get_file_outline(file='{topFile}')             — outline of largest file");
        }

        if (projects.Count > 0)
        {
            string topProj = projects[0].Name;
            sb.AppendLine($"  list_files(project='{topProj}')                — files in largest project");
            sb.AppendLine($"  get_project_dependencies(project='{topProj}')  — dependency graph");
        }

        sb.AppendLine("  find_references(symbol='YourMethodName')       — who calls a method?");
        sb.AppendLine("  search_text(query='TODO', ignoreCase=true)     — find TODOs");

        return sb.ToString();
    }
}
