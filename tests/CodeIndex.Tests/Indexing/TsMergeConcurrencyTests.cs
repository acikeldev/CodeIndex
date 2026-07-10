using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Indexing;

/// <summary>Phase 2 concurrency: interleaved C# and TS rebuilds must never drop a segment (the two single-writer
/// segments each re-compose under _rebuildGate), and lock-free readers must never see a torn/half-merged snapshot.</summary>
public sealed class TsMergeConcurrencyTests
{
    private readonly InMemoryFileSystem _fs = new();
    private readonly string _root = @"C:\repo";

    public TsMergeConcurrencyTests()
    {
        _fs.AddFile(@"C:\repo\Fixture.slnx",
            "<Solution>\n  <Project Path=\"Proj/Proj.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\Proj\Proj.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n");
        _fs.AddFile(@"C:\repo\Proj\A.cs", "namespace N; public class CsThing { public void M() { } }");

        _fs.AddFile(@"C:\repo\web\package.json", """{ "name": "myapp-web" }""");
        _fs.AddFile(@"C:\repo\web\tsconfig.json", "{ }");
        _fs.AddFile(@"C:\repo\web\Store.ts", "export class TsStore { }");
    }

    private CodeIndexStore NewStore() =>
        new(_fs, new IndexCache(_fs), new TsIndexCache(_fs), CodeIndexConfig.Default);

    private static bool Has(CodeIndexStore s, string name) => s.SearchSymbol(name, null, null).Any(r => r.Name == name);

    [Fact]
    public async Task InterleavedCsAndTsRebuilds_NeverDropASegment()
    {
        CodeIndexStore store = NewStore();
        store.Build(_root);
        await store.RefreshTypeScriptAsync(_root, fullRebuild: true, CancellationToken.None);

        // Hammer both rebuild paths concurrently. Each C#/TS rebuild is a no-op (nothing changed) but still
        // exercises the concurrent PublishCs/PublishTs re-compose; neither segment may be lost.
        List<Task> tasks = new();
        for (int i = 0; i < 30; i++)
        {
            tasks.Add(Task.Run(() => store.Rebuild(_root, fullRebuild: false, CancellationToken.None)));
            tasks.Add(store.RefreshTypeScriptAsync(_root, fullRebuild: false, CancellationToken.None));
        }

        await Task.WhenAll(tasks);

        Has(store, "CsThing").Should().BeTrue("C# segment was dropped by an interleaved rebuild");
        Has(store, "TsStore").Should().BeTrue("TS segment was dropped by an interleaved rebuild");
    }

    [Fact]
    public async Task TornReadStress_ReadersAlwaysSeeConsistentSnapshot()
    {
        CodeIndexStore store = NewStore();
        store.Build(_root);
        await store.RefreshTypeScriptAsync(_root, fullRebuild: true, CancellationToken.None);

        using CancellationTokenSource cts = new();
        Exception? readerFault = null;
        Task reader = Task.Run(() =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    // C# is present in every published snapshot (Compose always carries the C# files), so a reader
                    // must always find it — never an exception, never an empty/torn view.
                    Has(store, "CsThing").Should().BeTrue();
                    _ = store.ListProjects();
                }
            }
            catch (Exception ex)
            {
                readerFault = ex;
            }
        });

        List<Task> writers = new();
        for (int i = 0; i < 20; i++)
        {
            writers.Add(Task.Run(() => store.Rebuild(_root, fullRebuild: false, CancellationToken.None)));
            writers.Add(store.RefreshTypeScriptAsync(_root, fullRebuild: false, CancellationToken.None));
        }

        await Task.WhenAll(writers);
        cts.Cancel();
        await reader;

        readerFault.Should().BeNull();
        Has(store, "TsStore").Should().BeTrue();
    }
}
