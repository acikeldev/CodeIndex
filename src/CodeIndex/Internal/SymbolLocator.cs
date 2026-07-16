using CodeIndex.Models;

namespace CodeIndex.Internal;

/// <summary>
/// Maps a source line to the innermost declared symbol that encloses it, using the stored type/member line ranges
/// (no re-parse). Lets text search tag each hit with its enclosing Type.Member — structural context grep cannot
/// give without a follow-up read.
/// </summary>
internal static class SymbolLocator
{
    /// <summary>
    /// Innermost "Type.Member" (or "Type", or null when the line is outside any type — e.g. usings/namespace)
    /// whose declared range contains <paramref name="line"/> (1-based). Innermost = the containing declaration
    /// with the largest start line, so nested types and the specific member win over their encloser.
    /// </summary>
    public static string? EnclosingSymbol(SourceFileIndex file, int line)
    {
        TypeInfo? bestType = null;
        foreach (TypeInfo t in file.Types)
        {
            if (line >= t.StartLine && line < t.StartLine + t.LineCount && (bestType is null || t.StartLine > bestType.StartLine))
            {
                bestType = t;
            }
        }

        if (bestType is null)
        {
            return null;
        }

        MemberInfo? bestMember = null;
        foreach (MemberInfo m in bestType.Members)
        {
            if (line >= m.StartLine && line < m.StartLine + m.LineCount && (bestMember is null || m.StartLine > bestMember.StartLine))
            {
                bestMember = m;
            }
        }

        return bestMember is null ? bestType.Name : $"{bestType.Name}.{bestMember.Name}";
    }
}
