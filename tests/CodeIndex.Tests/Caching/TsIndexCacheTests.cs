using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Models;
using CodeIndex.Tests.Infrastructure;
using MessagePack;

namespace CodeIndex.Tests.Caching;

/// <summary>Phase 2: the independent TS cache segment (index.ts.v{N}.cache) — round-trip, version isolation, and
/// the both-directions cleanup that must never reap the OTHER segment's live cache. Driven against the in-memory
/// file system so the 30-minute stale-cache reclaim window is exercised deterministically.</summary>
public class TsIndexCacheTests
{
    private const string Dir = @"C:\cache\.codeindex";

    private readonly InMemoryFileSystem _fs = new();
    private readonly TsIndexCache _tsCache;
    private readonly IndexCache _csCache;

    public TsIndexCacheTests()
    {
        _tsCache = new TsIndexCache(_fs);
        _csCache = new IndexCache(_fs);
    }

    private static TsSegment SampleSegment()
    {
        SourceFileIndex file = new()
        {
            FileName = "Store.ts",
            SourceFilePath = @"C:\repo\web\Store.ts",
            ProjectName = "myapp-web",
            Language = Language.TypeScript,
            Types = [new TypeInfo { Name = "TsStore", Kind = SymbolKind.Class, TypeKeyword = "class", StartLine = 1, LineCount = 3 }],
        };
        Dictionary<string, long> timestamps = new(StringComparer.OrdinalIgnoreCase) { [file.SourceFilePath] = 123456 };
        List<TsProjectInfo> projects = new() { new() { Name = "myapp-web", ProjectDirPath = @"C:\repo\web" } };
        AliasMap aliases = new();
        aliases.Packages["myapp-web"] = @"C:\repo\web";
        return new TsSegment([file], timestamps, projects, aliases);
    }

    /// <summary>Age a file past the reclaim window via the in-memory mtime seam.</summary>
    private void Touch(string path, TimeSpan age) =>
        _fs.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);

    [Fact]
    public void RoundTrip_PreservesSegment()
    {
        _tsCache.Save(Dir, SampleSegment());
        TsSegment? loaded = _tsCache.Load(Dir);

        loaded.Should().NotBeNull();
        loaded!.Files.Should().ContainSingle(f => f.FileName == "Store.ts" && f.Language == Language.TypeScript);
        loaded.Files[0].Types[0].Name.Should().Be("TsStore");
        loaded.Projects.Should().ContainSingle(p => p.Name == "myapp-web");
        // Case-insensitive timestamp key survives the round-trip.
        loaded.Timestamps.ContainsKey(@"c:\repo\web\store.ts").Should().BeTrue();
        loaded.Aliases.Packages.ContainsKey("myapp-web").Should().BeTrue();
    }

    [Fact]
    public void Load_Missing_ReturnsNull()
    {
        _tsCache.Load(Dir).Should().BeNull();
    }

    [Fact]
    public void Load_Corrupt_ReturnsNull()
    {
        // Garbage bytes at the cache path → the MessagePack deserialize throws and Load swallows it into null.
        _fs.WriteAllBytes(_tsCache.GetTsCachePath(Dir), [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02]);

        _tsCache.Load(Dir).Should().BeNull();
    }

    [Fact]
    public void Load_VersionMismatch_ReturnsNull()
    {
        // Write a cache stamped with a future schema version → must be discarded on load.
        TsIndexCache.TsCacheData data = new()
        {
            SourceFiles = [],
            FileTimestamps = new Dictionary<string, long>(),
            TsProjects = [],
            Aliases = AliasMap.Empty,
            SchemaVersion = TsIndexCache.SchemaVersion + 999,
        };
        _fs.WriteAllBytes(_tsCache.GetTsCachePath(Dir), MessagePackSerializer.Serialize(data));

        _tsCache.Load(Dir).Should().BeNull();
    }

    [Fact]
    public void CsSave_DoesNotReapLiveTsCache()
    {
        // A stale TS cache (older than the reclaim window) must survive a C# save — the C# cleanup glob no longer
        // matches index.ts.*. A stale superseded C# cache IS reaped.
        string tsCache = _tsCache.GetTsCachePath(Dir);
        _fs.WriteAllText(tsCache, "ts");
        Touch(tsCache, TimeSpan.FromMinutes(31));

        string oldCs = Path.Combine(Dir, "index.v3.cache");
        _fs.WriteAllText(oldCs, "old");
        Touch(oldCs, TimeSpan.FromMinutes(31));

        _csCache.Save(Dir, EmptyCsCache());

        _fs.FileExists(tsCache).Should().BeTrue("the TS cache must not be reaped by a C# save");
        _fs.FileExists(oldCs).Should().BeFalse("a stale superseded C# cache should be reaped");
    }

    [Fact]
    public void CsSave_ReapsLegacyUnversionedCache()
    {
        string legacy = Path.Combine(Dir, "index.cache");
        _fs.WriteAllText(legacy, "legacy");
        Touch(legacy, TimeSpan.FromMinutes(31));

        _csCache.Save(Dir, EmptyCsCache());

        _fs.FileExists(legacy).Should().BeFalse("the legacy unversioned index.cache should still be reaped");
    }

    [Fact]
    public void TsSave_DoesNotReapLiveCsCache()
    {
        // Mirror image: a TS save must leave the C# caches alone, while reaping a stale superseded TS cache.
        string csCache = _csCache.GetCachePath(Dir);
        _fs.WriteAllText(csCache, "cs");
        Touch(csCache, TimeSpan.FromMinutes(31));

        string oldTs = Path.Combine(Dir, "index.ts.v0.cache");
        _fs.WriteAllText(oldTs, "oldts");
        Touch(oldTs, TimeSpan.FromMinutes(31));

        _tsCache.Save(Dir, SampleSegment());

        _fs.FileExists(csCache).Should().BeTrue("the C# cache must not be reaped by a TS save");
        _fs.FileExists(oldTs).Should().BeFalse("a stale superseded TS cache should be reaped");
    }

    private static CacheData EmptyCsCache() => new()
    {
        Projects = [],
        SourceFiles = [],
        FileTimestamps = new Dictionary<string, long>(),
        SchemaVersion = IndexCache.SchemaVersion,
    };
}
