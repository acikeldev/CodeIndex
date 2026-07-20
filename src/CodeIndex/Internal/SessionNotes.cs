using System.Text;
using CodeIndex.Abstractions;
using CodeIndex.Caching;

namespace CodeIndex.Internal;

/// <summary>
/// Cross-session repo notes: short agent-authored facts persisted to the repo's cache directory and surfaced by
/// get_onboarding on every future session, so a hard-won orientation ("auth entry point is AuthController.Login",
/// "DTOs are generated — edit the template") is discovered ONCE and amortized across sessions instead of
/// re-derived each time (SWE-agent's observation-elision / Serena's .serena/memories, made local and syntactic).
/// Stored SEPARATELY from the build-keyed onboarding digest so a reindex never drops them. Best-effort and local:
/// one short single-line note per line, newest kept, deduped, capped.
/// </summary>
internal static class SessionNotes
{
    internal const string FileName = "notes.v1.md";
    private const int MaxNotes = 50;
    private const int MaxNoteLength = 300;

    public static string NotesPath(string cacheDirectory) => Path.Combine(cacheDirectory, FileName);

    /// <summary>Persist a note (deduped + moved to newest), capped to the most recent <see cref="MaxNotes"/>.</summary>
    public static string Add(IFileSystem fileSystem, string cacheDirectory, string note)
    {
        string cleaned = Normalize(note);
        if (cleaned.Length == 0)
        {
            return "remember: empty note ignored.";
        }

        List<string> notes = Load(fileSystem, cacheDirectory);
        notes.RemoveAll(n => string.Equals(n, cleaned, StringComparison.Ordinal)); // dedup → moves it to newest
        notes.Add(cleaned);
        if (notes.Count > MaxNotes)
        {
            notes.RemoveRange(0, notes.Count - MaxNotes);
        }

        try
        {
            AtomicCacheIo io = new(fileSystem);
            io.WriteAtomic(NotesPath(cacheDirectory), Encoding.UTF8.GetBytes(string.Join("\n", notes) + "\n"));
        }
        catch (Exception)
        {
            return "remember: note could not be persisted (cache not writable) — it will not survive this session.";
        }

        return $"Remembered. {notes.Count} note(s) stored for this repo; they appear in get_onboarding and survive restart + reindex.";
    }

    /// <summary>All stored notes, oldest first. Best-effort: any read failure yields an empty list.</summary>
    public static List<string> Load(IFileSystem fileSystem, string cacheDirectory)
    {
        try
        {
            string path = NotesPath(cacheDirectory);
            if (!fileSystem.FileExists(path))
            {
                return new List<string>();
            }

            return fileSystem.ReadAllText(path)
                .Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Length > 0)
                .ToList();
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }

    /// <summary>The notes rendered as a get_onboarding section (newest first), or an empty string when there are
    /// none — so onboarding output stays byte-identical on a repo that has never used remember.</summary>
    public static string RenderSection(IFileSystem fileSystem, string cacheDirectory)
    {
        List<string> notes = Load(fileSystem, cacheDirectory);
        if (notes.Count == 0)
        {
            return string.Empty;
        }

        StringBuilder sb = new();
        sb.AppendLine();
        sb.AppendLine("## Notes from previous sessions (via remember)");
        for (int i = notes.Count - 1; i >= 0; i--)
        {
            sb.AppendLine($"- {notes[i]}");
        }

        return sb.ToString();
    }

    // Notes are single-line for stable storage/rendering: collapse whitespace, strip newlines, cap length.
    private static string Normalize(string note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return string.Empty;
        }

        string oneLine = note.Replace('\r', ' ').Replace('\n', ' ').Trim();
        while (oneLine.Contains("  ", StringComparison.Ordinal))
        {
            oneLine = oneLine.Replace("  ", " ");
        }

        return oneLine.Length > MaxNoteLength ? oneLine[..MaxNoteLength] : oneLine;
    }
}
