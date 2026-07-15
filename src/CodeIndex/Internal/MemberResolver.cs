using System.Text;
using CodeIndex.Abstractions;
using CodeIndex.Models;

namespace CodeIndex.Internal;

/// <summary>
/// Resolves a member (or, as a fallback, a type) by NAME to its source line range for get_symbol_source's
/// member-addressed mode — so the caller need not know line numbers. Mirrors <see cref="TypeResolver"/>: exact
/// name match, honours type/namespace/project filters, and returns a disambiguation listing when a name is shared
/// (across types in a file, across files, or as overloads). A whole-type read above a size threshold is refused
/// with guidance so the tool never re-introduces the whole-file read it exists to eliminate.
/// </summary>
internal static class MemberResolver
{
    // A single type body larger than this is not dumped; the caller is steered to outline + member= instead.
    private const int LargeTypeLineThreshold = 300;

    internal sealed record MemberLocation(
        string TypeName,
        string Signature,
        string SourceFilePath,
        int StartLine,
        int LineCount,
        string Project);

    public static MemberLocation? Resolve(
        ICodeIndexStore index, string member, string? file, string? type, string? ns, string? project, int? startLineHint, out string? error)
    {
        error = null;

        IReadOnlyList<SourceFileIndex> files;
        if (file is not null)
        {
            SourceFileIndex? scoped = index.GetFileOutline(file);
            if (scoped is null)
            {
                error = $"File '{file}' not found in index." + NameSuggester.DidYouMean(file, index.FileNames());
                return null;
            }

            files = [scoped];
        }
        else
        {
            files = index.AllSourceFiles;
        }

        List<MemberLocation> hits = new();
        List<string> memberNamesInScope = new();
        foreach (SourceFileIndex f in files)
        {
            if (project is not null && !f.ProjectName.Equals(project, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (TypeInfo t in f.Types)
            {
                if (type is not null && !t.Name.Equals(type, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (ns is not null && !NamespaceMatches(t.Namespace ?? f.Namespace, ns))
                {
                    continue;
                }

                foreach (MemberInfo m in t.Members)
                {
                    memberNamesInScope.Add(m.Name);
                    if (m.Name.Equals(member, StringComparison.OrdinalIgnoreCase))
                    {
                        hits.Add(new MemberLocation(t.Name, m.Signature, f.SourceFilePath, m.StartLine, m.LineCount, f.ProjectName));
                    }
                }
            }
        }

        // Partial types re-list the same physical member; collapse identical spans.
        List<MemberLocation> distinct = hits
            .GroupBy(h => (h.SourceFilePath, h.StartLine, h.LineCount))
            .Select(g => g.First())
            .ToList();

        if (distinct.Count == 1)
        {
            return distinct[0];
        }

        if (distinct.Count > 1)
        {
            // An explicit startLine selects one overload/among-duplicates — the advertised escape hatch when
            // type=/namespace=/project= can't split overloads (they share all three).
            if (startLineHint is not null)
            {
                List<MemberLocation> atLine = distinct.Where(d => d.StartLine == startLineHint.Value).ToList();
                if (atLine.Count == 1)
                {
                    return atLine[0];
                }
            }

            error = BuildAmbiguous(index, member, distinct);
            return null;
        }

        // No member matched. Only fall back to a same-named TYPE when the caller did NOT scope to a file/type —
        // otherwise we'd hand back an unrelated type's body from a different file than the one they asked about.
        if (file is null && type is null)
        {
            MemberLocation? asType = ResolveAsType(index, member, ns, project, out string? typeError);
            if (asType is not null || typeError is not null)
            {
                error = typeError;
                return asType;
            }
        }

        error = $"Member '{member}'"
            + (type is not null ? $" in type '{type}'" : string.Empty)
            + (file is not null ? $" in file '{file}'" : string.Empty)
            + " not found in index."
            + NameSuggester.DidYouMean(member, memberNamesInScope)
            + " Try get_file_outline for the declaring file, or search_symbol for a partial name.";
        return null;
    }

    private static MemberLocation? ResolveAsType(ICodeIndexStore index, string name, string? ns, string? project, out string? error)
    {
        error = null;
        TypeResolver.ResolvedType? t = TypeResolver.Resolve(index, name, ns, project, out string? resolveError);
        if (t is null)
        {
            // Propagate only a genuine ambiguity; a plain miss stays null so the caller emits its member-not-found.
            error = resolveError is not null && resolveError.StartsWith("AMBIGUOUS", StringComparison.Ordinal) ? resolveError : null;
            return null;
        }

        if (t.LineCount > LargeTypeLineThreshold)
        {
            error = $"Type '{t.Name}' is {t.LineCount} lines — too large to dump. Use get_file_outline('{t.DisplayFile}') then get_symbol_source(member=...) for one member, or pass startLine+lineCount to force a window.";
            return null;
        }

        // Empty TypeName marks a whole-type render so the header reads "class Foo", not "Foo.class Foo".
        return new MemberLocation(string.Empty, $"{t.TypeKeyword} {t.Name}", t.SourceFilePath, t.StartLine, t.LineCount, t.Project);
    }

    private static string BuildAmbiguous(ICodeIndexStore index, string member, List<MemberLocation> matches)
    {
        IReadOnlyDictionary<string, string> dirs = index.ProjectDirsByName();
        StringBuilder sb = new();
        sb.AppendLine($"AMBIGUOUS: {matches.Count} members named '{member}' — pass type=/namespace=/project= to pick one, or (for overloads) re-call with member= plus the startLine= of the one you want from below:");
        foreach (MemberLocation m in matches.OrderBy(m => m.Project, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.StartLine))
        {
            sb.AppendLine($"  - {m.TypeName}.{m.Signature} [{GroupedMatchOutput.RelPath(m.SourceFilePath, m.Project, dirs)}:{m.StartLine}+{m.LineCount}] ({m.Project})");
        }

        return sb.ToString().TrimEnd();
    }

    // Same suffix rule as TypeResolver's private namespace match; duplicated (10 lines) rather than widening that API.
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
