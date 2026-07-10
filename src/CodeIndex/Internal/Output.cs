namespace CodeIndex.Internal;

/// <summary>
/// Shared output-shaping helpers for the MCP tools: a common token estimate and match-line clipping so a single
/// pathological source line (e.g. a 1,800-char embedded SQL string) can't dominate a response. Keeping these in
/// one place makes the token-budget contract consistent across tools.
/// </summary>
internal static class Output
{
    // The host caps a single tool response at ~25k tokens; keep tool self-limits comfortably under that.
    public const int MaxResponseTokens = 20_000;

    // Rough token estimate (chars/4). Deliberately crude — used only for budgeting, never reported as exact.
    public static int EstimateTokens(string text) => (int)Math.Ceiling(text.Length / 4.0);

    /// <summary>
    /// Clip a match line for display so no single line blows the token budget. When <paramref name="focus"/> is
    /// found, the kept window is centred on it (with leading/trailing "…"); otherwise the head is kept. The caller
    /// still has the line number, so the full text is one get_symbol_source away.
    /// </summary>
    public static string ClipLine(string line, string? focus = null, int max = 200)
    {
        if (line.Length <= max)
        {
            return line;
        }

        int focusIdx = focus is not null ? line.IndexOf(focus, StringComparison.Ordinal) : -1;
        if (focusIdx < 0)
        {
            return line[..max] + " …";
        }

        int half = max / 2;
        int start = Math.Max(0, focusIdx - half);
        int end = Math.Min(line.Length, start + max);
        start = Math.Max(0, end - max); // re-expand left if we clamped on the right
        string clipped = line[start..end];
        string prefix = start > 0 ? "… " : string.Empty;
        string suffix = end < line.Length ? " …" : string.Empty;
        return prefix + clipped + suffix;
    }
}
