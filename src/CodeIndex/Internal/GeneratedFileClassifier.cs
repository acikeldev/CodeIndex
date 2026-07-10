namespace CodeIndex.Internal;

/// <summary>
/// Classifies generated/boilerplate source files by filename so search ranking and reference output can DEMOTE
/// them (rank them below hand-written code) — NEVER exclude them. Demotion matters because generated entities
/// (e.g. ORM models emitted into *.designer.cs) must stay findable. Filename-only and repo-agnostic; the
/// default glob set is overridable via <see cref="UseGlobs"/>, the single seam a future .codeindex config uses.
/// </summary>
internal static class GeneratedFileClassifier
{
    public static readonly IReadOnlyList<string> DefaultGlobs =
    [
        "*.designer.cs", "*.g.cs", "*.g.i.cs", "*.generated.cs",
        "reference.cs", "temporarygeneratedfile_*.cs", "assemblyinfo.cs", "*.assemblyattributes.cs",
    ];

    private static volatile string[] _globs = [.. DefaultGlobs];

    /// <summary>Override the default globs (config seam). Empty/whitespace entries are ignored; an all-empty set is a no-op.</summary>
    public static void UseGlobs(IEnumerable<string> globs)
    {
        string[] arr = globs.Where(g => !string.IsNullOrWhiteSpace(g)).ToArray();
        if (arr.Length > 0)
        {
            _globs = arr;
        }
    }

    public static bool IsGenerated(string sourceFilePathOrName)
    {
        string name = Path.GetFileName(sourceFilePathOrName);
        string[] globs = _globs;
        foreach (string glob in globs)
        {
            if (MatchesGlob(name, glob))
            {
                return true;
            }
        }
        return false;
    }

    // Supports a single '*' anywhere (prefix*suffix); no '*' means an exact filename match. Case-insensitive.
    private static bool MatchesGlob(string name, string glob)
    {
        int star = glob.IndexOf('*');
        if (star < 0)
        {
            return name.Equals(glob, StringComparison.OrdinalIgnoreCase);
        }

        string prefix = glob[..star];
        string suffix = glob[(star + 1)..];
        return name.Length >= prefix.Length + suffix.Length
            && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }
}
