using BenchmarkDotNet.Attributes;
using CodeIndex.Benchmarks.Infrastructure;
using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Models;

namespace CodeIndex.Benchmarks;

/// <summary>
/// Measures MessagePackIndexCache.Save() and TryLoad() independently.
///
/// Save() is on the write path after every Rebuild; TryLoad() is on the
/// startup hot path. Both are LZ4-compressed MessagePack.
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

    private IReadOnlyList<ProjectIndex> _projects = null!;

    // Separate FS for save (write target) vs load (pre-populated)
    private InMemoryFileSystem _saveFs = null!;
    private InMemoryFileSystem _loadFs = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Build a real index to get realistic project data.
        InMemoryFileSystem buildFs = new();
        CsSourceGenerator.Populate(buildFs, RepoRoot, FileCount, seed: 42);
        CodeIndexStore store = new(buildFs);
        store.Rebuild(RepoRoot);
        _projects = store.GetProjects();

        // Save FS: empty, used as write target in SaveCache benchmark.
        _saveFs = new InMemoryFileSystem();

        // Load FS: pre-populated with the serialized cache.
        _loadFs = new InMemoryFileSystem();
        MessagePackIndexCache seedCache = new(RepoRoot, _loadFs);
        seedCache.Save(_projects);
    }

    [Benchmark(Description = "Cache Save — serialize + LZ4 compress")]
    public void SaveCache()
    {
        // Re-create the FS each time so the write path is exercised cleanly.
        // The cost of `new InMemoryFileSystem()` is negligible (~ns) vs serialize (µs–ms).
        _saveFs = new InMemoryFileSystem();
        MessagePackIndexCache cache = new(RepoRoot, _saveFs);
        cache.Save(_projects);
    }

    [Benchmark(Description = "Cache Load — LZ4 decompress + deserialize")]
    public IReadOnlyList<ProjectIndex>? LoadCache()
    {
        MessagePackIndexCache cache = new(RepoRoot, _loadFs);
        return cache.TryLoad();
    }
}
