namespace CodeIndex.Internal;

/// <summary>
/// Path-containment guard. The MCP tools take file arguments from an agent (potentially prompt-injected), so any
/// tool that reads a raw path must confirm the path stays inside the indexed repository — otherwise an argument
/// like C:\Users\me\.aws\credentials would be read and returned. Windows-oriented (case-insensitive) to match the
/// rest of the codebase's path comparisons.
/// </summary>
internal static class PathSecurity
{
    public static bool IsWithinRepo(string path, string? repoRoot)
    {
        if (string.IsNullOrEmpty(repoRoot))
        {
            return false;
        }

        string root = Path.GetFullPath(repoRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(path);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
