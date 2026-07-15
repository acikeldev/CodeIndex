using System.ComponentModel;
using System.Text;
using CodeIndex.Abstractions;
using CodeIndex.Internal;
using CodeIndex.Models;
using ModelContextProtocol.Server;

namespace CodeIndex.Mcp;

[McpServerToolType]
public static class GetClassHierarchyTool
{
    [McpServerTool(Name = "get_class_hierarchy")]
    [Description("Inheritance hierarchy for a type: base types (up), derived types/implementors (down). Use to find all implementors of an interface or subclasses of a base type. Pass namespace=/project= to disambiguate a shared name.")]
    public static string GetClassHierarchy(
        ICodeIndexStore index,
        [Description("Type name")] string type,
        [Description("Optional namespace to disambiguate (full or trailing segment)")] string? @namespace = null,
        [Description("Optional project to disambiguate")] string? project = null)
    {
        TypeResolver.ResolvedType? resolved = TypeResolver.Resolve(index, type, @namespace, project, out string? error);
        if (resolved is null)
        {
            return error!;
        }

        IReadOnlyDictionary<string, string> projectDirs = index.ProjectDirsByName();

        StringBuilder sb = new();
        sb.AppendLine($"# {resolved.TypeKeyword} {resolved.Name} [{GroupedMatchOutput.RelPath(resolved.SourceFilePath, resolved.Project, projectDirs)}:{resolved.StartLine}+{resolved.LineCount}] ({resolved.Project})");

        if (resolved.BaseTypesDisplay is not null)
        {
            sb.AppendLine($"Inherits: {resolved.BaseTypesDisplay}");
        }

        sb.AppendLine();

        // Find derived types / implementors (exact simple-name match against each base entry — no substring hits).
        List<(TypeInfo Type, SourceFileIndex File)> derived = index.GetDerivedTypes(resolved.Name);
        if (derived.Count > 0)
        {
            sb.AppendLine($"Derived/Implementors ({derived.Count}):");
            foreach ((TypeInfo dt, SourceFileIndex df) in derived.OrderBy(d => d.Type.Name))
            {
                string bases = dt.BaseTypesDisplay is not null ? $" : {dt.BaseTypesDisplay}" : string.Empty;
                sb.AppendLine($"  {dt.TypeKeyword} {dt.Name}{bases} [{GroupedMatchOutput.RelPath(df.SourceFilePath, df.ProjectName, projectDirs)}:{dt.StartLine}+{dt.LineCount}] ({df.ProjectName})");
            }
        }
        else
        {
            sb.AppendLine("No derived types or implementors found.");
        }

        return sb.ToString();
    }
}
