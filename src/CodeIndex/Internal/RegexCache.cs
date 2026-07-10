using System.Text.RegularExpressions;

namespace CodeIndex.Internal;

/// <summary>
/// Bounded LRU of INTERPRETED regexes (each with a 1s match timeout). Interpreted, not Compiled, on purpose: an
/// evicted <see cref="RegexOptions.Compiled"/> regex never reclaims its emitted dynamic assembly, so a Compiled
/// cache is an unbounded native-memory leak for a long-lived server. FindReferencesTool previously cached Compiled
/// regexes in an UNBOUNDED ConcurrentDictionary and SearchTextTool compiled a fresh one every call — both leaked.
/// This bounds the set and reuses instances across calls. Thread-safe.
/// </summary>
internal static class RegexCache
{
    private const int Capacity = 128;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, LinkedListNode<Entry>> Map = new(StringComparer.Ordinal);
    private static readonly LinkedList<Entry> Lru = new();

    private readonly record struct Entry(string Key, Regex Regex);

    /// <summary>Returns a cached (or newly built) interpreted regex. Throws <see cref="RegexParseException"/> for an
    /// invalid pattern (callers that accept user patterns should catch it).</summary>
    public static Regex Get(string pattern, RegexOptions options)
    {
        string key = ((int)options).ToString() + '\0' + pattern;

        lock (Gate)
        {
            if (Map.TryGetValue(key, out LinkedListNode<Entry>? hit))
            {
                Lru.Remove(hit);
                Lru.AddFirst(hit);
                return hit.Value.Regex;
            }
        }

        // Build OUTSIDE the lock (Compiled stripped — see class remarks). A rare concurrent duplicate build is fine.
        Regex regex = new(pattern, options & ~RegexOptions.Compiled, MatchTimeout);

        lock (Gate)
        {
            if (Map.TryGetValue(key, out LinkedListNode<Entry>? existing))
            {
                Lru.Remove(existing);
                Lru.AddFirst(existing);
                return existing.Value.Regex;
            }

            LinkedListNode<Entry> node = new(new Entry(key, regex));
            Lru.AddFirst(node);
            Map[key] = node;

            if (Map.Count > Capacity)
            {
                LinkedListNode<Entry> evict = Lru.Last!;
                Lru.RemoveLast();
                Map.Remove(evict.Value.Key);
            }
            return regex;
        }
    }
}
