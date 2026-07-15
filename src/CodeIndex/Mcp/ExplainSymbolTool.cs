using System.ComponentModel;
using CodeIndex.Abstractions;
using CodeIndex.Internal;
using CodeIndex.Models;
using ModelContextProtocol.Server;

namespace CodeIndex.Mcp;

/// <summary>
/// One-call symbol dossier. Composes get_type_members / get_class_hierarchy / get_symbol_source / find_references
/// (or, for a method, source + references + callers) into a single response so the agent stops hand-running the
/// search → members → source → refs chain that the client cannot parallelize and that dominates session cost.
/// </summary>
[McpServerToolType]
public static class ExplainSymbolTool
{
    [McpServerTool(Name = "explain_symbol")]
    [Description("One-call symbol dossier: resolves a symbol and returns — for a type — its identity + members + inheritance + source + references, or — for a method/member — its source + references + callers, in a SINGLE response. Use instead of hand-running search_symbol → get_type_members → get_symbol_source → find_references. Pass namespace= or project= to disambiguate; for an editing task use prepare_change instead.")]
    public static string ExplainSymbol(
        ICodeIndexStore index,
        IFileSystem fileSystem,
        [Description("Symbol name — a type ('MyService', 'IFileSystem') or a method/member ('GetItems')")] string symbol,
        [Description("Optional namespace to disambiguate (full or trailing segment, e.g. 'Models')")] string? @namespace = null,
        [Description("Optional project name to disambiguate (e.g., 'MyApp.Core')")] string? project = null)
    {
        TypeResolver.ResolvedType? resolved = TypeResolver.Resolve(index, symbol, @namespace, project, out string? error);
        if (resolved is not null)
        {
            // Use the RESOLVED proper-case name for every section: find_references / call_hierarchy match
            // case-sensitively, so the caller's raw query (which resolves case-insensitively / by substring)
            // would otherwise report zero references for a symbol whose Source we just rendered.
            List<(string Heading, string Body)> sections = new()
            {
                ("Members", GetTypeMembersTool.GetTypeMembers(index, resolved.Name, null, @namespace, project)),
                ("Inheritance", GetClassHierarchyTool.GetClassHierarchy(index, resolved.Name, @namespace, project)),
                ("Source", DossierBuilder.RenderSource(index, fileSystem, resolved.SourceFilePath, resolved.DisplayFile, resolved.StartLine, resolved.LineCount)),
                ("References", FindReferencesTool.FindReferences(index, fileSystem, resolved.Name, project)),
            };
            return DossierBuilder.Assemble($"# explain_symbol: {resolved.TypeKeyword} {resolved.Name} ({resolved.Project})", sections);
        }

        // Ambiguity is the caller's to resolve — hand back the disambiguation list verbatim.
        if (error is not null && error.StartsWith("AMBIGUOUS", StringComparison.Ordinal))
        {
            return error;
        }

        // Not a type — treat the name as a method/member.
        return BuildMemberDossier(index, fileSystem, symbol, project);
    }

    private static string BuildMemberDossier(ICodeIndexStore index, IFileSystem fileSystem, string symbol, string? project)
    {
        List<SymbolSearchResult> results = index.SearchSymbol(symbol, null, project);
        if (results.Count == 0)
        {
            return $"No symbol found matching '{symbol}'."
                + NameSuggester.DidYouMean(symbol, index.SymbolNames(project))
                + " Try search_text for a string/comment/config match.";
        }

        SymbolSearchResult best = results[0];
        string note = results.Count > 1
            ? $"  ({results.Count} matches — showing the top-ranked; pass project= to narrow)"
            : string.Empty;

        List<(string Heading, string Body)> sections = new()
        {
            ("Source", DossierBuilder.RenderSource(index, fileSystem, best.SourceFilePath, best.File, best.StartLine, best.LineCount)),
            ("References", FindReferencesTool.FindReferences(index, fileSystem, best.Name, project)),
            ("Callers", CallHierarchyTool.CallHierarchy(index, best.Name, "callers", "both", project)),
        };
        return DossierBuilder.Assemble($"# explain_symbol: {best.Signature ?? best.Name} [{best.File}:{best.StartLine}+{best.LineCount}] ({best.Project}){note}", sections);
    }
}
