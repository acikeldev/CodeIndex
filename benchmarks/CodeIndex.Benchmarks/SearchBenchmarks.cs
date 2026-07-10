using BenchmarkDotNet.Attributes;
using CodeIndex.Benchmarks.Infrastructure;
using CodeIndex.Caching;
using CodeIndex.Indexing;
using CodeIndex.Internal;
using CodeIndex.Models;

namespace CodeIndex.Benchmarks;

/// <summary>
/// Measures the query paths in CodeIndexStore after a full index is built.
/// These methods run on every MCP tool call, so allocation and throughput
/// are both critical.
///
/// FileCount controls index size (total types/members to scan).
/// Term controls result-set size: broad ("Type0") matches many, narrow ("Xyz") matches none.
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
[MinIterationCount(20)]
[MaxIterationCount(100)]
public class SearchBenchmarks
{
    private const string RepoRoot = @"C:\Repo";

    private CodeIndexStore _store = null!;

    [Params(100, 500, 2000)]
    public int FileCount { get; set; }

    // "Type0" matches ~every type; "Xyz" matches nothing; "Method3" is mid-selectivity.
    [Params("Type0", "Method3", "Xyz")]
    public string Term { get; set; } = "Type0";

    [GlobalSetup]
    public void Setup()
    {
        InMemoryFileSystem fs = new();
        CsSourceGenerator.Populate(fs, RepoRoot, FileCount, seed: 42);
        _store = new CodeIndexStore(fs, new IndexCache(fs), new TsIndexCache(fs), CodeIndexConfig.Default);
        _store.Build(RepoRoot);
    }

    [Benchmark(Description = "SearchSymbol — substring scan")]
    public List<SymbolSearchResult> SearchSymbol() => _store.SearchSymbol(Term, null, null);

    [Benchmark(Description = "SearchSymbol — with kind filter (class)")]
    public List<SymbolSearchResult> SearchSymbolClassFilter() => _store.SearchSymbol(Term, "class", null);

    [Benchmark(Description = "SearchSymbol — with kind filter (method)")]
    public List<SymbolSearchResult> SearchSymbolMethodFilter() => _store.SearchSymbol(Term, "method", null);

    [Benchmark(Description = "GetFileOutline — file lookup")]
    public SourceFileIndex? GetFileOutline() => _store.GetFileOutline("File0");

    [Benchmark(Description = "AllSourceFiles — full enumeration")]
    public IReadOnlyList<SourceFileIndex> AllSourceFiles() => _store.AllSourceFiles;
}
