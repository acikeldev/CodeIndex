using System.Diagnostics.CodeAnalysis;
using CodeIndex.Abstractions;
using CodeIndex.Indexing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

[ExcludeFromCodeCoverage]
internal static class Program
{
    private static async Task Main(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        // stdout is reserved for the MCP JSON-RPC transport — any stray console output breaks the protocol.
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton<IFileSystem, FileSystem>();

        // Engine bring-up: serve a safe empty index until the real snapshot-swap store is wired in (later wave).
        builder.Services.AddSingleton<ICodeIndexStore, EmptyCodeIndexStore>();

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport();

        await builder.Build().RunAsync();
    }
}
