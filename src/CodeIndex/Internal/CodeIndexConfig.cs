using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIndex.Abstractions;

namespace CodeIndex.Internal;

/// <summary>
/// Optional per-repo configuration from <c>codeindex.json</c> at the repo root — the seam that makes CodeIndex a
/// generic any-repo tool instead of one wired to a single codebase. All keys optional; a missing/invalid file
/// falls back to defaults and never throws. JSON allows comments and trailing commas.
/// </summary>
internal sealed class CodeIndexConfig
{
    /// <summary>"repo" (default: {repo}/.codeindex), "user" (%LOCALAPPDATA%/CodeIndex/&lt;repo-hash&gt; — survives
    /// read-only checkouts and keeps the repo working tree clean), or an explicit directory path.</summary>
    public string? CacheDir { get; init; }

    /// <summary>Extra directory NAMES to prune during the scan, in addition to the built-ins
    /// (node_modules/bin/obj/.git/.vs/.codeindex).</summary>
    public IReadOnlyList<string>? Exclude { get; init; }

    /// <summary>Override the generated-file globs used for search demotion (default in GeneratedFileClassifier).</summary>
    public IReadOnlyList<string>? GeneratedGlobs { get; init; }

    /// <summary>Index .csproj files not referenced by any solution (needed for solution-less repos, which
    /// otherwise index nothing). Default false preserves the solution-scoped behavior.</summary>
    public bool LooseProjects { get; init; }

    /// <summary>Index TypeScript/TSX/SCSS in the background after the C# index is serving. Default true.
    /// Set false to kill-switch all TS/SCSS work: the watcher skips TS classification and the initial TS
    /// build, so behaviour is byte-identical to the C#-only tool.</summary>
    public bool IndexTypeScript { get; init; } = true;

    /// <summary>
    /// Resolves the base directory for the "user" cache-dir setting. Defaults to the machine's
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/> folder; overridable purely as a test seam
    /// so the "user" branch can be exercised without touching the real profile folder (and without leaking a
    /// concept the <see cref="IFileSystem"/> abstraction does not model).
    /// </summary>
    [JsonIgnore]
    public Func<string> LocalAppDataProvider { get; init; } =
        static () => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static CodeIndexConfig Default { get; } = new();

    public static CodeIndexConfig Load(string repoRoot, IFileSystem fileSystem)
    {
        try
        {
            string path = Path.Combine(repoRoot, "codeindex.json");
            if (!fileSystem.FileExists(path))
            {
                return Default;
            }

            JsonSerializerOptions options = new()
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            CodeIndexConfig? config = JsonSerializer.Deserialize<CodeIndexConfig>(fileSystem.ReadAllText(path), options);
            if (config is not null)
            {
                Console.Error.WriteLine($"[CodeIndex] Loaded codeindex.json from {path}.");
            }

            return config ?? Default;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CodeIndex] codeindex.json could not be read ({ex.GetType().Name}: {ex.Message}) — using defaults.");
            return Default;
        }
    }

    /// <summary>The directory the cache lives in, resolving the "repo"/"user"/explicit setting.</summary>
    public string ResolveCacheDirectory(string repoRoot)
    {
        string setting = CacheDir ?? "repo";

        if (setting.Equals("user", StringComparison.OrdinalIgnoreCase))
        {
            string baseDir = LocalAppDataProvider();
            string key = Path.GetFullPath(repoRoot).ToLowerInvariant();
            string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..12];
            return Path.Combine(baseDir, "CodeIndex", hash);
        }

        if (setting.Equals("repo", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(repoRoot, ".codeindex");
        }

        return setting; // explicit path
    }
}
