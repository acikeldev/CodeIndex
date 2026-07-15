using System.ComponentModel;
using System.Text;
using CodeIndex.Abstractions;
using CodeIndex.Caching;
using CodeIndex.Models;
using ModelContextProtocol.Server;

namespace CodeIndex.Mcp;

/// <summary>
/// One-call repo orientation for a fresh session: the projects, the most important symbols (PageRank), and a
/// starting-point note — assembled once and cached on disk, keyed to the index build. Returning to a repo is then
/// a single cheap call instead of repo_info + repo_map + a few probes, and the digest survives a server restart.
/// The cache signature includes the build stamp, so a code change invalidates it automatically (regenerate).
/// This is also the repo-level base that <c>get_task_context</c> reuses.
/// </summary>
[McpServerToolType]
public static class GetOnboardingTool
{
    internal const int SchemaVersion = 1;
    internal const string CacheFileName = "onboarding.v1.digest";
    private const int RepoMapBudget = 1500;

    [McpServerTool(Name = "get_onboarding")]
    [Description("One-call repo orientation for a fresh session: projects, the most important symbols (PageRank-ranked), and where to start — assembled once and cached (keyed to the index build, so it refreshes when the code changes). Prefer this over separate repo_info + repo_map probes when you're new to the repo.")]
    public static string GetOnboarding(ICodeIndexStore index, IFileSystem fileSystem)
    {
        string signature = Signature(index);
        string cachePath = Path.Combine(index.CacheDirectory, CacheFileName);

        string? cached = TryLoad(fileSystem, cachePath, signature);
        if (cached is not null)
        {
            return cached;
        }

        string digest = Build(index);
        TrySave(fileSystem, cachePath, signature, digest);
        return digest;
    }

    // The cache is valid only for the exact index it was built from; the build stamp makes a code change miss.
    internal static string Signature(ICodeIndexStore index)
    {
        BuildInfo b = index.LastBuild;
        return $"v{SchemaVersion}:{index.SourceFileCount}:{index.TypeCount}:{index.MemberCount}:{b.Kind}:{b.WhenUtc:O}";
    }

    private static string Build(ICodeIndexStore index)
    {
        StringBuilder sb = new();
        sb.AppendLine("# Repo onboarding");
        string projectsText = index.TsProjectCount > 0
            ? $"{index.ProjectCount + index.TsProjectCount} projects ({index.ProjectCount} C#, {index.TsProjectCount} TS/SCSS)"
            : $"{index.ProjectCount} projects";
        sb.AppendLine($"{projectsText}, {index.SourceFileCount} files, {index.TypeCount} types, {index.MemberCount} members.");
        sb.AppendLine();

        sb.AppendLine("## Projects");
        foreach (ProjectIndex p in index.ListProjects())
        {
            sb.AppendLine($"  {p.Name} ({p.SourceFiles.Count} files)");
        }

        sb.AppendLine();
        sb.AppendLine("## Most important symbols (PageRank)");
        sb.AppendLine(index.GetRepoMap([], RepoMapBudget, null).TrimEnd());
        sb.AppendLine();
        sb.AppendLine("Next: explain_symbol <name> for a symbol dossier; prepare_change <name> before an edit; search_symbol / search_text to find things.");
        return sb.ToString();
    }

    // Cache is best-effort: any read failure just triggers a regenerate.
    internal static string? TryLoad(IFileSystem fileSystem, string cachePath, string expectedSignature)
    {
        try
        {
            if (!fileSystem.FileExists(cachePath))
            {
                return null;
            }

            string content = fileSystem.ReadAllText(cachePath);
            int nl = content.IndexOf('\n');
            if (nl < 0)
            {
                return null;
            }

            string sig = content[..nl].TrimEnd('\r');
            return sig == expectedSignature ? content[(nl + 1)..] : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Best-effort write (signature on the first line, digest after); a failure just means the next call regenerates.
    private static void TrySave(IFileSystem fileSystem, string cachePath, string signature, string digest)
    {
        try
        {
            AtomicCacheIo io = new(fileSystem);
            byte[] bytes = Encoding.UTF8.GetBytes(signature + "\n" + digest);
            io.WriteAtomic(cachePath, bytes);
        }
        catch (Exception)
        {
            // Non-fatal: the digest was still returned; only the cache write failed.
        }
    }
}
