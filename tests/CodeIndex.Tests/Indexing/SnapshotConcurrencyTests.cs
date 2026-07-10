using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Models;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Indexing;

/// <summary>Wave 1 snapshot-swap: lock-free read consistency during rebuilds, snapshot immutability, and
/// delta copy-forward reuse. These are the regression guards for removing the coarse read/rebuild lock.</summary>
public sealed class SnapshotConcurrencyTests
{
    private readonly InMemoryFileSystem _fs = new();
    private readonly string _root = @"C:\repo";

    public SnapshotConcurrencyTests()
    {
        _fs.AddFile(@"C:\repo\Fixture.slnx",
            "<Solution>\n  <Project Path=\"Proj/Proj.csproj\" />\n</Solution>\n");
        _fs.AddFile(@"C:\repo\Proj\Proj.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n");
    }

    private string Add(string name, string content)
    {
        string path = Path.Combine(_root, "Proj", name);
        _fs.AddFile(path, content);
        return path;
    }

    private CodeIndexStore Build()
    {
        CodeIndexStore store = new(_fs, new IndexCache(_fs), new TsIndexCache(_fs), CodeIndexConfig.Default);
        store.Build(_root);
        return store;
    }

    [Fact]
    public async Task Reads_AreConsistent_DuringConcurrentRebuild()
    {
        Add("A.cs", "namespace N; public class A { public void MA() { } }");
        string bPath = Add("B.cs", "namespace N; public class B { public void MB() { } }");
        CodeIndexStore store = Build();

        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(600));
        Exception? failure = null;

        Task rebuilder = Task.Run(() =>
        {
            long tick = 0;
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    // Bump B's mtime so each Rebuild does a real delta (reparse B, copy A forward).
                    _fs.SetLastWriteTimeUtc(bPath, new DateTime(2020, 1, 1).AddSeconds(tick++));
                    store.Build(_root);
                }
                catch (Exception ex)
                {
                    failure ??= ex;
                    return;
                }
            }
        });

        Task[] readers = Enumerable.Range(0, 4).Select(n => Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    foreach (SymbolSearchResult r in store.SearchSymbol("M", null, null))
                    {
                        r.SourceFilePath.Should().NotBeNull();
                    }

                    foreach (SourceFileIndex f in store.AllSourceFiles)
                    {
                        _ = f.Types.Count;
                    }

                    _ = store.GetFileOutline("A.cs");
                }
                catch (Exception ex)
                {
                    failure ??= ex;
                    return;
                }
            }
        })).ToArray();

        await Task.WhenAll([rebuilder, .. readers]);
        failure.Should().BeNull(); // no "Collection was modified", no NRE — reads stayed consistent lock-free
    }

    [Fact]
    public void AllSourceFiles_IsStableSnapshot_UnaffectedByLaterRebuild()
    {
        Add("A.cs", "namespace N; public class A { }");
        CodeIndexStore store = Build();

        IReadOnlyList<SourceFileIndex> captured = store.AllSourceFiles;
        int before = captured.Count;

        Add("C.cs", "namespace N; public class C { }");
        store.Build(_root);

        captured.Count.Should().Be(before);                 // old snapshot frozen
        store.AllSourceFiles.Count.Should().BeGreaterThan(before); // new snapshot grew
    }

    [Fact]
    public void Delta_ReusesPreviousSnapshotParsedFiles()
    {
        Add("A.cs", "namespace N; public class A { }");
        string bPath = Add("B.cs", "namespace N; public class B { }");
        CodeIndexStore store = Build();

        SourceFileIndex capturedA = store.GetFileOutline("A.cs")!;
        SourceFileIndex capturedB = store.GetFileOutline("B.cs")!;

        _fs.SetLastWriteTimeUtc(bPath, DateTime.UtcNow.AddMinutes(5));
        store.Build(_root);

        store.GetFileOutline("A.cs").Should().BeSameAs(capturedA);     // survivor reused, not reparsed
        store.GetFileOutline("B.cs").Should().NotBeSameAs(capturedB);  // changed file reparsed
    }

    [Fact]
    public void Delta_RemovedFile_DropsFromNewSnapshot_ButOldSnapshotUnchanged()
    {
        Add("A.cs", "namespace N; public class A { }");
        string bPath = Add("B.cs", "namespace N; public class B { }");
        CodeIndexStore store = Build();

        IReadOnlyList<SourceFileIndex> old = store.AllSourceFiles;
        int before = old.Count;

        _fs.DeleteFile(bPath);
        store.Build(_root);

        store.GetFileOutline("B.cs").Should().BeNull();                 // gone from the new snapshot
        old.Count.Should().Be(before);                                  // old snapshot still intact
        old.Should().Contain(f => f.FileName == "B.cs");
    }

    [Fact]
    public void NoOpRebuild_KeepsSameSnapshotInstance()
    {
        Add("A.cs", "namespace N; public class A { }");
        CodeIndexStore store = Build();

        IReadOnlyList<SourceFileIndex> a = store.AllSourceFiles;
        store.Build(_root); // no file changes → keep the same snapshot, no swap
        IReadOnlyList<SourceFileIndex> b = store.AllSourceFiles;

        b.Should().BeSameAs(a);
    }

    [Fact]
    public void CountsMatchAggregateAfterDelta()
    {
        Add("A.cs", "namespace N; public class A { public void One() { } }");
        string bPath = Add("B.cs", "namespace N; public class B { public int P { get; set; } }");
        CodeIndexStore store = Build();

        _fs.SetLastWriteTimeUtc(bPath, DateTime.UtcNow.AddMinutes(5));
        store.Build(_root);

        int typeSum = store.AllSourceFiles.Sum(f => f.Types.Count);
        int memberSum = store.AllSourceFiles.Sum(f => f.Types.Sum(t => t.Members.Count));
        store.TypeCount.Should().Be(typeSum);
        store.MemberCount.Should().Be(memberSum);
    }
}
