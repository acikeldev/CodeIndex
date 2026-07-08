using BenchmarkDotNet.Attributes;
using CodeIndex.Benchmarks.Infrastructure;
using CodeIndex.Indexing;
using CodeIndex.Models;

namespace CodeIndex.Benchmarks;

/// <summary>
/// Measures the LINQ search paths in CodeIndexStore after a full index is built.
/// These methods run on every MCP tool call, so allocation and throughput
/// are both critical.
///
/// FileCount controls index size (total types/members to scan).
/// Term controls result-set size: broad ("Type") matches many, narrow ("Xyz") matches none.
/// </summary>
[MemoryDiagnoser]
[SimpleJob]
[MinIterationCount(20)]
[MaxIterationCount(100)]
public class SearchBenchmarks
{
    private CodeIndexStore _store = null!;

    [Params(100, 500, 2000)]
    public int FileCount { get; set; }

    // "Type0" matches ~every type; "Xyz" matches nothing; "Method3" is mid-selectivity
    [Params("Type0", "Method3", "Xyz")]
    public string Term { get; set; } = "Type0";

    [GlobalSetup]
    public void Setup()
    {
        InMemoryFileSystem fs = new();
        CsSourceGenerator.Populate(fs, @"C:\Repo", FileCount, seed: 42);
        _store = new CodeIndexStore(fs);
        _store.Rebuild(@"C:\Repo");
    }

    [Benchmark(Description = "SearchTypes — substring scan")]
    public IReadOnlyList<TypeInfo> SearchTypes() => _store.SearchTypes(Term);

    [Benchmark(Description = "SearchTypes — with kind filter")]
    public IReadOnlyList<TypeInfo> SearchTypesFiltered() =>
        _store.SearchTypes(Term, SymbolKind.Class);

    [Benchmark(Description = "SearchMembers — substring scan")]
    public IReadOnlyList<MemberInfo> SearchMembers() => _store.SearchMembers(Term);

    [Benchmark(Description = "SearchFiles — path fragment")]
    public IReadOnlyList<SourceFileIndex> SearchFiles() => _store.SearchFiles("File0");

    [Benchmark(Description = "GetFiles — full enumeration")]
    public IReadOnlyList<SourceFileIndex> GetFiles() => _store.GetFiles();
}
