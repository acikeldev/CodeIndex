using BenchmarkDotNet.Attributes;
using CodeIndex.Benchmarks.Infrastructure;
using CodeIndex.Caching;
using CodeIndex.Indexing;

namespace CodeIndex.Benchmarks;

/// <summary>
/// Measures CodeIndexStore.Rebuild() — the full indexing pipeline from
/// solution discovery through Roslyn parsing to in-memory population.
///
/// Three scenarios:
///   Cold  — no cache, parse every file from scratch.
///   Warm  — cache present, all timestamps match → zero re-parsing.
///   Delta — cache present, 5 % of files have a newer timestamp → only those re-parsed.
///
/// FileCount controls the total number of .cs files in the project.
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
[MinIterationCount(5)]
[MaxIterationCount(30)]
public class RebuildBenchmarks
{
    private const string RepoRoot = @"C:\Repo";
    private const double DeltaFraction = 0.05;

    [Params(50, 200, 1000)]
    public int FileCount { get; set; }

    // ── Cold rebuild ──────────────────────────────────────────────────────────

    private InMemoryFileSystem _coldFs = null!;

    [GlobalSetup(Target = nameof(ColdRebuild))]
    public void SetupCold()
    {
        _coldFs = new InMemoryFileSystem();
        CsSourceGenerator.Populate(_coldFs, RepoRoot, FileCount, seed: 42);
    }

    [Benchmark(Description = "Rebuild — cold (no cache)")]
    public void ColdRebuild()
    {
        CodeIndexStore store = new(_coldFs);
        store.Rebuild(RepoRoot);
    }

    // ── Warm rebuild (cache hit) ──────────────────────────────────────────────

    private InMemoryFileSystem _warmFs = null!;
    private InMemoryFileSystem _warmCacheFs = null!;

    [GlobalSetup(Target = nameof(WarmRebuild))]
    public void SetupWarm()
    {
        _warmFs = new InMemoryFileSystem();
        CsSourceGenerator.Populate(_warmFs, RepoRoot, FileCount, seed: 42);

        // Build once and save the cache into a separate FS so we can
        // re-use the same cache bytes on every benchmark iteration.
        _warmCacheFs = new InMemoryFileSystem();
        CsSourceGenerator.Populate(_warmCacheFs, RepoRoot, FileCount, seed: 42);

        MessagePackIndexCache cache = new(RepoRoot, _warmCacheFs);
        CodeIndexStore seedStore = new(_warmCacheFs, cache);
        seedStore.Rebuild(RepoRoot);
        // cache.bin is now in _warmCacheFs; copy bytes into _warmFs
        string cachePath = System.IO.Path.Combine(RepoRoot, ".codeindex", "cache.bin");
        byte[] bytes = _warmCacheFs.ReadAllBytes(cachePath);
        _warmFs.WriteAllBytes(cachePath, bytes);
    }

    [Benchmark(Description = "Rebuild — warm (all files cached, 0 re-parses)")]
    public void WarmRebuild()
    {
        MessagePackIndexCache cache = new(RepoRoot, _warmFs);
        CodeIndexStore store = new(_warmFs, cache);
        store.Rebuild(RepoRoot);
    }

    // ── Delta rebuild (5 % files changed) ────────────────────────────────────

    private InMemoryFileSystem _deltaFs = null!;

    [GlobalSetup(Target = nameof(DeltaRebuild))]
    public void SetupDelta()
    {
        _deltaFs = new InMemoryFileSystem();
        CsSourceGenerator.Populate(_deltaFs, RepoRoot, FileCount, seed: 42);

        // Seed the cache from a full build.
        MessagePackIndexCache cache = new(RepoRoot, _deltaFs);
        CodeIndexStore seedStore = new(_deltaFs, cache);
        seedStore.Rebuild(RepoRoot);

        // Bump timestamps on DeltaFraction of the files to simulate changes.
        int deltaCount = Math.Max(1, (int)(FileCount * DeltaFraction));
        DateTime later = DateTime.UtcNow.AddSeconds(1);
        for (int i = 0; i < deltaCount; i++)
        {
            string filePath = System.IO.Path.Combine(RepoRoot, "App", $"File{i:D4}.cs");
            // Re-add with a newer timestamp
            string source = CsSourceGenerator.GenerateCsFile(new Random(i + 999), $"File{i:D4}Delta", i);
            _deltaFs.AddFile(filePath, source, later);
        }
    }

    [Benchmark(Description = "Rebuild — delta (5 % files changed)")]
    public void DeltaRebuild()
    {
        MessagePackIndexCache cache = new(RepoRoot, _deltaFs);
        CodeIndexStore store = new(_deltaFs, cache);
        store.Rebuild(RepoRoot);
    }
}
