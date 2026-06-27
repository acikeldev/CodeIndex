using System.Diagnostics.CodeAnalysis;
using CodeIndex.Abstractions;
using CodeIndex.Indexing;
using CodeIndex.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

[ExcludeFromCodeCoverage]
internal static partial class Program
{
    private static async Task Main(string[] args)
    {
        string repoRoot = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();

        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddSingleton<IFileSystem, FileSystem>();
        builder.Services.AddSingleton<ICodeIndexStore>(sp =>
        {
            CodeIndexStore store = new(sp.GetRequiredService<IFileSystem>());
            store.Rebuild(repoRoot);
            return store;
        });

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
