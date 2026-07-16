using CodeIndex.Abstractions;
using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Models;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Caching;

/// <summary>Defensive/robustness edge coverage: best-effort saves must never throw and reaping must swallow IO errors.</summary>
public sealed class CachingEdgeTests
{
    private static CacheData EmptyCacheData() => new()
    {
        Projects = [],
        SourceFiles = [],
        FileTimestamps = new Dictionary<string, long>(),
        SchemaVersion = 4,
    };

    [Fact]
    public void AtomicCacheIo_ReapStale_MissingDirectory_IsNoOp()
    {
        AtomicCacheIo io = new(new InMemoryFileSystem());

        io.Invoking(x => x.ReapStale(@"C:\nodir", ["*.cache"], @"C:\nodir\current.cache"))
          .Should().NotThrow();
    }

    [Fact]
    public void AtomicCacheIo_ReapStale_DeleteThrows_IsSwallowed()
    {
        IFileSystem fs = Substitute.For<IFileSystem>();
        const string dir = @"C:\cache";
        const string stale = @"C:\cache\index.v3.cache";
        fs.DirectoryExists(dir).Returns(true);
        fs.EnumerateFiles(dir, "index.v*.cache", SearchOption.TopDirectoryOnly).Returns([stale]);
        fs.GetLastWriteTimeUtc(stale).Returns(DateTime.UtcNow.AddHours(-1)); // older than the 30-min window
        fs.When(x => x.DeleteFile(stale)).Do(_ => throw new IOException("locked"));

        AtomicCacheIo io = new(fs);

        io.Invoking(x => x.ReapStale(dir, ["index.v*.cache"], @"C:\cache\index.v4.cache"))
          .Should().NotThrow();
    }

    [Fact]
    public void IndexCache_Save_WhenWriteThrows_DoesNotPropagate()
    {
        IFileSystem fs = Substitute.For<IFileSystem>();
        fs.When(x => x.WriteAllBytes(Arg.Any<string>(), Arg.Any<byte[]>()))
          .Do(_ => throw new IOException("disk full"));

        IndexCache cache = new(fs);

        cache.Invoking(c => c.Save(@"C:\cache", EmptyCacheData())).Should().NotThrow();
    }

    [Fact]
    public void TsIndexCache_Save_WhenWriteThrows_DoesNotPropagate()
    {
        IFileSystem fs = Substitute.For<IFileSystem>();
        fs.When(x => x.WriteAllBytes(Arg.Any<string>(), Arg.Any<byte[]>()))
          .Do(_ => throw new IOException("disk full"));

        TsIndexCache cache = new(fs);

        cache.Invoking(c => c.Save(@"C:\cache", TsSegment.Empty)).Should().NotThrow();
    }

    [Fact]
    public void TsIndexCache_ExposesIndependentSchemaVersion()
    {
        ITsIndexCache cache = new TsIndexCache(new InMemoryFileSystem());

        cache.TsSchemaVersion.Should().Be(1);
        cache.GetTsCachePath(@"C:\cache").Should().EndWith("index.ts.v1.cache");
    }

    [Fact]
    public void IndexCache_ExposesSchemaVersionAndPath()
    {
        ICodeIndexCache cache = new IndexCache(new InMemoryFileSystem());

        cache.CurrentSchemaVersion.Should().Be(5);
        cache.GetCachePath(@"C:\cache").Should().EndWith("index.v5.cache");
    }
}
