using CodeIndex.Models;

namespace CodeIndex.Internal;

/// <summary>
/// Composite relevance ranking for search_symbol results. Replaces the old exact/prefix/substring-then-name sort
/// (which left the canonical `class Widget` ranked 14th behind generated + throwaway hits). Order:
/// match tier (exact > prefix > substring > camelCase-initials) → type-before-member → hand-written-before-generated
/// → name → path → line. Every input is available on the transient SymbolSearchResult, so no model/cache change.
/// </summary>
internal static class SymbolRanker
{
    public const int TierExact = 0;
    public const int TierPrefix = 1;
    public const int TierSubstring = 2;
    public const int TierSubsequence = 3;
    public const int TierNoMatch = int.MaxValue;

    public static int MatchTier(string name, string query)
    {
        if (name.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return TierExact;
        }
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return TierPrefix;
        }
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return TierSubstring;
        }
        if (IsInitialsSubsequence(name, query))
        {
            return TierSubsequence;
        }
        return TierNoMatch;
    }

    public static SymbolSortKey KeyFor(SymbolSearchResult r, string query)
    {
        int tier = MatchTier(r.Name, query);
        byte typeRank = (byte)(r.ParentType is null ? 0 : 1);           // types before members
        byte genRank = (byte)(GeneratedFileClassifier.IsGenerated(r.File) ? 1 : 0); // hand-written before generated
        return new SymbolSortKey(tier, typeRank, genRank, r.Name, r.SourceFilePath, r.StartLine);
    }

    /// <summary>Does <paramref name="query"/> match the word-boundary initials of <paramref name="name"/>?
    /// e.g. "GUBR" ⊆ initials(GetUsersByRole). Consumes query chars only at word boundaries (start, camelCase hump,
    /// or after '_'/'.'), case-insensitively. Requires query length ≥ 2.</summary>
    public static bool IsInitialsSubsequence(string name, string query)
    {
        if (query.Length < 2 || name.Length == 0)
        {
            return false;
        }

        int qi = 0;
        for (int i = 0; i < name.Length && qi < query.Length; i++)
        {
            char c = name[i];
            bool boundary = i == 0
                || (char.IsUpper(c) && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])))
                || name[i - 1] == '_'
                || name[i - 1] == '.';
            if (boundary && char.ToUpperInvariant(c) == char.ToUpperInvariant(query[qi]))
            {
                qi++;
            }
        }
        return qi == query.Length;
    }
}

/// <summary>Total-order sort key for a search result. The trailing Path + StartLine make ordering deterministic
/// (the old name-only tiebreak + unstable List.Sort left it non-deterministic).</summary>
internal readonly struct SymbolSortKey(int tier, byte typeRank, byte genRank, string name, string path, int startLine)
    : IComparable<SymbolSortKey>
{
    private readonly int _tier = tier;
    private readonly byte _typeRank = typeRank;
    private readonly byte _genRank = genRank;
    private readonly string _name = name;
    private readonly string _path = path;
    private readonly int _startLine = startLine;

    public int CompareTo(SymbolSortKey other)
    {
        int c = _tier.CompareTo(other._tier);
        if (c != 0)
        {
            return c;
        }
        c = _typeRank.CompareTo(other._typeRank);
        if (c != 0)
        {
            return c;
        }
        c = _genRank.CompareTo(other._genRank);
        if (c != 0)
        {
            return c;
        }
        c = string.Compare(_name, other._name, StringComparison.OrdinalIgnoreCase);
        if (c != 0)
        {
            return c;
        }
        c = string.Compare(_path, other._path, StringComparison.Ordinal);
        if (c != 0)
        {
            return c;
        }
        return _startLine.CompareTo(other._startLine);
    }
}
