using System.Text;
using CodeIndex.Abstractions;
using CodeIndex.Models;

namespace CodeIndex.Internal;

/// <summary>
/// Resolves a type name for the type-scoped tools (get_type_members, get_class_hierarchy). Replaces the old
/// silent first-match behaviour: when several types share a name it either MERGES partial declarations (same
/// name + namespace across files) into one logical type, or returns a disambiguation message listing the
/// candidates so the caller can re-query with namespace=/project=. This is the failure class behind the
/// "which type did I actually get?" incidents.
/// </summary>
internal static class TypeResolver
{
    internal sealed record ResolvedType(
        string Name,
        string TypeKeyword,
        string? BaseTypesDisplay,
        string? Namespace,
        string DisplayFile,
        string SourceFilePath,
        int StartLine,
        int LineCount,
        string Project,
        IReadOnlyList<MemberInfo> Members,
        int PartCount);

    /// <summary>
    /// Returns the resolved (possibly partial-merged) type, or null with <paramref name="error"/> set to a
    /// not-found or ambiguity message ready to return to the caller.
    /// </summary>
    public static ResolvedType? Resolve(ICodeIndexStore index, string typeName, string? ns, string? project, out string? error)
    {
        error = null;
        List<(TypeInfo Type, SourceFileIndex File)> matches = index.FindTypes(typeName);

        if (ns is not null)
        {
            matches = matches.Where(m => NamespaceMatches(m.Type.Namespace ?? m.File.Namespace, ns)).ToList();
        }
        if (project is not null)
        {
            matches = matches.Where(m => m.File.ProjectName.Equals(project, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (matches.Count == 0)
        {
            error = $"Type '{typeName}'" +
                    (ns is not null ? $" in namespace '{ns}'" : string.Empty) +
                    (project is not null ? $" in project '{project}'" : string.Empty) +
                    " not found in index." +
                    NameSuggester.DidYouMean(typeName, index.TypeNames()) +
                    " Try search_symbol to match by partial name, or get_file_outline if you know the declaring file.";
            return null;
        }

        // Group by namespace: same name + same namespace = parts of one (partial) type; different namespaces = genuinely different types.
        List<IGrouping<string, (TypeInfo Type, SourceFileIndex File)>> groups = matches
            .GroupBy(m => (m.Type.Namespace ?? m.File.Namespace) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (groups.Count > 1)
        {
            StringBuilder sb = new();
            sb.AppendLine($"AMBIGUOUS: {groups.Count} types named '{typeName}' in different namespaces — pass namespace= (or project=) to pick one:");
            foreach (IGrouping<string, (TypeInfo Type, SourceFileIndex File)> g in groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                (TypeInfo Type, SourceFileIndex File) first = g.First();
                string nsLabel = g.Key.Length == 0 ? "(global namespace)" : g.Key;
                sb.AppendLine($"  - {nsLabel}  [{first.File.FileName}] ({first.File.ProjectName})");
            }
            error = sb.ToString().TrimEnd();
            return null;
        }

        // Single namespace group — merge any partial parts into one logical type.
        List<(TypeInfo Type, SourceFileIndex File)> parts = groups[0].ToList();
        (TypeInfo Type, SourceFileIndex File) primary = parts
            .OrderByDescending(p => p.Type.BaseTypes is { Count: > 0 })
            .ThenBy(p => p.Type.StartLine)
            .First();

        List<MemberInfo> mergedMembers = parts.SelectMany(p => p.Type.Members).ToList();
        List<string> mergedBases = parts
            .Where(p => p.Type.BaseTypes is not null)
            .SelectMany(p => p.Type.BaseTypes!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new ResolvedType(
            Name: primary.Type.Name,
            TypeKeyword: primary.Type.TypeKeyword,
            BaseTypesDisplay: mergedBases.Count > 0 ? string.Join(", ", mergedBases) : null,
            Namespace: primary.Type.Namespace ?? primary.File.Namespace,
            DisplayFile: primary.File.FileName,
            SourceFilePath: primary.File.SourceFilePath,
            StartLine: primary.Type.StartLine,
            LineCount: primary.Type.LineCount,
            Project: primary.File.ProjectName,
            Members: mergedMembers,
            PartCount: parts.Count);
    }

    // A candidate namespace matches the requested filter if it equals it or ends with ".<filter>" (suffix match
    // lets the caller pass just the tail, e.g. namespace='Models' for 'MyApp.Data.Models').
    private static bool NamespaceMatches(string? candidate, string requested)
    {
        if (candidate is null)
        {
            return false;
        }
        return candidate.Equals(requested, StringComparison.OrdinalIgnoreCase)
            || candidate.EndsWith("." + requested, StringComparison.OrdinalIgnoreCase);
    }
}
