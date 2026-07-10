using System.Diagnostics.CodeAnalysis;
using CodeIndex.Abstractions;
using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

[ExcludeFromCodeCoverage]
internal static class Program
{
    private static async Task Main(string[] args)
    {
        string repoRoot = ResolveRepoRoot(args);

        IFileSystem fileSystem = new FileSystem();

        // Optional per-repo config (codeindex.json). Generated-file globs are a process-wide static, so apply them
        // before any indexing; the rest of the config flows through the store.
        CodeIndexConfig config = CodeIndexConfig.Load(repoRoot, fileSystem);
        if (config.GeneratedGlobs is { Count: > 0 })
        {
            GeneratedFileClassifier.UseGlobs(config.GeneratedGlobs);
        }

        ICodeIndexCache csCache = new IndexCache(fileSystem);
        ITsIndexCache tsCache = new TsIndexCache(fileSystem);
        CodeIndexStore index = new(fileSystem, csCache, tsCache, config);

        // Non-blocking startup: publish straight from the on-disk cache so the MCP handshake + first tool calls are
        // served immediately. Only a cold start (no cache) builds synchronously — better to block once than serve an
        // empty index. A build failure must NOT kill the server.
        if (!index.LoadCachedSnapshot(repoRoot))
        {
            try
            {
                index.Build(repoRoot);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[CodeIndex] Initial build failed ({ex.GetType().Name}: {ex.Message}) — starting with an empty index.");
            }
        }

        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        // stdout is reserved for the MCP JSON-RPC transport — any stray console output breaks the protocol.
        builder.Logging.ClearProviders();

        // Registered so the MCP SDK can inject them into tool methods: IFileSystem (the source-reading tools —
        // get_symbol_source / get_context_bundle / find_references / search_text) and ICodeIndexCache (index_stats).
        builder.Services.AddSingleton<IFileSystem>(fileSystem);
        builder.Services.AddSingleton<ICodeIndexCache>(csCache);
        builder.Services.AddSingleton<ICodeIndexStore>(index);

        // Keep the index LIVE: watch .git/HEAD (branch switch) + source files -> debounced delta rebuilds, and kick
        // the initial background TS/SCSS build post-serve (gated on config.IndexTypeScript) so it adds zero startup latency.
        builder.Services.AddHostedService(_ => new RepositoryWatcher(index, repoRoot, config.IndexTypeScript));

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        await builder.Build().RunAsync();
    }

    private static string ResolveRepoRoot(string[] args)
    {
        // 1. Explicit --root <path> CLI arg (highest priority) — lets a client point the server at any repo.
        for (int i = 0; i < args.Length - 1; i++)
        {
            if ((args[i] == "--root" || args[i] == "-r") && Directory.Exists(args[i + 1]))
            {
                return Path.GetFullPath(args[i + 1]);
            }
        }

        // 2. Explicit env var.
        string? envRoot = Environment.GetEnvironmentVariable("CODEINDEX_ROOT") ?? Environment.GetEnvironmentVariable("REPO_ROOT");
        if (!string.IsNullOrEmpty(envRoot) && Directory.Exists(envRoot))
        {
            return Path.GetFullPath(envRoot);
        }

        // 3. Walk up from the current directory to a repo root.
        string? fromCwd = WalkUpForRepoRoot(Directory.GetCurrentDirectory());
        if (fromCwd is not null)
        {
            return fromCwd;
        }

        // 4. Walk up from the assembly location (for dotnet run).
        string? fromAssembly = WalkUpForRepoRoot(Path.GetDirectoryName(typeof(CodeIndexStore).Assembly.Location));
        if (fromAssembly is not null)
        {
            return fromAssembly;
        }

        throw new InvalidOperationException(
            "Could not resolve repo root. Pass --root <path>, set CODEINDEX_ROOT, or run from within the repository.");
    }

    // A repo root has a .git — a DIRECTORY in a normal clone, or a FILE in a git worktree/submodule.
    private static string? WalkUpForRepoRoot(string? dir)
    {
        while (dir is not null)
        {
            string git = Path.Combine(dir, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }
}
