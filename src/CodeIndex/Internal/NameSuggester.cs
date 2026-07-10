namespace CodeIndex.Internal;

/// <summary>
/// Suggests the nearest indexed names for a miss ("Did you mean …?"), so a typo or plural/singular slip becomes a
/// self-correcting single call instead of a wasted round trip. Strict tiered near-bar (exact / prefix / substring
/// / short bounded edit-distance) — NEVER "closest of everything" — so a genuinely-wrong query adds zero tokens.
/// Pure string logic; repo-agnostic; runs only on the (rare) miss path.
/// </summary>
internal static class NameSuggester
{
    private enum NearTier { Exact = 0, Prefix = 1, Substring = 2, Fuzzy = 3 }

    private readonly record struct Scored(string Name, NearTier Tier, int Dist, int LenDelta);

    public static IReadOnlyList<string> Nearest(string query, IEnumerable<string> candidates, int max = 5)
    {
        if (string.IsNullOrEmpty(query))
        {
            return [];
        }

        string qLower = query.ToLowerInvariant();
        int qLen = query.Length;
        int threshold = Threshold(qLen);

        Dictionary<string, Scored> best = new(StringComparer.OrdinalIgnoreCase);
        foreach (string c in candidates)
        {
            if (string.IsNullOrEmpty(c))
            {
                continue;
            }
            Scored? scored = Classify(qLower, qLen, threshold, c);
            if (scored is null)
            {
                continue;
            }
            if (!best.TryGetValue(c, out Scored existing) || IsBetter(scored.Value, existing))
            {
                best[c] = scored.Value;
            }
        }

        return best.Values
            .OrderBy(s => s.Tier)
            .ThenBy(s => s.Dist)
            .ThenBy(s => s.LenDelta)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .Take(max)
            .Select(s => s.Name)
            .ToList();
    }

    /// <summary>"\nDid you mean: A, B, C?" or empty when nothing is close.</summary>
    public static string DidYouMean(string query, IEnumerable<string> candidates, int max = 5)
    {
        IReadOnlyList<string> near = Nearest(query, candidates, max);
        return near.Count == 0 ? string.Empty : "\nDid you mean: " + string.Join(", ", near) + "?";
    }

    private static bool IsBetter(Scored a, Scored b) => a.Tier < b.Tier || (a.Tier == b.Tier && a.Dist < b.Dist);

    private static int Threshold(int qLen) => qLen <= 4 ? 1 : qLen <= 8 ? 2 : 3;

    private static Scored? Classify(string qLower, int qLen, int threshold, string candidate)
    {
        string cLower = candidate.ToLowerInvariant();
        int lenDelta = Math.Abs(cLower.Length - qLen);

        if (cLower == qLower)
        {
            return new Scored(candidate, NearTier.Exact, 0, lenDelta);
        }
        if (cLower.StartsWith(qLower, StringComparison.Ordinal) || qLower.StartsWith(cLower, StringComparison.Ordinal))
        {
            return new Scored(candidate, NearTier.Prefix, lenDelta, lenDelta);
        }
        if (qLen >= 3 && (cLower.Contains(qLower, StringComparison.Ordinal) || qLower.Contains(cLower, StringComparison.Ordinal)))
        {
            return new Scored(candidate, NearTier.Substring, lenDelta, lenDelta);
        }
        if (lenDelta <= threshold)
        {
            int d = BoundedLevenshtein(qLower, cLower, threshold);
            if (d <= threshold)
            {
                return new Scored(candidate, NearTier.Fuzzy, d, lenDelta);
            }
        }
        return null;
    }

    // Two-row DP with a per-row minimum early-exit: returns max+1 as soon as the best possible distance exceeds max.
    private static int BoundedLevenshtein(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max)
        {
            return max + 1;
        }

        int[] prev = new int[b.Length + 1];
        int[] curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            int rowMin = curr[0];
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1), prev[j - 1] + cost);
                if (curr[j] < rowMin)
                {
                    rowMin = curr[j];
                }
            }
            if (rowMin > max)
            {
                return max + 1;
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}
