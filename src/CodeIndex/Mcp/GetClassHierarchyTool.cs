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
    [Description("Inheritance hierarchy for a type: base types (up), derived types/implementors (down). Use to find all implementors of an interface or subclasses of a base type. Pass transitive=true for the ENTIRE subtree (every descendant at any depth, grouped by level) in one call instead of walking it level by level. Pass namespace=/project= to disambiguate a shared name.")]
    public static string GetClassHierarchy(
        ICodeIndexStore index,
        [Description("Type name")] string type,
        [Description("Optional namespace to disambiguate (full or trailing segment)")] string? @namespace = null,
        [Description("Optional project to disambiguate")] string? project = null,
        [Description("Return the full transitive closure of descendants (all depths, grouped by level), not just direct children. Name-based like the rest of the hierarchy: on deep trees it may include same-named types from unrelated namespaces, and namespace= only scopes the root")] bool transitive = false)
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

        if (transitive)
        {
            AppendTransitiveDerived(sb, index, resolved, projectDirs);
        }
        else
        {
            AppendDirectDerived(sb, index, resolved.Name, projectDirs);
        }

        return sb.ToString();
    }

    // Backstops so a pathological or huge hierarchy can't blow the response: cap total listed descendants and the
    // BFS depth. Both are generous — real hierarchies are far smaller.
    private const int MaxTransitiveNodes = 500;
    private const int MaxTransitiveDepth = 25;

    // Direct (one-hop) derived types/implementors — the default, unchanged behaviour.
    private static void AppendDirectDerived(StringBuilder sb, ICodeIndexStore index, string typeName, IReadOnlyDictionary<string, string> projectDirs)
    {
        // Exact simple-name match against each base entry — no substring hits.
        List<(TypeInfo Type, SourceFileIndex File)> derived = index.GetDerivedTypes(typeName);
        if (derived.Count == 0)
        {
            sb.AppendLine("No derived types or implementors found.");
            return;
        }

        sb.AppendLine($"Derived/Implementors ({derived.Count}):");
        foreach ((TypeInfo dt, SourceFileIndex df) in derived.OrderBy(d => d.Type.Name, StringComparer.Ordinal))
        {
            sb.AppendLine(FormatDerived(dt, df, projectDirs, null));
        }
    }

    // Full transitive closure of descendants, grouped by depth — the one-call subtree that grep would need one
    // search per level to build.
    private static void AppendTransitiveDerived(StringBuilder sb, ICodeIndexStore index, TypeResolver.ResolvedType root, IReadOnlyDictionary<string, string> projectDirs)
    {
        List<(TypeInfo Type, SourceFileIndex File, int Depth)> all = CollectTransitive(index, root, out bool truncated);
        if (all.Count == 0)
        {
            sb.AppendLine("No derived types or implementors found.");
            return;
        }

        int maxDepth = all.Max(x => x.Depth);
        sb.AppendLine($"Derived/Implementors (transitive) — {all.Count} across {maxDepth} level(s):");
        foreach ((TypeInfo dt, SourceFileIndex df, int depth) in all.OrderBy(x => x.Depth).ThenBy(x => x.Type.Name, StringComparer.Ordinal))
        {
            sb.AppendLine(FormatDerived(dt, df, projectDirs, depth));
        }

        if (truncated)
        {
            sb.AppendLine($"  … truncated at {MaxTransitiveNodes} nodes / depth {MaxTransitiveDepth}; narrow with project= or query a subtree.");
        }
    }

    private static string FormatDerived(TypeInfo dt, SourceFileIndex df, IReadOnlyDictionary<string, string> projectDirs, int? depth)
    {
        string bases = dt.BaseTypesDisplay is not null ? $" : {dt.BaseTypesDisplay}" : string.Empty;
        string depthTag = depth is not null ? $"[d{depth}] " : string.Empty;
        return $"  {depthTag}{dt.TypeKeyword} {dt.Name}{bases} [{GroupedMatchOutput.RelPath(df.SourceFilePath, df.ProjectName, projectDirs)}:{dt.StartLine}+{dt.LineCount}] ({df.ProjectName})";
    }

    // Breadth-first walk of ALL descendants. visitedNames stops re-expanding a name (cycle / diamond guard);
    // listedKeys dedupes the OUTPUT by exact type location (a type reached two ways is listed once); the root's
    // own location is pre-seeded so a cycle back to the root never lists the root as its own descendant.
    private static List<(TypeInfo Type, SourceFileIndex File, int Depth)> CollectTransitive(ICodeIndexStore index, TypeResolver.ResolvedType root, out bool truncated)
    {
        List<(TypeInfo Type, SourceFileIndex File, int Depth)> results = new();
        HashSet<string> visitedNames = new(StringComparer.OrdinalIgnoreCase) { root.Name };
        HashSet<string> listedKeys = new(StringComparer.Ordinal) { LocationKey(root.Name, root.SourceFilePath, root.StartLine) };
        Queue<(string Name, int Depth)> frontier = new();
        frontier.Enqueue((root.Name, 0));
        truncated = false;

        while (frontier.Count > 0)
        {
            (string name, int depth) = frontier.Dequeue();
            if (depth >= MaxTransitiveDepth)
            {
                // Only a genuine drop is truncation — a leaf that merely sits AT the depth cap is complete, so
                // don't cry "truncated" (and send the agent on wasted follow-ups) unless it actually has children.
                if (index.GetDerivedTypes(name).Count > 0)
                {
                    truncated = true;
                }

                continue;
            }

            foreach ((TypeInfo dt, SourceFileIndex df) in index.GetDerivedTypes(name))
            {
                if (!listedKeys.Add(LocationKey(dt.Name, df.SourceFilePath, dt.StartLine)))
                {
                    continue;
                }

                if (results.Count >= MaxTransitiveNodes)
                {
                    truncated = true;
                    return results;
                }

                results.Add((dt, df, depth + 1));
                if (visitedNames.Add(dt.Name))
                {
                    frontier.Enqueue((dt.Name, depth + 1));
                }
            }
        }

        return results;
    }

    private static string LocationKey(string name, string sourceFilePath, int startLine) => $"{name}|{sourceFilePath}|{startLine}";
}
