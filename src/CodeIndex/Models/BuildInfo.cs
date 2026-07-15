namespace CodeIndex.Models;

/// <summary>
/// How the current index came to be — surfaced by the <c>repo_info</c> health tool.
/// <paramref name="Kind"/> is one of <c>none</c> / <c>cache-load</c> / <c>full</c> / <c>delta</c>.
/// </summary>
public sealed record BuildInfo(DateTime WhenUtc, string Kind, long DurationMs, int Reparsed, int Removed);
