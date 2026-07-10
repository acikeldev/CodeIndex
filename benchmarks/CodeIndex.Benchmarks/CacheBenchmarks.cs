using BenchmarkDotNet.Attributes;
using CodeIndex.Benchmarks.Infrastructure;
using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Models;

namespace CodeIndex.Benchmarks;

/// <summary>
/// Measures IndexCache.Save() and Load() independently.
///
/// Save() is on the write path after every rebuild; Load() is on the startup hot path. Both round-trip a
/// MessagePack-serialized <see cref="CacheData"/> through the (in-memory) file system.
///
/// FileCount controls the size of the serialized payload.
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
[MinIterationCount(20)]
[MaxIterationCount(100)]
public class CacheBenchmarks
{
    private const string RepoRoot = @"C:\Repo";

    [Params(50, 500, 2000)]
    public int FileCount { get; set; }

    // The fully-populated cache payload (projects + source files + timestamps) driving both benchmarks.
    private CacheData _data = null!;
    private string _cacheDir = null!;

    // Separate FS for save (write target) vs load (pre-populated).
    private InMemoryFileSystem _saveFs = null!;
    private InMemoryFileSystem _loadFs = null!;

    [GlobalSetup]
    public void Setup()
    {
        _cacheDir = Path.Combine(RepoRoot, ".codeindex");

        // Build a real index so the cache payload is realistic, then read the persisted cache back as a
        // fully-populated CacheData to drive both the save and load benchmarks.
        InMemoryFileSystem buildFs = new();
        CsSourceGenerator.Populate(buildFs, RepoRoot, FileCount, seed: 42);

        IndexCache buildCache = new(buildFs);
        CodeIndexStore store = new(buildFs, buildCache, new TsIndexCache(buildFs), CodeIndexConfig.Default);
        store.Build(RepoRoot);

        CacheData? built = buildCache.Load(_cacheDir);
        _data = built ?? throw new InvalidOperationException("Benchmark setup failed to produce a cache payload.");

        // Save FS: empty, used as the write target in the SaveCache benchmark.
        _saveFs = new InMemoryFileSystem();

        // Load FS: pre-populated with the serialized cache.
        _loadFs = new InMemoryFileSystem();
        IndexCache seedCache = new(_loadFs);
        seedCache.Save(_cacheDir, _data);
    }

    [Benchmark(Description = "Cache Save — serialize + write")]
    public void SaveCache()
    {
        // Re-create the FS each time so the write path is exercised cleanly.
        // The cost of `new InMemoryFileSystem()` is negligible (~ns) vs serialize (us-ms).
        _saveFs = new InMemoryFileSystem();
        IndexCache cache = new(_saveFs);
        cache.Save(_cacheDir, _data);
    }

    [Benchmark(Description = "Cache Load — read + deserialize")]
    public CacheData? LoadCache()
    {
        IndexCache cache = new(_loadFs);
        return cache.Load(_cacheDir);
    }
}
