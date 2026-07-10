using System.ComponentModel;
using System.Text;
using CodeIndex.Abstractions;
using CodeIndex.Internal;
using CodeIndex.Models;
using ModelContextProtocol.Server;

namespace CodeIndex.Mcp;

[McpServerToolType]
public static class GetTypeMembersTool
{
    [McpServerTool(Name = "get_type_members")]
    [Description("Get all members of a type by name. Returns constructors, properties, fields, events, and methods with signatures and line numbers. Faster than search_symbol when you know the type name. When several types share the name, pass namespace= or project= to disambiguate; partial types declared across files are merged.")]
    public static string GetTypeMembers(
        ICodeIndexStore index,
        [Description("Type name (e.g., 'MyService', 'Constants')")] string type,
        [Description("Optional member kind filter: method, property, field, constructor, event")] string? kind = null,
        [Description("Optional namespace to disambiguate (full or trailing segment, e.g. 'Models')")] string? @namespace = null,
        [Description("Optional project name to disambiguate (e.g., 'MyApp.Core')")] string? project = null)
    {
        if (kind is not null && !KindFilter.IsValidMemberKind(kind))
        {
            return $"Unknown member kind '{kind}'. Valid kinds: {KindFilter.ValidMemberKindsList()}.";
        }

        TypeResolver.ResolvedType? resolved = TypeResolver.Resolve(index, type, @namespace, project, out string? error);
        if (resolved is null)
        {
            return error!;
        }

        StringBuilder sb = new();
        string inheritance = resolved.BaseTypesDisplay is not null ? $" : {resolved.BaseTypesDisplay}" : string.Empty;
        string parts = resolved.PartCount > 1 ? $" (partial: {resolved.PartCount} files)" : string.Empty;
        sb.AppendLine($"# {resolved.TypeKeyword} {resolved.Name}{inheritance} [{resolved.DisplayFile}:{resolved.StartLine}+{resolved.LineCount}] ({resolved.Project}){parts}");

        IEnumerable<MemberInfo> members = resolved.Members;
        if (kind is not null)
        {
            SymbolKind? filter = kind.ToLowerInvariant() switch
            {
                "method" => SymbolKind.Method,
                "property" => SymbolKind.Property,
                "field" => SymbolKind.Field,
                "constructor" or "ctor" => SymbolKind.Constructor,
                "event" => SymbolKind.Event,
                _ => null
            };
            if (filter is not null)
            {
                members = members.Where(m => m.Kind == filter.Value);
            }
        }

        IEnumerable<IGrouping<SymbolKind, MemberInfo>> grouped = members.GroupBy(m => m.Kind).OrderBy(g => g.Key);
        foreach (IGrouping<SymbolKind, MemberInfo> group in grouped)
        {
            string label = group.Key switch
            {
                SymbolKind.Constructor => "Constructors",
                SymbolKind.Property => "Properties",
                SymbolKind.Field => "Fields",
                SymbolKind.Event => "Events",
                SymbolKind.Method => "Methods",
                _ => group.Key.ToString()
            };
            sb.AppendLine($"{label}:");
            foreach (MemberInfo m in group)
            {
                sb.AppendLine($"  {m.Signature} [{m.StartLine}+{m.LineCount}]");
            }
        }

        if (resolved.Members.Count == 0)
        {
            sb.AppendLine("No members.");
        }

        return sb.ToString();
    }
}
