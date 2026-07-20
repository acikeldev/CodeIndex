using System.ComponentModel;
using CodeIndex.Abstractions;
using CodeIndex.Internal;
using CodeIndex.Models;
using ModelContextProtocol.Server;

namespace CodeIndex.Mcp;

/// <summary>
/// One-call edit briefing. Like explain_symbol but ordered and scoped for MAKING A CHANGE: the definition, then
/// every impact site — call sites, callers, and (for a type) implementors/overrides — so the agent gathers the
/// blast radius in one round-trip instead of a pre-edit multi-tool sweep.
/// </summary>
[McpServerToolType]
public static class PrepareChangeTool
{
    // Widen the reference sweep vs the find_references default — before an edit you want the full blast radius.
    private const int RefMax = 60;
    private const int RefPerFile = 5;

    [McpServerTool(Name = "prepare_change")]
    [Description("One-call edit briefing for a symbol you're about to change: its definition, every call site (widened find_references), enclosing-member-labelled callers, and — for a type/interface — implementors/overrides. The full blast radius in one response instead of a pre-edit multi-tool sweep. Pass namespace=/project= to disambiguate. Pass verbosity='concise' to scope the blast radius without the definition body.")]
    public static string PrepareChange(
        ICodeIndexStore index,
        IFileSystem fileSystem,
        [Description("Symbol name — a type or a method/member")] string symbol,
        [Description("Optional namespace to disambiguate (full or trailing segment)")] string? @namespace = null,
        [Description("Optional project to disambiguate")] string? project = null,
        [Description("'detailed' (default, includes the definition body) or 'concise' (call sites + callers + implementors only, body omitted — fetch it with get_symbol_source)")] string? verbosity = null)
    {
        bool concise = DossierBuilder.IsConcise(verbosity);
        string tag = concise ? " [concise]" : string.Empty;
        TypeResolver.ResolvedType? resolved = TypeResolver.Resolve(index, symbol, @namespace, project, out string? error);
        if (resolved is not null)
        {
            // Resolved proper-case name for every section — the case-sensitive reference/call matchers would
            // otherwise miss a symbol resolved case-insensitively (see ExplainSymbolTool for the same fix).
            List<(string Heading, string Body)> sections = new()
            {
                ("Definition", DossierBuilder.RenderSource(index, fileSystem, resolved.SourceFilePath, resolved.DisplayFile, resolved.StartLine, resolved.LineCount, concise)),
                ("Call sites", FindReferencesTool.FindReferences(index, fileSystem, resolved.Name, project, max: RefMax, perFileMax: RefPerFile)),
                ("Implementors / overrides", GetClassHierarchyTool.GetClassHierarchy(index, resolved.Name, @namespace, project)),
                ("Callers", CallHierarchyTool.CallHierarchy(index, resolved.Name, "callers", "both", project)),
            };
            return DossierBuilder.Assemble($"# prepare_change: {resolved.TypeKeyword} {resolved.Name} ({resolved.Project}){tag}", sections);
        }

        if (error is not null && error.StartsWith("AMBIGUOUS", StringComparison.Ordinal))
        {
            return error;
        }

        List<SymbolSearchResult> results = index.SearchSymbol(symbol, null, project);
        if (results.Count == 0)
        {
            return $"No symbol found matching '{symbol}'."
                + NameSuggester.DidYouMean(symbol, index.SymbolNames(project))
                + " Try search_text for a string/comment/config match.";
        }

        SymbolSearchResult best = results[0];
        List<(string Heading, string Body)> memberSections = new()
        {
            ("Definition", DossierBuilder.RenderSource(index, fileSystem, best.SourceFilePath, best.File, best.StartLine, best.LineCount, concise)),
            ("Call sites", FindReferencesTool.FindReferences(index, fileSystem, best.Name, project, max: RefMax, perFileMax: RefPerFile)),
            ("Callers", CallHierarchyTool.CallHierarchy(index, best.Name, "callers", "both", project)),
        };
        return DossierBuilder.Assemble($"# prepare_change: {best.Signature ?? best.Name} [{GroupedMatchOutput.RelPath(best.SourceFilePath, best.Project, index.ProjectDirsByName())}:{best.StartLine}+{best.LineCount}] ({best.Project}){tag}", memberSections);
    }
}
