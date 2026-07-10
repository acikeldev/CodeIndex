using System.ComponentModel;
using System.Text;
using CodeIndex.Abstractions;
using CodeIndex.Internal;
using CodeIndex.Models;
using ModelContextProtocol.Server;

namespace CodeIndex.Mcp;

[McpServerToolType]
public static class GetFileOutlineTool
{
    // Above this, the full member-by-member outline is replaced by a types-only summary so god-classes
    // (e.g. a 1,400-member file) can never exceed the MCP response cap and hard-fail the call.
    private const int MaxOutlineChars = 48_000;

    [McpServerTool(Name = "get_file_outline")]
    [Description("Get all types and members of a source file with signatures and line numbers. Accepts full or partial filename. Very large files degrade to a types-only summary (call get_type_members for a specific type). Pass typesOnly=true to force the summary.")]
    public static string GetFileOutline(
        ICodeIndexStore index,
        [Description("Filename to look up (e.g., 'Constants.cs' or 'MyService.svc.cs')")] string file,
        [Description("Return only the type list with per-type member counts (default false)")] bool typesOnly = false)
    {
        SourceFileIndex? result = index.GetFileOutline(file);
        if (result is null)
        {
            return $"File '{file}' not found in index."
                + NameSuggester.DidYouMean(file, index.FileNames())
                + " Try list_files to browse indexed files, or search_symbol if you know a type it declares.";
        }

        if (typesOnly)
        {
            return FormatTypesOnly(result, reason: "typesOnly=true");
        }

        string full = FormatOutline(result);
        // Guard against the response-token cap: fall back to the compact summary when the full outline is too large.
        if (full.Length > MaxOutlineChars)
        {
            return FormatTypesOnly(result, reason: $"full outline ~{full.Length / 1000}k chars exceeds the response budget");
        }

        return full;
    }

    private static string FormatTypesOnly(SourceFileIndex file, string reason)
    {
        int totalMembers = file.Types.Sum(t => t.Members.Count);
        StringBuilder sb = new();
        sb.AppendLine($"# {file.FileName} ({file.ProjectName}) — types-only summary ({reason})");
        if (file.Namespace is not null)
        {
            sb.Append($"Namespace: {file.Namespace}  ");
        }

        sb.AppendLine($"Source: {file.SourceFilePath}");
        sb.AppendLine($"{file.Types.Count} types, {totalMembers} members. Call get_type_members(type=...) for a type's members.");
        sb.AppendLine();

        foreach (TypeInfo type in file.Types)
        {
            string prefix = type.IsNested ? "  " : string.Empty;
            string inheritance = type.BaseTypesDisplay is not null ? $" : {type.BaseTypesDisplay}" : string.Empty;
            int ctors = type.Members.Count(m => m.Kind == SymbolKind.Constructor);
            int props = type.Members.Count(m => m.Kind == SymbolKind.Property);
            int fields = type.Members.Count(m => m.Kind == SymbolKind.Field);
            int events = type.Members.Count(m => m.Kind == SymbolKind.Event);
            int methods = type.Members.Count(m => m.Kind == SymbolKind.Method);
            List<string> counts = new();
            if (ctors > 0)
            {
                counts.Add($"{ctors} ctors");
            }

            if (props > 0)
            {
                counts.Add($"{props} props");
            }

            if (fields > 0)
            {
                counts.Add($"{fields} fields");
            }

            if (events > 0)
            {
                counts.Add($"{events} events");
            }

            if (methods > 0)
            {
                counts.Add($"{methods} methods");
            }

            string tail = counts.Count > 0 ? $" — {string.Join(", ", counts)}" : string.Empty;
            sb.AppendLine($"{prefix}{type.TypeKeyword} {type.Name}{inheritance} [{type.StartLine}+{type.LineCount}]{tail}");
        }

        return sb.ToString();
    }

    private static string FormatOutline(SourceFileIndex file)
    {
        StringBuilder sb = new();
        sb.AppendLine($"# {file.FileName} ({file.ProjectName})");
        if (file.Namespace is not null)
        {
            sb.Append($"Namespace: {file.Namespace}  ");
        }

        sb.AppendLine($"Source: {file.SourceFilePath}");
        sb.AppendLine();

        foreach (TypeInfo type in file.Types)
        {
            string prefix = type.IsNested ? "  " : string.Empty;
            string heading = type.IsNested ? "###" : "##";
            string inheritance = type.BaseTypesDisplay is not null ? $" : {type.BaseTypesDisplay}" : string.Empty;
            sb.AppendLine($"{prefix}{heading} {type.TypeKeyword} {type.Name}{inheritance} [{type.StartLine}+{type.LineCount}]");

            if (type.EnumValues is not null)
            {
                sb.AppendLine($"{prefix}Values: {type.EnumValues}");
            }

            List<MemberInfo> constructors = type.Members.Where(m => m.Kind == SymbolKind.Constructor).ToList();
            List<MemberInfo> properties = type.Members.Where(m => m.Kind == SymbolKind.Property).ToList();
            List<MemberInfo> fields = type.Members.Where(m => m.Kind == SymbolKind.Field).ToList();
            List<MemberInfo> events = type.Members.Where(m => m.Kind == SymbolKind.Event).ToList();
            List<MemberInfo> methods = type.Members.Where(m => m.Kind == SymbolKind.Method).ToList();

            if (constructors.Count > 0)
            {
                sb.AppendLine($"{prefix}Constructors:");
                foreach (MemberInfo m in constructors)
                {
                    sb.AppendLine($"{prefix}  - {m.Signature} [{m.StartLine}+{m.LineCount}]");
                }
            }

            if (properties.Count > 0)
            {
                sb.AppendLine($"{prefix}Properties:");
                foreach (MemberInfo m in properties)
                {
                    sb.AppendLine($"{prefix}  - {m.Signature} [{m.StartLine}+{m.LineCount}]");
                }
            }

            if (fields.Count > 0)
            {
                sb.AppendLine($"{prefix}Fields:");
                foreach (MemberInfo m in fields)
                {
                    sb.AppendLine($"{prefix}  - {m.Signature} [{m.StartLine}+{m.LineCount}]");
                }
            }

            if (events.Count > 0)
            {
                sb.AppendLine($"{prefix}Events:");
                foreach (MemberInfo m in events)
                {
                    sb.AppendLine($"{prefix}  - {m.Signature} [{m.StartLine}+{m.LineCount}]");
                }
            }

            if (methods.Count > 0)
            {
                sb.AppendLine($"{prefix}Methods:");
                foreach (MemberInfo m in methods)
                {
                    sb.AppendLine($"{prefix}  - {m.Signature} [{m.StartLine}+{m.LineCount}]");
                }
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }
}
