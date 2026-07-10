using MessagePack;

namespace CodeIndex.Models;

/// <summary>
/// TypeScript module-resolution aliases harvested from <c>tsconfig.json</c> (<c>compilerOptions.baseUrl</c>
/// + <c>paths</c>) and <c>package.json</c> (<c>name</c>). Built and persisted in Phase 2 so it is available
/// to the DEFERRED alias-resolution slice (per-file import capture → Usings/UsingAliases so
/// find_references / resolve_bare_name work across the <c>@myapp/*</c> monorepo aliases). Not consumed by any
/// query tool yet — it is inert data until that slice lands.
/// </summary>
[MessagePackObject]
public sealed class AliasMap
{
    /// <summary>Alias pattern (e.g. <c>@myapp/core</c> or <c>@myapp/core/*</c>) → its target path template(s),
    /// relative to the owning tsconfig's baseUrl. Mirrors tsconfig <c>compilerOptions.paths</c>.</summary>
    [Key(0)] public Dictionary<string, List<string>> Paths { get; init; } = [];

    /// <summary><c>package.json</c> <c>name</c> → that package's directory (absolute path).</summary>
    [Key(1)] public Dictionary<string, string> Packages { get; init; } = [];

    public static readonly AliasMap Empty = new();
}
