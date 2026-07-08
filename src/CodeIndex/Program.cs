using System.Diagnostics.CodeAnalysis;
using CodeIndex.Abstractions;
using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

[ExcludeFromCodeCoverage]
internal static partial class Program
{
    private static async Task Main(string[] args)
    {
        string repoRoot = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();

        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddSingleton<IFileSystem, FileSystem>();
        builder.Services.AddSingleton<ICodeIndexCache>(sp =>
            new MessagePackIndexCache(repoRoot, sp.GetRequiredService<IFileSystem>()));
        builder.Services.AddSingleton<ICodeIndexStore>(sp =>
        {
            CodeIndexStore store = new(
                sp.GetRequiredService<IFileSystem>(),
                sp.GetRequiredService<ICodeIndexCache>());
            store.Rebuild(repoRoot);
            return store;
        });

        builder.Services.AddSingleton<RepositoryWatcher>(sp => new RepositoryWatcher(
            sp.GetRequiredService<ICodeIndexStore>(),
            repoRoot,
            sp.GetRequiredService<ILogger<RepositoryWatcher>>()));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RepositoryWatcher>());

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<CodeIndexTools>();

        IHost host = builder.Build();

        // Trigger store construction (and initial index build) before accepting requests.
        _ = host.Services.GetRequiredService<ICodeIndexStore>();

        await host.RunAsync();
    }
}
