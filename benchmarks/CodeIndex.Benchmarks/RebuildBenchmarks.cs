using BenchmarkDotNet.Attributes;
using CodeIndex.Benchmarks.Infrastructure;
using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;

namespace CodeIndex.Benchmarks;

/// <summary>
/// Measures CodeIndexStore.Build()/Rebuild() — the full indexing pipeline from
/// solution discovery through Roslyn parsing to in-memory population.
///
/// Three scenarios:
///   Cold  — no cache, parse every file from scratch (forced full rebuild, so every invocation is cold).
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

    private static CodeIndexStore NewStore(InMemoryFileSystem fs) =>
        new(fs, new IndexCache(fs), new TsIndexCache(fs), CodeIndexConfig.Default);

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
        // fullRebuild ignores any cache and reparses every file, so each invocation measures the true cold
        // path (the store persists a cache after the first build; without this every later call would be warm).
        CodeIndexStore store = NewStore(_coldFs);
        store.Rebuild(RepoRoot, fullRebuild: true, CancellationToken.None);
    }

    // ── Warm rebuild (cache hit) ──────────────────────────────────────────────

    private InMemoryFileSystem _warmFs = null!;

    [GlobalSetup(Target = nameof(WarmRebuild))]
    public void SetupWarm()
    {
        _warmFs = new InMemoryFileSystem();
        CsSourceGenerator.Populate(_warmFs, RepoRoot, FileCount, seed: 42);

        // Seed the on-disk cache from a full build over THIS same file system, so the cached timestamps match
        // the current file timestamps exactly and every benchmarked rebuild takes the zero-reparse warm path.
        CodeIndexStore seedStore = NewStore(_warmFs);
        seedStore.Build(RepoRoot);
    }

    [Benchmark(Description = "Rebuild — warm (all files cached, 0 re-parses)")]
    public void WarmRebuild()
    {
        CodeIndexStore store = NewStore(_warmFs);
        store.Build(RepoRoot);
    }

    // ── Delta rebuild (5 % files changed) ────────────────────────────────────

    private InMemoryFileSystem _deltaFs = null!;

    [GlobalSetup(Target = nameof(DeltaRebuild))]
    public void SetupDelta()
    {
        _deltaFs = new InMemoryFileSystem();
        CsSourceGenerator.Populate(_deltaFs, RepoRoot, FileCount, seed: 42);

        // Seed the cache from a full build.
        CodeIndexStore seedStore = NewStore(_deltaFs);
        seedStore.Build(RepoRoot);

        // Bump timestamps on DeltaFraction of the files to simulate changes.
        int deltaCount = Math.Max(1, (int)(FileCount * DeltaFraction));
        DateTime later = DateTime.UtcNow.AddSeconds(1);
        for (int i = 0; i < deltaCount; i++)
        {
            string filePath = Path.Combine(RepoRoot, "App", $"File{i:D4}.cs");
            // Re-add with a newer timestamp.
            string source = CsSourceGenerator.GenerateCsFile(new Random(i + 999), $"File{i:D4}Delta", i);
            _deltaFs.AddFile(filePath, source, later);
        }
    }

    [Benchmark(Description = "Rebuild — delta (5 % files changed)")]
    public void DeltaRebuild()
    {
        CodeIndexStore store = NewStore(_deltaFs);
        store.Build(RepoRoot);
    }
}
