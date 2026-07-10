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
    [Description("Get inheritance hierarchy for a type: base types (up) and derived types / implementors (down). Use to find all classes implementing an interface or extending a base class. Pass namespace= or project= to disambiguate when several types share the name.")]
    public static string GetClassHierarchy(
        ICodeIndexStore index,
        [Description("Type name (e.g., 'IMyService', 'MyService')")] string type,
        [Description("Optional namespace to disambiguate (full or trailing segment, e.g. 'Models')")] string? @namespace = null,
        [Description("Optional project name to disambiguate (e.g., 'MyApp.Core')")] string? project = null)
    {
        TypeResolver.ResolvedType? resolved = TypeResolver.Resolve(index, type, @namespace, project, out string? error);
        if (resolved is null)
        {
            return error!;
        }

        StringBuilder sb = new();
        sb.AppendLine($"# {resolved.TypeKeyword} {resolved.Name} [{resolved.DisplayFile}:{resolved.StartLine}+{resolved.LineCount}] ({resolved.Project})");

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
                sb.AppendLine($"  {dt.TypeKeyword} {dt.Name}{bases} [{df.FileName}:{dt.StartLine}+{dt.LineCount}] ({df.ProjectName})");
            }
        }
        else
        {
            sb.AppendLine("No derived types or implementors found.");
        }

        return sb.ToString();
    }
}
