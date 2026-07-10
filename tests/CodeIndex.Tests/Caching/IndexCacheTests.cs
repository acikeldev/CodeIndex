using CodeIndex.Caching;
using CodeIndex.Models;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Caching;

public class IndexCacheTests
{
    private readonly InMemoryFileSystem _fs = new();
    private readonly IndexCache _cache;
    private const string CacheDir = @"C:\Repo\.codeindex";

    public IndexCacheTests()
    {
        _cache = new IndexCache(_fs);
    }

    private string CachePath => _cache.GetCachePath(CacheDir);

    [Fact]
    public void Load_ReturnsNullWhenFileMissing()
    {
        _cache.Load(CacheDir).Should().BeNull();
    }

    [Fact]
    public void SaveThenLoad_RoundTripsWithCurrentVersion()
    {
        CacheData saved = MakeCache(_cache.CurrentSchemaVersion);
        _cache.Save(CacheDir, saved);

        CacheData? loaded = _cache.Load(CacheDir);

        loaded.Should().NotBeNull();
        loaded!.SchemaVersion.Should().Be(_cache.CurrentSchemaVersion);
        loaded.Projects.Should().ContainSingle();
    }

    [Fact]
    public void GetCachePath_EncodesSchemaVersionInFilename()
    {
        _cache.GetCachePath(CacheDir)
            .Should().Be(Path.Combine(CacheDir, $"index.v{_cache.CurrentSchemaVersion}.cache"));
    }

    [Fact]
    public void Load_DiscardsCacheWithMismatchedSchemaVersion()
    {
        CacheData stale = MakeCache(schemaVersion: _cache.CurrentSchemaVersion - 1);
        _cache.Save(CacheDir, stale);

        CacheData? loaded = _cache.Load(CacheDir);

        loaded.Should().BeNull();
    }

    [Fact]
    public void Load_ReturnsNullOnCorruptCache()
    {
        _fs.AddBinaryFile(CachePath, [0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);

        CacheData? loaded = _cache.Load(CacheDir);

        loaded.Should().BeNull();
    }

    [Fact]
    public void Load_RejectsLegacyCacheWithoutSchemaVersion()
    {
        // Simulate a cache written before SchemaVersion was added. MessagePack will
        // deserialize SchemaVersion as the default int value (0), which won't match
        // the current version, so Load must reject it.
        CacheData legacyShaped = MakeCache(schemaVersion: 0);
        _cache.Save(CacheDir, legacyShaped);

        CacheData? loaded = _cache.Load(CacheDir);

        loaded.Should().BeNull();
    }

    [Fact]
    public void ComputeDelta_IgnoresPathCasing_NoChurn()
    {
        CacheData cached = new()
        {
            Projects = [],
            SourceFiles = [],
            FileTimestamps = new Dictionary<string, long> { ["C:\\Repo\\Proj\\A.cs"] = 100L, ["C:\\Repo\\Proj\\B.cs"] = 200L },
            SchemaVersion = _cache.CurrentSchemaVersion
        };
        // Same files, different path casing (e.g. tool launched from c:\ vs C:\).
        Dictionary<string, long> current = new(StringComparer.OrdinalIgnoreCase)
        {
            ["c:\\repo\\proj\\a.cs"] = 100L,
            ["c:\\repo\\proj\\b.cs"] = 200L
        };

        IndexCache.DeltaResult delta = IndexCache.ComputeDelta(cached, current);

        delta.IsFullRebuild.Should().BeFalse();
        delta.RemovedFiles.Should().BeEmpty();
        delta.ChangedFiles.Should().BeEmpty();
    }

    [Fact]
    public void ComputeDelta_NullBaseline_IsFullRebuild()
    {
        IndexCache.DeltaResult delta = IndexCache.ComputeDelta((CacheData?)null, new Dictionary<string, long> { ["a.cs"] = 1L });

        delta.IsFullRebuild.Should().BeTrue();
        delta.ChangedFiles.Should().BeEmpty();
        delta.RemovedFiles.Should().BeEmpty();
    }

    [Fact]
    public void ComputeDelta_EmptyBaseline_IsFullRebuild()
    {
        IndexCache.DeltaResult delta = IndexCache.ComputeDelta(
            new Dictionary<string, long>(),
            new Dictionary<string, long> { ["a.cs"] = 1L });

        delta.IsFullRebuild.Should().BeTrue();
    }

    [Fact]
    public void ComputeDelta_DetectsChangedAndRemovedFiles()
    {
        Dictionary<string, long> baseline = new()
        {
            ["A.cs"] = 100L,
            ["B.cs"] = 200L,
            ["Gone.cs"] = 300L
        };
        Dictionary<string, long> current = new()
        {
            ["A.cs"] = 100L,   // unchanged
            ["B.cs"] = 999L,   // changed
            ["New.cs"] = 400L  // added
        };

        IndexCache.DeltaResult delta = IndexCache.ComputeDelta(baseline, current);

        delta.IsFullRebuild.Should().BeFalse();
        delta.ChangedFiles.Should().BeEquivalentTo("B.cs", "New.cs");
        delta.RemovedFiles.Should().BeEquivalentTo("Gone.cs");
    }

    [Fact]
    public void Save_ReclaimsStaleLegacyIndexCache()
    {
        // A pre-versioning unversioned cache that has gone untouched past the reclaim window.
        _fs.AddBinaryFile(Path.Combine(CacheDir, "index.cache"), [1, 2, 3]);
        _fs.SetLastWriteTimeUtc(Path.Combine(CacheDir, "index.cache"), DateTime.UtcNow.AddMinutes(-31));

        _cache.Save(CacheDir, MakeCache(_cache.CurrentSchemaVersion));

        _fs.FileExists(Path.Combine(CacheDir, "index.cache")).Should().BeFalse();
    }

    [Fact]
    public void Save_ReclaimsStaleVersionedCache()
    {
        // An older versioned cache from a superseded tool version, untouched past the reclaim window.
        _fs.AddBinaryFile(Path.Combine(CacheDir, "index.v3.cache"), [1, 2, 3]);
        _fs.SetLastWriteTimeUtc(Path.Combine(CacheDir, "index.v3.cache"), DateTime.UtcNow.AddMinutes(-31));

        _cache.Save(CacheDir, MakeCache(_cache.CurrentSchemaVersion));

        _fs.FileExists(Path.Combine(CacheDir, "index.v3.cache")).Should().BeFalse();
    }

    [Fact]
    public void Save_KeepsRecentlyTouchedVersionedCache()
    {
        // A different live tool version re-saves its own cache each run: a recent mtime means it is
        // still live, so reaping it would thrash both versions into perpetual full rebuilds.
        _fs.AddBinaryFile(Path.Combine(CacheDir, "index.v3.cache"), [1, 2, 3]);
        _fs.SetLastWriteTimeUtc(Path.Combine(CacheDir, "index.v3.cache"), DateTime.UtcNow);

        _cache.Save(CacheDir, MakeCache(_cache.CurrentSchemaVersion));

        _fs.FileExists(Path.Combine(CacheDir, "index.v3.cache")).Should().BeTrue();
    }

    [Fact]
    public void Save_DoesNotReapLiveTsCache()
    {
        // Two-direction reaping guarantee (1/2): a C# Save must never reap the TS segment's cache,
        // even when that TS cache is old enough to be reclaimed — the TS segment owns its own cleanup.
        string tsCache = Path.Combine(CacheDir, "index.ts.v2.cache");
        _fs.AddBinaryFile(tsCache, [9, 9, 9]);
        _fs.SetLastWriteTimeUtc(tsCache, DateTime.UtcNow.AddMinutes(-31));

        _cache.Save(CacheDir, MakeCache(_cache.CurrentSchemaVersion));

        _fs.FileExists(tsCache).Should().BeTrue();
    }

    [Fact]
    public void TsReap_DoesNotReapLiveCsCache()
    {
        // Two-direction reaping guarantee (2/2): a TS-segment reap (its own globs) must never reap the
        // live C# cache, even when that C# cache is old enough to be reclaimed.
        _cache.Save(CacheDir, MakeCache(_cache.CurrentSchemaVersion));
        _fs.SetLastWriteTimeUtc(CachePath, DateTime.UtcNow.AddMinutes(-31));

        AtomicCacheIo tsIo = new(_fs);
        tsIo.ReapStale(CacheDir, ["index.ts.v*.cache", "index.ts.cache"], Path.Combine(CacheDir, "index.ts.v2.cache"));

        _fs.FileExists(CachePath).Should().BeTrue();
    }

    private static CacheData MakeCache(int schemaVersion)
    {
        return new CacheData
        {
            Projects =
            [
                new ProjectIndex
                {
                    Name = "TestProject",
                    ProjectDirPath = "/tmp/test",
                    SourceFiles = []
                }
            ],
            SourceFiles = [],
            FileTimestamps = new Dictionary<string, long>(),
            SchemaVersion = schemaVersion
        };
    }
}
